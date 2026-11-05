using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Services;
using MinerBot.Dashboard.Tests.Support;
using static MinerBot.Dashboard.Tests.Support.FakeDiscord;

namespace MinerBot.Dashboard.Tests
{
    // Every call meant for Discord reaches the fake, and one to any other address fails instead of leaving the process
    [Collection("Database")]
    public class NetworkTests(MariaDbFixture fixture)
    {
        [Fact]
        public async Task EveryDiscordCallGoesToTheFake()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            await SignInSteps.SignedInAsync(factory, factory.Visitor(), TestUsers.Owner);

            var calls = factory.Discord.Calls;
            Assert.Equal([Endpoint.Token, Endpoint.User, Endpoint.Revoke], calls.Select(call => call.Endpoint));

            // The user and the revoke go through the dashboard's typed client, the code exchange through the OAuth handler's own
            Assert.Equal(DiscordUserApi.UserAgent, calls[1].UserAgent);
            Assert.Equal(DiscordUserApi.UserAgent, calls[2].UserAgent);
        }

        // Fails if any of Discord's four addresses is written into the code instead of read from the settings: the
        // browser's, the code exchange's, the user's and the revoke's
        [Fact]
        public async Task EveryDiscordAddressComesFromTheSettings()
        {
            const string elsewhere = "https://discord.example.test";

            await using var factory = new DashboardFactory(await fixture.ReadyAsync(), new DashboardFactoryOptions
            {
                Settings = new Dictionary<string, string?>
                {
                    ["Discord:AuthorizationEndpoint"] = elsewhere + "/oauth2/authorize",
                    ["Discord:TokenEndpoint"] = elsewhere + "/api/oauth2/token",
                    ["Discord:UserInformationEndpoint"] = elsewhere + "/api/v10/users/@me",
                    ["Discord:RevocationEndpoint"] = elsewhere + "/api/oauth2/token/revoke"
                }
            });

            factory.Discord.Origin = elsewhere;

            // The browser is sent to the configured authorize address, which SignInSteps checks
            await SignInSteps.SignedInAsync(factory, factory.Visitor(), TestUsers.Owner);

            var calls = factory.Discord.Calls;
            Assert.Equal([Endpoint.Token, Endpoint.User, Endpoint.Revoke], calls.Select(call => call.Endpoint));
            Assert.All(calls, call => Assert.StartsWith(elsewhere + "/", call.Address, StringComparison.Ordinal));
        }

        [Fact]
        public async Task UnknownAddressIsAFailedSignInNotANetworkCall()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync(), new DashboardFactoryOptions
            {
                Settings = new Dictionary<string, string?> { ["Discord:UserInformationEndpoint"] = "https://discord.example.test/api/v10/users/@me" }
            });

            var response = await SignInSteps.SignInAsync(factory, factory.Visitor(), TestUsers.Owner);

            Assert.Equal("/denied?reason=failed", SignInSteps.PathOf(response.Headers.Location));
            Assert.Contains(factory.Discord.Calls, call => call.Endpoint == Endpoint.Unknown && call.Address.StartsWith("https://discord.example.test/", StringComparison.Ordinal));

            // The token it was given is still revoked
            Assert.Single(factory.Discord.CallsTo(Endpoint.Revoke));
        }
    }
}
