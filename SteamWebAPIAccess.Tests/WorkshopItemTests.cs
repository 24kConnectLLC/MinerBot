using System.Net;
using Steam.Models;

namespace SteamWebAPIAccess.Tests
{
    // GetWorkshopItem for /maps lookup, on canned replies, and the checks that decide what the bot shows
    public class WorkshopItemTests
    {
        const uint AppId = 274900;
        const ulong MapId = 3776027135;

        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

        readonly FakeWeb web = new();
        readonly WebRetryHandler handler;
        readonly GetData data;

        public WorkshopItemTests()
        {
            // SteamAPIAccess reads this without checking it
            Environment.SetEnvironmentVariable("STEAMAPP_ID", "1");

            handler = new WebRetryHandler(web);
            data = new GetData(new SteamAPIAccess { PublisherWebAPIKey = "key", appID = AppId }, new HttpClient(handler, disposeHandler: false) { Timeout = handler.ClientTimeout }, handler);
            data.Initilization();
        }

        static string Reply(string item) => $$$"""{"response":{"result":1,"resultcount":1,"publishedfiledetails":[{{{item}}}]}}""";

        Task<PublishedFileDetailsModel?> LookUpAsync() => data.GetWorkshopItem(MapId).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

        // Each kind of answer, through SteamWebAPI2's reader and the checks
        [Theory]
        [InlineData("""{"publishedfileid":"3776027135","result":1,"consumer_app_id":274900,"visibility":0,"banned":0}""", WorkshopItemCheck.Shown)]
        [InlineData("""{"publishedfileid":"3776027135","result":1,"consumer_app_id":274900,"visibility":3,"banned":0}""", WorkshopItemCheck.Shown)]
        [InlineData("""{"publishedfileid":"3776027135","result":9}""", WorkshopItemCheck.NotFound)]
        [InlineData("""{"publishedfileid":"3776027135","result":1,"consumer_app_id":274900,"visibility":0,"banned":1}""", WorkshopItemCheck.NotFound)]
        [InlineData("""{"publishedfileid":"3776027135","result":1,"consumer_app_id":274900,"visibility":1,"banned":0}""", WorkshopItemCheck.NotFound)]
        [InlineData("""{"publishedfileid":"3776027135","result":1,"consumer_app_id":274900,"visibility":2,"banned":0}""", WorkshopItemCheck.NotFound)]
        [InlineData("""{"publishedfileid":"3776027135","result":1,"consumer_app_id":4000,"visibility":0,"banned":0}""", WorkshopItemCheck.OtherGame)]
        [InlineData("""{"publishedfileid":"3776027135","result":1,"consumer_app_id":4000,"visibility":2,"banned":0}""", WorkshopItemCheck.NotFound)]
        public async Task EachAnswerIsCheckedByTheLookupsRules(string item, WorkshopItemCheck expected)
        {
            web.Answer(HttpStatusCode.OK, Reply(item));

            var map = await LookUpAsync();

            Assert.Equal(expected, WorkshopItems.Check(map, data.AppID, unlistedAllowed: true));
            Assert.Equal(HttpMethod.Post, Assert.Single(web.Requests).Method);
            Assert.Contains($"&itemcount=1&publishedfileids%5B0%5D={MapId}", Assert.Single(web.Bodies));
        }

        // The latest maps only list public ones, and a lookup by link also shows an unlisted one
        [Fact]
        public void UnlistedMapIsOnlyShownWhenAskedForByItself()
        {
            var unlisted = new PublishedFileDetailsModel { Result = 1, ConsumerAppId = AppId, Visibility = WorkshopItems.Unlisted };

            Assert.Equal(WorkshopItemCheck.Shown, WorkshopItems.Check(unlisted, AppId, unlistedAllowed: true));
            Assert.Equal(WorkshopItemCheck.NotFound, WorkshopItems.Check(unlisted, AppId, unlistedAllowed: false));
        }

        [Fact]
        public void NoDetailsIsNotFound()
        {
            Assert.Equal(WorkshopItemCheck.NotFound, WorkshopItems.Check(null, AppId, unlistedAllowed: true));
        }

        [Theory]
        [InlineData("""{"response":{"result":1,"resultcount":0,"publishedfiledetails":[]}}""")]
        [InlineData("""{"response":{"result":1,"resultcount":1,"publishedfiledetails":[{"publishedfileid":"1","result":1}]}}""")]
        public async Task ReplyWithoutDetailsForTheIdGivesNoMap(string reply)
        {
            web.Answer(HttpStatusCode.OK, reply);

            Assert.Null(await LookUpAsync());
        }

        // Steam lists every ID it's asked about, so a reply without a list is broken too, and must not read as a map that
        // doesn't exist
        [Theory]
        [InlineData(HttpStatusCode.OK, """{"response":{"result":1,"resultcount":0}}""")]
        [InlineData(HttpStatusCode.OK, """{"response":{"result":16}}""")]
        [InlineData(HttpStatusCode.OK, """{"response":{}}""")]
        [InlineData(HttpStatusCode.OK, "{}")]
        [InlineData(HttpStatusCode.OK, "null")]
        [InlineData(HttpStatusCode.OK, "")]
        [InlineData(HttpStatusCode.NoContent, "")]
        public async Task ReplyWithoutAListIsAnError(HttpStatusCode status, string reply)
        {
            web.Answer(status, reply);

            await Assert.ThrowsAsync<InvalidDataException>(() => LookUpAsync());
            Assert.Single(web.Requests);
        }

        // SteamWebAPI2 catches these and gives no response at all, which must not read as a map that doesn't exist
        [Theory]
        [InlineData(HttpStatusCode.InternalServerError)]
        [InlineData(HttpStatusCode.BadRequest)]
        public async Task FailedRequestIsAnError(HttpStatusCode status)
        {
            web.Answer(status);

            await Assert.ThrowsAsync<HttpRequestException>(() => LookUpAsync());
            Assert.Single(web.Requests);
        }

        [Fact]
        public async Task UnreadableReplyIsAnError()
        {
            web.Answer(HttpStatusCode.OK, "<html>Steam is down</html>");

            await Assert.ThrowsAnyAsync<Exception>(() => LookUpAsync());
        }

        // A POST is never sent twice
        [Fact]
        public async Task LookupHasAShortTimeLimit()
        {
            web.Never();
            handler.AttemptLimit = WebRetryHandlerTests.ShortLimit;

            await WebRetryHandlerTests.TimesOutAsync(data.GetWorkshopItem(MapId));

            Assert.Single(web.Requests);
        }
    }
}
