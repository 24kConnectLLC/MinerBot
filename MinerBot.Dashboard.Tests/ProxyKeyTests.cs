using System.Net;
using DataAccessLibrary.Tests.Database;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // The gate in the whole app: only nginx, which sends the key, gets an answer, and the health endpoints answer only
    // the container itself, on Host localhost
    [Collection("Database")]
    public class ProxyKeyTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        [Fact]
        public async Task OnlyRequestsWithTheKeyGetAnAnswer()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var withoutKey = await factory.Visitor(proxyKey: false).Client.GetAsync("/", Token);
            Assert.Equal(HttpStatusCode.NotFound, withoutKey.StatusCode);
            Assert.Empty(await withoutKey.Content.ReadAsByteArrayAsync(Token));

            var wrong = factory.Visitor(proxyKey: false);
            wrong.Client.DefaultRequestHeaders.Add(ProxyKeyMiddleware.HeaderName, new string('0', factory.ProxyKey.Length));
            var withWrongKey = await wrong.Client.GetAsync("/", Token);
            Assert.Equal(HttpStatusCode.NotFound, withWrongKey.StatusCode);
            Assert.Empty(await withWrongKey.Content.ReadAsByteArrayAsync(Token));

            var withKey = await factory.Visitor().Client.GetAsync("/", Token);
            Assert.Equal(HttpStatusCode.OK, withKey.StatusCode);
        }

        [Fact]
        public async Task HealthChecksAnswerOnlyOnLocalhost()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            // The container's own check: Host localhost and no key
            using var local = factory.Local();
            var live = await local.GetAsync("/health/live", Token);
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.Equal("Healthy", await live.Content.ReadAsStringAsync(Token));

            // The exemption covers the health checks only
            var localHome = await local.GetAsync("/", Token);
            Assert.Equal(HttpStatusCode.NotFound, localHome.StatusCode);
            Assert.Empty(await localHome.Content.ReadAsByteArrayAsync(Token));

            // On the public name, without the key, as anything else would ask
            var publicWithoutKey = await factory.Visitor(proxyKey: false).Client.GetAsync("/health/live", Token);
            Assert.Equal(HttpStatusCode.NotFound, publicWithoutKey.StatusCode);
            Assert.Empty(await publicWithoutKey.Content.ReadAsByteArrayAsync(Token));

            // On the public name with the key, as a visitor through nginx would ask: still not there
            var publicWithKey = await factory.Visitor().Client.GetAsync("/health/live", Token);
            Assert.Equal(HttpStatusCode.NotFound, publicWithKey.StatusCode);
            Assert.DoesNotContain("Healthy", await publicWithKey.Content.ReadAsStringAsync(Token));
        }

        // Discord's redirect address is built from the Host header, so only the public name and localhost are answered
        [Fact]
        public async Task OtherHostNamesAreRefused()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var response = await factory.Visitor(baseAddress: new Uri("https://evil.example.test")).Client.GetAsync("/", Token);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(Token));
        }

        // The list is set in code, so ASP.NET Core's AllowedHosts setting, from the environment or anywhere else, can't
        // widen it. Fails if the list is ever left to that setting.
        [Theory]
        [InlineData("*")]
        [InlineData("evil.example.test")]
        public async Task AllowedHostsSettingCantWidenTheList(string allowedHosts)
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync(), new DashboardFactoryOptions
            {
                Settings = new Dictionary<string, string?> { ["AllowedHosts"] = allowedHosts }
            });

            var other = await factory.Visitor(baseAddress: new Uri("https://evil.example.test")).Client.GetAsync("/", Token);
            Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);

            var own = await factory.Visitor().Client.GetAsync("/", Token);
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        }

        // TestServer always sends a Host, so this pins what a request can't reach: Kestrel takes an HTTP/1.0 request with no
        // Host at all, and only AllowEmptyHosts being off refuses it
        [Fact]
        public async Task HostListIsSetInCode()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var filtering = factory.Services.GetRequiredService<IOptionsMonitor<HostFilteringOptions>>().CurrentValue;

            Assert.Equal(["dashboard.test", "localhost"], filtering.AllowedHosts);
            Assert.False(filtering.AllowEmptyHosts);
            Assert.False(filtering.IncludeFailureMessage);
        }

        // The key never reaches a page, so no page or log can show it
        [Fact]
        public async Task KeyIsGoneBeforeThePages()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var page = await factory.Visitor().Client.GetStringAsync("/", Token);

            Assert.DoesNotContain(factory.ProxyKey, page);
            Assert.DoesNotContain(factory.ProxyKey, factory.Logs.Text);
        }
    }
}
