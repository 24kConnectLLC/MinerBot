using System.Net;
using System.Net.Sockets;
using DataAccessLibrary.Posting;
using Discord;
using Discord.Net;
using Discord.Net.Rest;
using MinerBot.Shared.Delivery;
using MinerBot_2._0.Posting;

namespace MinerBot.Tests
{
    // How a post's send that didn't go is read: Discord's answers by its numbers, and what Discord.Net throws without one by
    // what the posts' options make sure of. A TimeoutException comes only before a try, and a connection that never opened
    // never took the request, so nothing was made; a send cancelled at its own limit, a 5xx or a connection lost once the
    // request was under way may have made the message, so it's held in doubt for the look.
    public class DiscordFailuresTests
    {
        public static TheoryData<string, bool, string, string?, int, bool> Posts => new()
        {
            { "time out before a try", false, DeliveryState.Failed, DeliveryReasons.RateLimited, 0, false },
            { "time out before a try", true, DeliveryState.Failed, DeliveryReasons.NotSent, 0, false },
            { "429 that reached the bot", false, DeliveryState.Failed, DeliveryReasons.RateLimited, 429, false },
            { "cancelled", false, DeliveryState.InDoubt, DeliveryReasons.NoAnswer, 0, false },
            { "cancelled", true, DeliveryState.InDoubt, null, 0, false },
            { "503", false, DeliveryState.InDoubt, DeliveryReasons.NoAnswer, 503, false },
            { "name not found", false, DeliveryState.Failed, DeliveryReasons.NotSent, 0, false },
            { "connection refused", false, DeliveryState.Failed, DeliveryReasons.NotSent, 0, false },
            { "TLS failing", false, DeliveryState.Failed, DeliveryReasons.NotSent, 0, false },
            { "connection lost under way", false, DeliveryState.InDoubt, DeliveryReasons.NoAnswer, 0, false },
            { "I/O error", false, DeliveryState.InDoubt, DeliveryReasons.NoAnswer, 0, false },
            { "401", false, DeliveryState.Failed, DeliveryReasons.Unauthorized, 401, false },
            { "403 50013", false, DeliveryState.Failed, DeliveryReasons.DiscordPermissions, 403, true },
            { "anything else", false, DeliveryState.InDoubt, null, 0, false }
        };

        [Theory]
        [MemberData(nameof(Posts))]
        public void PostsFailureIsReadByWhatTheSendsOptionsMakeSureOf(string what, bool stopping, string state, string? reason, int status, bool counts)
        {
            var failure = DiscordFailures.ClassifyPost(Thrown(what), stopping);

            Assert.Equal((state, reason, status == 0 ? null : (ushort?)status, counts), (failure.State, failure.Reason, failure.HttpStatus, failure.Counts));
        }

        // Classify, by which the owner's direct message goes, reads a TimeoutException as Discord not answering, as it reads a
        // send cut off while it was under way; the alert job sets the TimeoutException apart, as a message whose tries were all
        // rate-limited or answered 502 until its time was up, which never went (SubmissionAlertTests)
        [Fact]
        public void OtherSendsTimeoutMayHaveArrived()
        {
            Assert.Equal((DeliveryState.InDoubt, (string?)DeliveryReasons.NoAnswer), Read(DiscordFailures.Classify(new TimeoutException())));
            Assert.Equal((DeliveryState.InDoubt, (string?)DeliveryReasons.NoAnswer), Read(DiscordFailures.Classify(new TaskCanceledException())));
        }

        // What the posts' reading rests on, as Discord.Net's own REST client and .NET give it, against a listener on this
        // machine: a request its token ends before an answer comes is an OperationCanceledException, never a
        // TimeoutException, so a post its limit ended is held in doubt; and a connection refused is an HttpRequestException
        // that says so, before any of the request went, so that post wasn't made
        [Fact]
        public async Task RealRestClientEndsRequestsAsThePostsReadingSays()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = DefaultRestClientProvider.Instance($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");

            try
            {
                var accepted = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));

                var unanswered = await Record.ExceptionAsync(() => client.SendAsync("POST", "channels/1/messages", "{}", limit.Token)
                    .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

                // The listener took the connection, so the request was under way when its token ended it
                Assert.True(accepted.IsCompletedSuccessfully, "The request never reached the listener");
                using var taken = await accepted;
                Assert.IsAssignableFrom<OperationCanceledException>(unanswered);
                Assert.Equal((DeliveryState.InDoubt, (string?)DeliveryReasons.NoAnswer), Read(DiscordFailures.ClassifyPost(unanswered!, stopping: false)));
            }
            finally
            {
                listener.Stop();
            }

            var refused = await Record.ExceptionAsync(() => client.SendAsync("POST", "channels/1/messages", "{}", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

            Assert.Equal(HttpRequestError.ConnectionError, Assert.IsType<HttpRequestException>(refused).HttpRequestError);
            Assert.Equal((DeliveryState.Failed, (string?)DeliveryReasons.NotSent), Read(DiscordFailures.ClassifyPost(refused!, stopping: false)));
        }

        static (string, string?) Read(SendFailure failure) => (failure.State, failure.Reason);

        static Exception Thrown(string what) => what switch
        {
            "time out before a try" => new TimeoutException(),
            "429 that reached the bot" => new RateLimitedException(null!),
            "cancelled" => new TaskCanceledException("Discord didn't answer", new TimeoutException()),
            "503" => Answer(HttpStatusCode.ServiceUnavailable, null),
            "name not found" => new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known"),
            "connection refused" => new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made"),
            "TLS failing" => new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established"),
            "connection lost under way" => new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely"),
            "I/O error" => new IOException(),
            "401" => Answer(HttpStatusCode.Unauthorized, DiscordErrorCode.GeneralError),
            "403 50013" => Answer(HttpStatusCode.Forbidden, DiscordErrorCode.InsufficientPermissions),
            _ => new InvalidOperationException()
        };

        static HttpException Answer(HttpStatusCode status, DiscordErrorCode? code) => new(status, null!, code, FakeDiscord.DiscordText);
    }
}
