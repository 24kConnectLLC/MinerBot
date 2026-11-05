using System.Net;
using DataAccessLibrary.Tests.Database;
using Microsoft.Extensions.Primitives;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Tests.Support;
using static MinerBot.Dashboard.Tests.Support.FakeDiscord;

namespace MinerBot.Dashboard.Tests
{
    // ASP.NET Core reads cookie names ignoring case, and Safari checks the __Host- prefix with its case, so a __HOST- cookie
    // that murderminershub.com set for the whole domain would otherwise count as the dashboard's own
    [Collection("Database")]
    public class CookiePrefixGuardTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        // Null when nothing goes, and what stays keeps its text
        [Theory]
        [InlineData("__Host-mb=a", null)]
        [InlineData("theme=dark", null)]
        [InlineData("__Hostile=1", null)]
        [InlineData("__HOST-mb=x", "")]
        [InlineData("__host-mb-signin.n=N", "")]
        [InlineData("__Host-mb=a; __HOST-mb=x", "__Host-mb=a")]
        [InlineData("__HOST-mb=x; __Host-mb=a; theme=dark", "__Host-mb=a; theme=dark")]
        [InlineData("__Secure-x=1; __SECURE-y=2", "__Secure-x=1")]
        [InlineData(" __hOsT-mb=x;__Host-mb-af=t", "__Host-mb-af=t")]
        public void CookiesWithAPrefixInAnotherCaseAreDropped(string header, string? kept)
        {
            Assert.Equal(kept, CookiePrefixGuard.Kept(header)?.ToString());
        }

        [Fact]
        public void EachCookieHeaderIsCheckedOnItsOwn()
        {
            var kept = CookiePrefixGuard.Kept(new StringValues(["__Host-mb=a", "__HOST-mb=x", "theme=dark"]));

            Assert.Equal(["__Host-mb=a", "theme=dark"], kept!.Value.ToArray());
        }

        // Someone on the lists starts a sign-in, approves it at Discord as themselves, and has the victim's browser finish
        // it with the correlation cookie planted under __HOST-. Fails if the victim ends up signed in as the attacker.
        [Fact]
        public async Task PlantedCorrelationCookieCantFinishSomeoneElsesSignIn()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var attacker = factory.Visitor("203.0.113.80");
            var authorize = await SignInSteps.StartAsync(attacker);
            var correlation = Assert.Single(attacker.Jar.GetCookies(DashboardFactory.PublicAddress), cookie => cookie.Name.StartsWith(DashboardAuth.CorrelationCookiePrefix, StringComparison.Ordinal));
            var code = factory.Discord.Expect(authorize.CodeChallenge, TestUsers.Admin);

            var victim = factory.Visitor("203.0.113.81");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{DashboardAuth.CallbackPath}?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(authorize.State)}");
            request.Headers.Add("Cookie", "__HOST-" + correlation.Name["__Host-".Length..] + "=" + correlation.Value);

            var response = await victim.Client.SendAsync(request, Token);

            Assert.Equal("/denied?reason=failed", SignInSteps.PathOf(response.Headers.Location));
            Assert.Null(victim.SessionKey);
            Assert.Empty(factory.Discord.CallsTo(Endpoint.Token));
        }

        // A session key planted under __HOST-mb is no session. Fails if the browser is signed in with it.
        [Fact]
        public async Task PlantedSessionCookieIsNoSession()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var key = await SignInSteps.SignedInAsync(factory, factory.Visitor(), TestUsers.Admin);

            using var request = new HttpRequestMessage(HttpMethod.Get, "/admin");
            request.Headers.Add("Cookie", "__HOST-mb=" + key);

            var response = await factory.Visitor("203.0.113.82").Client.SendAsync(request, Token);

            Assert.Equal("/signin?returnUrl=%2Fadmin", SignInSteps.PathOf(response.Headers.Location));
        }

        // A planted __HOST-mb after the real cookie would win, since the last of one name does, and hide the real session.
        // Fails if the real session isn't the one used.
        [Fact]
        public async Task PlantedSessionCookieDoesNotHideTheRealOne()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var key = await SignInSteps.SignedInAsync(factory, factory.Visitor(), TestUsers.Owner);

            using var request = new HttpRequestMessage(HttpMethod.Get, "/admin");
            request.Headers.Add("Cookie", $"__Host-mb={key}; __HOST-mb={SessionKeys.New()}");

            var response = await factory.Visitor("203.0.113.83").Client.SendAsync(request, Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
