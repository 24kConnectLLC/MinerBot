using System.Net;
using Discord;
using Discord.Net;
using MinerBot_2._0.Status;

namespace MinerBot.Tests
{
    // How the bot tells a failure in its log, its history and its runs' details: the exception's type, with MariaDB's error
    // number or Discord's numbers, never the message, which can name the login, or carry Discord's own words or a token
    public class FailureLogTests
    {
        const string DiscordsWords = "zz-discords-own-words";

        static HttpException Answer(HttpStatusCode status, DiscordErrorCode? code) => new(status, null!, code, DiscordsWords);

        [Fact]
        public void DescribeNamesDiscordsStatusAndCode()
        {
            Assert.Equal("HttpException, HTTP 403, Discord 50007", FailureLog.Describe(Answer(HttpStatusCode.Forbidden, DiscordErrorCode.CannotSendMessageToUser)));
            Assert.Equal("HttpException, HTTP 404, Discord 10003", FailureLog.Describe(Answer(HttpStatusCode.NotFound, DiscordErrorCode.UnknownChannel)));

            // Wrapped, as a task can hand it on: the outer type, with Discord's numbers from inside, as for MariaDB's
            Assert.Equal("AggregateException, HTTP 403, Discord 50013",
                FailureLog.Describe(new AggregateException(Answer(HttpStatusCode.Forbidden, DiscordErrorCode.InsufficientPermissions))));

            Assert.Equal(50007, FailureLog.DiscordCode(Answer(HttpStatusCode.Forbidden, DiscordErrorCode.CannotSendMessageToUser)));
        }

        // Discord's 5xx and 401 answers carry code 0, its "general error", which says nothing the status doesn't: the code is
        // left out, and the number a request's result gives is the status
        [Fact]
        public void DescribeLeavesOutACodeOfZero()
        {
            Assert.Equal("HttpException, HTTP 500", FailureLog.Describe(Answer(HttpStatusCode.InternalServerError, DiscordErrorCode.GeneralError)));
            Assert.Equal("HttpException, HTTP 401", FailureLog.Describe(Answer(HttpStatusCode.Unauthorized, DiscordErrorCode.GeneralError)));
            Assert.Equal("HttpException, HTTP 502", FailureLog.Describe(Answer(HttpStatusCode.BadGateway, null)));

            Assert.Equal(500, FailureLog.DiscordCode(Answer(HttpStatusCode.InternalServerError, DiscordErrorCode.GeneralError)));
            Assert.Equal(502, FailureLog.DiscordCode(Answer(HttpStatusCode.BadGateway, null)));
        }

        // Discord's message holds its own words, and a request's endpoint can hold a webhook's or an interaction's token
        [Fact]
        public void DescribeNeverHoldsTheMessage()
        {
            Exception[] failures =
            [
                Answer(HttpStatusCode.Forbidden, DiscordErrorCode.CannotSendMessageToUser),
                new RateLimitedException(null!),
                new TimeoutException(DiscordsWords),
                new InvalidOperationException(DiscordsWords, Answer(HttpStatusCode.BadRequest, DiscordErrorCode.InvalidFormBody))
            ];

            Assert.Contains(DiscordsWords, failures[0].Message);
            Assert.All(failures, failure => Assert.DoesNotContain(DiscordsWords, FailureLog.Describe(failure)));
            Assert.Equal(["HttpException, HTTP 403, Discord 50007", "RateLimitedException", "TimeoutException", "InvalidOperationException, HTTP 400, Discord 50035"],
                failures.Select(FailureLog.Describe));
        }
    }
}
