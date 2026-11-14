using System.Net;
using System.Web;

namespace SteamWebAPIAccess.Tests
{
    // GetLatestWorkshopItems on canned replies: QueryFiles pages in SteamKit's text format, then one details call in JSON.
    // The details replies hold every map of the test unless a test says otherwise, so the maps that come back show
    // which IDs the query collected.
    public class LatestMapsTests
    {
        const uint AppId = 274900;
        const uint OtherAppId = 4000;
        const string Query = "GET /IPublishedFileService/QueryFiles/v1/";
        const string Details = "POST /ISteamRemoteStorage/GetPublishedFileDetails/v1/";

        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

        readonly FakeWeb web = new();
        readonly WebRetryHandler handler;
        readonly GetData data;

        public LatestMapsTests()
        {
            // SteamAPIAccess reads this without checking it
            Environment.SetEnvironmentVariable("STEAMAPP_ID", "1");

            handler = new WebRetryHandler(web);
            data = new GetData(new SteamAPIAccess { PublisherWebAPIKey = "key", appID = AppId }, new HttpClient(handler, disposeHandler: false) { Timeout = handler.ClientTimeout }, handler);
            data.Initilization();
        }

        // A QueryFiles item with these fields in this order
        static string Item(params (string Name, string Value)[] fields) =>
            "{\n" + string.Concat(fields.Select(field => $"\"{field.Name}\" \"{field.Value}\"\n")) + "}\n";

        static string Item(ulong id, int visibility) => Item(("publishedfileid", $"{id}"), ("visibility", $"{visibility}"));

        // A full page of twenty items, as the query asks for, counting down from firstId. Only publicIds are public.
        static string[] FullPage(ulong firstId, params ulong[] publicIds) =>
            Enumerable.Range(0, 20).Select(index => firstId - (ulong)index).Select(id => Item(id, publicIds.Contains(id) ? 0 : 2)).ToArray();

        // A QueryFiles page. Steam leaves out a list with no items, and total is left out when a test gives none.
        static string Page(int? total, params string[] items) =>
            "\"response\"\n{\n"
            + (total == null ? "" : $"\"total\" \"{total}\"\n")
            + (items.Length == 0 ? "" : "\"publishedfiledetails\"\n{\n" + string.Concat(items.Select((item, index) => $"\"{index}\"\n{item}")) + "}\n")
            + "}\n";

        // A map of the details reply, which passes every check unless a test says otherwise
        static string Map(ulong id, int result = 1, uint app = AppId, int visibility = 0, int banned = 0) =>
            $$"""{"publishedfileid":"{{id}}","result":{{result}},"creator_app_id":{{app}},"consumer_app_id":{{app}},"title":"Map {{id}}","description":"","time_created":1700000000,"time_updated":1700003600,"visibility":{{visibility}},"banned":{{banned}},"ban_reason":"","tags":[]}""";

        static string DetailsReply(params string[] maps) =>
            $$$"""{"response":{"result":1,"resultcount":{{{maps.Length}}},"publishedfiledetails":[{{{string.Join(",", maps)}}}]}}""";

        async Task<List<ulong>> ReadAsync()
        {
            var maps = await data.GetLatestWorkshopItems(5).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
            return maps.Select(map => map.PublishedFileId).ToList();
        }

        List<string> Calls => web.Requests.Select(request => $"{request.Method} {request.Uri!.AbsolutePath}").ToList();

        // The IDs that the details call carries in its body
        List<ulong> DetailsIds()
        {
            var body = HttpUtility.ParseQueryString(web.Bodies[Calls.IndexOf(Details)]);
            return Enumerable.Range(0, int.Parse(body["itemcount"]!)).Select(index => ulong.Parse(body[$"publishedfileids[{index}]"]!)).ToList();
        }

        [Fact]
        public async Task ReadIsOneQueryAndOneDetailsCall()
        {
            web.Answer(HttpStatusCode.OK, Page(7, Item(700, 0), Item(600, 2), Item(500, 0), Item(400, 0), Item(300, 1), Item(200, 0), Item(100, 0)))
               .Answer(HttpStatusCode.OK, DetailsReply(Map(700), Map(500), Map(400), Map(200), Map(100)));

            Assert.Equal([700ul, 500, 400, 200, 100], await ReadAsync());
            Assert.Equal([Query, Details], Calls);
            Assert.Equal([700ul, 500, 400, 200, 100], DetailsIds());
            Assert.Contains("&numperpage=20&", web.Requests[0].Uri!.Query);
        }

        // Steam's usual first page: twenty public maps and a total far past it, so only the number asked for stops the
        // read, both on the page and before the next one
        [Fact]
        public async Task ReadStopsAtTheMapsAsked()
        {
            var ids = Enumerable.Range(0, 20).Select(index => 2000ul - (ulong)index).ToList();
            web.Answer(HttpStatusCode.OK, Page(5000, FullPage(2000, [.. ids])))
               .Answer(HttpStatusCode.OK, DetailsReply(ids.Select(id => Map(id)).ToArray()));

            Assert.Equal([2000ul, 1999, 1998, 1997, 1996], await ReadAsync());
            Assert.Equal([Query, Details], Calls);
            Assert.Equal([2000ul, 1999, 1998, 1997, 1996], DetailsIds());
        }

        // Steam leaves out the fields an item doesn't have, so a field's place says nothing
        [Fact]
        public async Task FieldsAreReadByNameInAnyOrder()
        {
            web.Answer(HttpStatusCode.OK, Page(3,
                    Item(("visibility", "0"), ("publishedfileid", "300")),
                    Item(("result", "1"), ("creator", "76561197960265729"), ("publishedfileid", "200"), ("flags", "0"), ("visibility", "2")),
                    Item(("result", "1"), ("publishedfileid", "100"), ("creator", "76561197960265729"), ("title", "Map"), ("visibility", "0"), ("flags", "0"))))
               .Answer(HttpStatusCode.OK, DetailsReply(Map(300), Map(200), Map(100)));

            Assert.Equal([300ul, 100], await ReadAsync());
            Assert.Equal([300ul, 100], DetailsIds());
        }

        [Fact]
        public async Task ItemWithoutAReadableIdOrVisibilityIsSkipped()
        {
            web.Answer(HttpStatusCode.OK, Page(6,
                    Item(("visibility", "0")),
                    Item(("publishedfileid", "abc"), ("visibility", "0")),
                    Item(("publishedfileid", "0"), ("visibility", "0")),
                    Item(("publishedfileid", "200")),
                    Item(("publishedfileid", "100"), ("visibility", "public")),
                    Item(300, 0)))
               .Answer(HttpStatusCode.OK, DetailsReply(Map(200), Map(100), Map(300)));

            Assert.Equal([300ul], await ReadAsync());
            Assert.Equal([300ul], DetailsIds());
        }

        [Fact]
        public async Task FriendsOnlyPrivateAndUnlistedItemsAreSkipped()
        {
            web.Answer(HttpStatusCode.OK, Page(4, Item(400, 1), Item(300, 2), Item(200, 3), Item(100, 0)))
               .Answer(HttpStatusCode.OK, DetailsReply(Map(400), Map(300), Map(200), Map(100)));

            Assert.Equal([100ul], await ReadAsync());
            Assert.Equal([100ul], DetailsIds());
        }

        [Fact]
        public async Task PageWithNoItemsStopsTheRead()
        {
            web.Answer(HttpStatusCode.OK, Page(null, Item(300, 0), Item(200, 0)))
               .Answer(HttpStatusCode.OK, Page(null))
               .Answer(HttpStatusCode.OK, DetailsReply(Map(300), Map(200)));

            Assert.Equal([300ul, 200], await ReadAsync());
            Assert.Equal([Query, Query, Details], Calls);
            Assert.Contains("&page=2&", web.Requests[1].Uri!.Query);
        }

        [Fact]
        public async Task ReadStopsAfterThreePagesWithWhatItHas()
        {
            web.Answer(HttpStatusCode.OK, Page(null, Item(300, 0), Item(301, 2)))
               .Answer(HttpStatusCode.OK, Page(null, Item(200, 0), Item(201, 2)))
               .Answer(HttpStatusCode.OK, Page(null, Item(100, 0), Item(101, 2)))
               .Answer(HttpStatusCode.OK, DetailsReply(Map(300), Map(200), Map(100)));

            Assert.Equal([300ul, 200, 100], await ReadAsync());
            Assert.Equal([Query, Query, Query, Details], Calls);
        }

        // total counts every item of the query, so the page that reaches it is the last
        [Fact]
        public async Task TotalStopsTheReadWithFewerMapsThanAsked()
        {
            web.Answer(HttpStatusCode.OK, Page(3, Item(300, 0), Item(200, 2), Item(100, 0)))
               .Answer(HttpStatusCode.OK, DetailsReply(Map(300), Map(200), Map(100)));

            Assert.Equal([300ul, 100], await ReadAsync());
            Assert.Equal([Query, Details], Calls);
        }

        // Steam always sends total. One past the first page lets the read go on, and the page reaching it is the last.
        [Fact]
        public async Task ReadGoesOnUntilAPageReachesTheTotal()
        {
            web.Answer(HttpStatusCode.OK, Page(40, FullPage(1000, 1000, 990, 985)))
               .Answer(HttpStatusCode.OK, Page(40, FullPage(980, 975)))
               .Answer(HttpStatusCode.OK, DetailsReply(Map(1000), Map(990), Map(985), Map(975)));

            Assert.Equal([1000ul, 990, 985, 975], await ReadAsync());
            Assert.Equal([Query, Query, Details], Calls);
            Assert.Contains("&page=2&", web.Requests[1].Uri!.Query);
        }

        // Steam may page by its cursor and send the first page again
        [Fact]
        public async Task RepeatedIdIsListedOnce()
        {
            web.Answer(HttpStatusCode.OK, Page(null, Item(300, 0), Item(200, 0), Item(300, 0)))
               .Answer(HttpStatusCode.OK, Page(null, Item(300, 0), Item(200, 0), Item(100, 0)))
               .Answer(HttpStatusCode.OK, Page(null))
               .Answer(HttpStatusCode.OK, DetailsReply(Map(300), Map(200), Map(100)));

            Assert.Equal([300ul, 200, 100], await ReadAsync());
            Assert.Equal([300ul, 200, 100], DetailsIds());
        }

        [Fact]
        public async Task DetailsInAnotherOrderKeepSteamsRanking()
        {
            web.Answer(HttpStatusCode.OK, Page(3, Item(300, 0), Item(100, 0), Item(200, 0)))
               .Answer(HttpStatusCode.OK, DetailsReply(Map(100), Map(200), Map(300)));

            Assert.Equal([300ul, 100, 200], await ReadAsync());
        }

        // A map can be removed, banned or hidden between the two calls, or the query can list another game's item
        [Theory]
        [InlineData(9, AppId, 0, 0)]
        [InlineData(1, AppId, 0, 1)]
        [InlineData(1, AppId, 1, 0)]
        [InlineData(1, AppId, 2, 0)]
        [InlineData(1, AppId, 3, 0)]
        [InlineData(1, OtherAppId, 0, 0)]
        public async Task MapThatFailsACheckOfItsDetailsIsLeftOut(int result, uint app, int visibility, int banned)
        {
            web.Answer(HttpStatusCode.OK, Page(3, Item(300, 0), Item(200, 0), Item(100, 0)))
               .Answer(HttpStatusCode.OK, DetailsReply(Map(300), Map(200, result, app, visibility, banned), Map(100)));

            Assert.Equal([300ul, 100], await ReadAsync());
        }

        // Steam answers an ID it no longer has with a placeholder that holds only the ID and the result
        [Fact]
        public async Task MapRemovedSinceTheQueryIsLeftOut()
        {
            web.Answer(HttpStatusCode.OK, Page(3, Item(300, 0), Item(200, 0), Item(100, 0)))
               .Answer(HttpStatusCode.OK, DetailsReply(Map(300), """{"publishedfileid":"200","result":9}""", Map(100)));

            Assert.Equal([300ul, 100], await ReadAsync());
        }

        // A reply without one of the IDs leaves only that map out
        [Fact]
        public async Task MapMissingFromTheDetailsIsLeftOut()
        {
            web.Answer(HttpStatusCode.OK, Page(3, Item(300, 0), Item(200, 0), Item(100, 0)))
               .Answer(HttpStatusCode.OK, DetailsReply(Map(300), Map(100)));

            Assert.Equal([300ul, 100], await ReadAsync());
        }

        [Fact]
        public async Task FirstPageWithNoItemsGivesAnEmptyListWithoutADetailsCall()
        {
            web.Answer(HttpStatusCode.OK, Page(0));

            Assert.Empty(await ReadAsync());
            Assert.Equal([Query], Calls);
        }

        [Fact]
        public async Task QueryThatFailsGivesAnEmptyList()
        {
            web.Answer(HttpStatusCode.InternalServerError).Answer(HttpStatusCode.InternalServerError);

            Assert.Empty(await ReadAsync());
            Assert.Equal([Query, Query], Calls);
        }

        [Fact]
        public async Task UnreadableQueryReplyGivesAnEmptyList()
        {
            web.Answer(HttpStatusCode.OK, "<html>Steam is down</html>");

            Assert.Empty(await ReadAsync());
            Assert.Equal([Query], Calls);
        }

        // SteamWebAPI2 catches the error and gives no response at all
        [Fact]
        public async Task DetailsCallThatFailsGivesAnEmptyList()
        {
            web.Answer(HttpStatusCode.OK, Page(1, Item(100, 0))).Answer(HttpStatusCode.InternalServerError);

            Assert.Empty(await ReadAsync());
            Assert.Equal([Query, Details], Calls);
        }

        [Fact]
        public async Task DetailsCallThatNeverAnswersGivesAnEmptyList()
        {
            handler.AttemptLimit = WebRetryHandlerTests.ShortLimit;
            web.Answer(HttpStatusCode.OK, Page(1, Item(100, 0))).Never();

            Assert.Empty(await ReadAsync());
            Assert.Equal([Query, Details], Calls);
        }
    }
}
