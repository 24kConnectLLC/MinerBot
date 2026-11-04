using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace SteamWebAPIAccess.Tests
{
    // GetData's real calls through SteamKit and SteamWebAPI2, on the shared handler over a fake network
    public class WebCallTests
    {
        const ulong SteamId = 76561197960265729;

        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

        // What Steam sends back in SteamKit's text format
        const string VanityAnswer = """
            "response"
            {
                "steamid"	"76561197960265729"
                "success"	"1"
            }
            """;

        readonly FakeWeb web = new();
        readonly WebRetryHandler handler;
        readonly GetData data;

        public WebCallTests()
        {
            // SteamAPIAccess reads this without checking it
            Environment.SetEnvironmentVariable("STEAMAPP_ID", "1");

            handler = new WebRetryHandler(web);
            data = new GetData(new SteamAPIAccess { PublisherWebAPIKey = "key" }, new HttpClient(handler, disposeHandler: false) { Timeout = handler.ClientTimeout }, handler);
            data.Initilization();
        }

        // The bot's own registrations, with the fake network put under the shared handler before its first call
        static ServiceProvider Registered(FakeWeb network)
        {
            var services = new ServiceCollection().AddWebClient();
            services.AddSteamAPIServices();

            var provider = services.BuildServiceProvider();
            provider.GetRequiredService<WebRetryHandler>().InnerHandler = network;
            provider.GetRequiredService<ISteamAPIAccess>().PublisherWebAPIKey = "key";
            return provider;
        }

        static void AssertGetsOf(string path, IReadOnlyList<(HttpMethod Method, Uri? Uri)> requests, int count)
        {
            Assert.Equal(count, requests.Count);
            Assert.All(requests, request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Contains(path, request.Uri!.AbsolutePath);
            });
        }

        [Fact]
        public async Task SteamKitCallsGoThroughTheSharedHandler()
        {
            web.Answer(HttpStatusCode.ServiceUnavailable).Answer(HttpStatusCode.OK, VanityAnswer);

            Assert.Equal(SteamId, await data.ResolveVanityURL("someone").WaitAsync(WaitLimit, TestContext.Current.CancellationToken));
            AssertGetsOf("ISteamUser/ResolveVanityURL", web.Requests, 2);
        }

        [Fact]
        public async Task SteamKitCallsHaveAShortTimeLimit()
        {
            web.Never().Never();
            handler.AttemptLimit = WebRetryHandlerTests.ShortLimit;

            await WebRetryHandlerTests.TimesOutAsync(data.ResolveVanityURL("someone"));

            AssertGetsOf("ISteamUser/ResolveVanityURL", web.Requests, 2);
        }

        [Fact]
        public void SteamKitsOwnTimeoutIsReplaced()
        {
            using var steamInterface = data.GetSteamInterface("ISteamUser");

            Assert.Equal(handler.ClientTimeout, steamInterface.Timeout);
        }

        [Fact]
        public async Task SteamWebAPI2CallsGoThroughTheSharedHandler()
        {
            web.Answer(HttpStatusCode.InternalServerError).Answer(HttpStatusCode.InternalServerError);

            await Assert.ThrowsAsync<HttpRequestException>(() => data.GetPlayerSummaries(SteamId).WaitAsync(WaitLimit, TestContext.Current.CancellationToken));

            AssertGetsOf("GetPlayerSummaries", web.Requests, 2);
        }

        [Fact]
        public void RegisteredClientHasTheTimeLimitOfTheSharedHandler()
        {
            using var provider = Registered(new FakeWeb());

            Assert.Equal(provider.GetRequiredService<WebRetryHandler>().ClientTimeout, provider.GetRequiredService<HttpClient>().Timeout);
        }

        // SteamKit gets the handler and SteamWebAPI2 the client, so both have to be the registered ones
        [Fact]
        public async Task RegisteredGetDataCallsThroughTheSharedHandler()
        {
            var network = new FakeWeb().Answer(HttpStatusCode.ServiceUnavailable).Answer(HttpStatusCode.OK, VanityAnswer)
                .Answer(HttpStatusCode.InternalServerError).Answer(HttpStatusCode.InternalServerError);
            using var provider = Registered(network);
            var registered = provider.GetRequiredService<IGetData>();
            registered.Initilization();

            Assert.Equal(SteamId, await registered.ResolveVanityURL("someone").WaitAsync(WaitLimit, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<HttpRequestException>(() => registered.GetPlayerSummaries(SteamId).WaitAsync(WaitLimit, TestContext.Current.CancellationToken));

            AssertGetsOf("ISteamUser/ResolveVanityURL", network.Requests.Take(2).ToList(), 2);
            AssertGetsOf("GetPlayerSummaries", network.Requests.Skip(2).ToList(), 2);
        }
    }
}
