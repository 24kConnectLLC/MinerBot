using System.Net;
using DataAccessLibrary.Tests.Database;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Hosting;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // The settings are checked before the app starts. Program.cs makes the host's own start-up check first, so a missing
    // setting stops it with a plain message, which CommandModeProcessTests sees from outside, and nothing ever serves a
    // request. Fails if ValidateOnStart is left out, when the app would start and answer 404 to everything.
    [Collection("Database")]
    public class StartupTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        [Theory]
        [InlineData("Dashboard:ProxyKey")]
        [InlineData("Discord:ClientSecret")]
        [InlineData("Dashboard:Owners")]
        public async Task MissingSettingStopsTheAppBeforeAnyRequest(string key)
        {
            await using var factory = new DashboardFactory(fixture.Require(), new DashboardFactoryOptions
            {
                Settings = new Dictionary<string, string?> { [key] = "" }
            });

            var failure = Assert.Throws<OptionsValidationException>(() => factory.Services.GetRequiredService<IStartupValidator>().Validate());
            Assert.Contains(key, failure.Message);
            Assert.StartsWith(key + " ", Assert.Single(DashboardHosting.SettingsProblems(factory.Services)));

            // The app returned before starting, so there's no server to send a request to
            Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        }

        [Fact]
        public async Task BothSectionsFailingStopsTheAppWithBoth()
        {
            await using var factory = new DashboardFactory(fixture.Require(), new DashboardFactoryOptions
            {
                Settings = new Dictionary<string, string?> { ["Dashboard:ProxyKey"] = "", ["Discord:ClientSecret"] = "" }
            });

            var failure = Assert.Throws<AggregateException>(() => factory.Services.GetRequiredService<IStartupValidator>().Validate());
            Assert.Equal(2, failure.InnerExceptions.Count);
            Assert.All(failure.InnerExceptions, inner => Assert.IsType<OptionsValidationException>(inner));
            Assert.Equal(2, DashboardHosting.SettingsProblems(factory.Services).Count);
        }

        [Fact]
        public async Task ValidSettingsStart()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            using var local = factory.Local();

            var response = await local.GetAsync("/health/live", Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(DashboardHosting.SettingsProblems(factory.Services));
        }

        // TestServer never uses Kestrel, so this checks the options the app would give it. KestrelTests checks Kestrel itself.
        [Fact]
        public async Task KestrelIsSetFromTheSettings()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var kestrel = factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

            Assert.False(kestrel.AddServerHeader);
            Assert.Equal(DashboardHosting.MaxRequestBodySize, kestrel.Limits.MaxRequestBodySize);
        }
    }
}
