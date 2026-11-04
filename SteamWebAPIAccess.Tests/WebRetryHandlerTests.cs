using System.Net;

namespace SteamWebAPIAccess.Tests
{
    // The handler on a fake network. Only the tests where the limit has to fire make it short.
    public class WebRetryHandlerTests
    {
        const string Url = "https://api.example.com/data";

        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);
        internal static readonly TimeSpan ShortLimit = TimeSpan.FromMilliseconds(250);

        static HttpClient Client(FakeWeb web, TimeSpan? limit = null)
        {
            var handler = new WebRetryHandler(web);
            handler.AttemptLimit = limit ?? handler.AttemptLimit;
            return new HttpClient(handler) { Timeout = handler.ClientTimeout };
        }

        // A call that never ends must not pass as one that timed out, so it has to end inside the guard
        internal static async Task<TimeoutException> TimesOutAsync(Task call)
        {
            var finished = await Task.WhenAny(call, Task.Delay(WaitLimit, TestContext.Current.CancellationToken));

            Assert.Same(call, finished);
            return await Assert.ThrowsAsync<TimeoutException>(() => call);
        }

        // So a call that should answer fails the test if it hangs, instead of holding up the whole run
        static Task<T> AnsweredAsync<T>(Task<T> call) => call.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

        [Fact]
        public async Task GetThatGetsAServerErrorIsSentOnceMore()
        {
            var web = new FakeWeb().Answer(HttpStatusCode.ServiceUnavailable).Answer(HttpStatusCode.OK, "fine");
            using var client = Client(web);

            Assert.Equal("fine", await AnsweredAsync(client.GetStringAsync(Url, TestContext.Current.CancellationToken)));
            Assert.Equal(2, web.Requests.Count);
        }

        [Fact]
        public async Task GetThatTimesOutIsSentOnceMore()
        {
            var web = new FakeWeb().Never().Answer(HttpStatusCode.OK, "fine");
            using var client = Client(web, ShortLimit);

            Assert.Equal("fine", await AnsweredAsync(client.GetStringAsync(Url, TestContext.Current.CancellationToken)));
            Assert.Equal(2, web.Requests.Count);
        }

        // So the limit covers the body as well as the headers
        [Fact]
        public async Task GetWhoseBodyStallsIsSentOnceMore()
        {
            var web = new FakeWeb().BodyThatNeverEnds().Answer(HttpStatusCode.OK, "fine");
            using var client = Client(web, ShortLimit);

            Assert.Equal("fine", await AnsweredAsync(client.GetStringAsync(Url, TestContext.Current.CancellationToken)));
            Assert.Equal(2, web.Requests.Count);
        }

        [Fact]
        public async Task GetIsSentNoMoreThanTwice()
        {
            var web = new FakeWeb().Answer(HttpStatusCode.ServiceUnavailable).Answer(HttpStatusCode.ServiceUnavailable).Answer(HttpStatusCode.OK);
            using var client = Client(web);

            using var response = await AnsweredAsync(client.GetAsync(Url, TestContext.Current.CancellationToken));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(2, web.Requests.Count);
        }

        [Fact]
        public async Task GetThatTimesOutTwiceFailsWithATimeout()
        {
            var web = new FakeWeb().Never().Never();
            using var client = Client(web, ShortLimit);

            var timeout = await TimesOutAsync(client.GetAsync(Url, TestContext.Current.CancellationToken));

            Assert.StartsWith("No answer from api.example.com within", timeout.Message);
            Assert.Equal(2, web.Requests.Count);
        }

        [Fact]
        public async Task PostThatTimesOutIsNotSentAgain()
        {
            var web = new FakeWeb().Never().Answer(HttpStatusCode.OK);
            using var client = Client(web, ShortLimit);

            await TimesOutAsync(client.PostAsync(Url, new StringContent("data"), TestContext.Current.CancellationToken));

            Assert.Equal(HttpMethod.Post, Assert.Single(web.Requests).Method);
        }

        [Fact]
        public async Task PostThatGetsAServerErrorIsNotSentAgain()
        {
            var web = new FakeWeb().Answer(HttpStatusCode.ServiceUnavailable).Answer(HttpStatusCode.OK);
            using var client = Client(web);

            using var response = await AnsweredAsync(client.PostAsync(Url, new StringContent("data"), TestContext.Current.CancellationToken));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Single(web.Requests);
        }

        [Fact]
        public async Task ClientErrorIsNotSentAgain()
        {
            var web = new FakeWeb().Answer(HttpStatusCode.NotFound).Answer(HttpStatusCode.OK);
            using var client = Client(web);

            using var response = await AnsweredAsync(client.GetAsync(Url, TestContext.Current.CancellationToken));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Single(web.Requests);
        }

        [Fact]
        public async Task CallerThatGivesUpGetsNoSecondTry()
        {
            var web = new FakeWeb().Never().Answer(HttpStatusCode.OK);
            using var client = Client(web);
            using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            giveUp.CancelAfter(ShortLimit);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync(Url, giveUp.Token));

            Assert.Single(web.Requests);
        }
    }
}
