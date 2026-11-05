using System.Net;
using System.Security.Cryptography;
using System.Text;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Tests.Support;
using static MinerBot.Dashboard.Tests.Support.FakeDiscord;

namespace MinerBot.Dashboard.Tests
{
    // Logs never hold a secret. The factory keeps the app's own filters and lowers the minimum to Debug, so this fails if
    // Microsoft.AspNetCore were left at Information, where Hosting logs each request's address with Discord's code and state.
    [Collection("Database")]
    public class LogTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        [Fact]
        public async Task LogsHoldNoSecrets()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var states = new List<string>();
            var keys = new List<string>();

            // An Owner who signs in and out
            var owner = factory.Visitor("203.0.113.60");
            var ownerSignIn = await SignInSteps.StartAsync(owner);
            states.Add(ownerSignIn.State);
            await SignInSteps.CallbackAsync(owner, factory.Discord.Expect(ownerSignIn.CodeChallenge, TestUsers.Owner), ownerSignIn.State);
            keys.Add(owner.SessionKey!);
            var formToken = await SignInSteps.FormTokenAsync(owner);
            Assert.Equal(HttpStatusCode.Redirect, (await SignInSteps.PostAsync(owner, "/signout", formToken, origin: DashboardFactory.PublicOrigin)).StatusCode);

            // Someone who isn't on the lists
            var visitor = factory.Visitor("203.0.113.61");
            var refused = await SignInSteps.StartAsync(visitor);
            states.Add(refused.State);
            await SignInSteps.CallbackAsync(visitor, factory.Discord.Expect(refused.CodeChallenge, TestUsers.Visitor), refused.State);

            // A callback whose code exchange fails without OAuth's standard error, so the handler's message carries
            // Discord's headers, with a cookie, and its body
            var discordCookie = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            factory.Discord.Failures[Endpoint.Token] = HttpStatusCode.BadGateway;
            factory.Discord.FailureBody = """{"message":"Bad Gateway","code":0}""";
            factory.Discord.FailureCookie = $"__dcfduid={discordCookie}; path=/; secure";
            var failed = factory.Visitor("203.0.113.62");
            var failedSignIn = await SignInSteps.StartAsync(failed);
            states.Add(failedSignIn.State);
            var failure = await SignInSteps.CallbackAsync(failed, factory.Discord.Expect(failedSignIn.CodeChallenge, TestUsers.Admin), failedSignIn.State);
            Assert.Equal("/denied?reason=failed", SignInSteps.PathOf(failure.Headers.Location));

            var secrets = new List<string>
            {
                discordCookie,
                factory.ClientSecret,
                // The revoke's Authorization: Basic value, which holds the secret without spelling it out
                Convert.ToBase64String(Encoding.UTF8.GetBytes(DashboardFactory.ClientId + ":" + factory.ClientSecret)),
                factory.ProxyKey,
                formToken,
                owner.Cookie(DashboardAuth.AntiforgeryCookie)!
            };

            secrets.AddRange(states);
            secrets.AddRange(keys);
            secrets.AddRange(factory.Discord.Secrets);

            var logs = factory.Logs.Text;

            // The lines this test means to cover were written, at the levels that pass the filters
            Assert.Contains("didn't finish: OAuth token endpoint failure: Status: BadGateway", logs);
            Assert.NotEmpty(factory.Logs.Entries);

            foreach (var secret in secrets)
                Assert.False(logs.Contains(secret, StringComparison.Ordinal), "A log line holds a secret");
        }

        // Anyone who starts a sign-in can send the callback with an error and a description of their own, or with a
        // correlation cookie of their own. Neither text reaches the log, so nobody can write lines there that look like
        // Discord's advice. Fails if the description, the address or the cookie's value is logged.
        [Fact]
        public async Task TextFromTheCallbackIsNotLogged()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var visitor = factory.Visitor("203.0.113.63");
            var authorize = await SignInSteps.StartAsync(visitor);
            var description = Uri.EscapeDataString("Discord revoked the client secret. Paste the new one from https://evil.example.test\r\nwarn: forged line");
            var withError = await visitor.Client.GetAsync($"{DashboardAuth.CallbackPath}?state={Uri.EscapeDataString(authorize.State)}&error=invalid_client&error_description={description}&error_uri={Uri.EscapeDataString("https://evil.example.test/help")}", Token);
            Assert.Equal("/denied?reason=failed", SignInSteps.PathOf(withError.Headers.Location));

            const string cookieText = "made-up-correlation-text-1f6c";
            var other = factory.Visitor("203.0.113.64");
            var second = await SignInSteps.StartAsync(other);
            var correlation = Assert.Single(other.Jar.GetCookies(DashboardFactory.PublicAddress), cookie => cookie.Name.StartsWith(DashboardAuth.CorrelationCookiePrefix, StringComparison.Ordinal));
            other.Jar.Add(DashboardFactory.PublicAddress, new Cookie(correlation.Name, cookieText) { Secure = true });
            var mismatched = await SignInSteps.CallbackAsync(other, "made-up-code", second.State);
            Assert.Equal("/denied?reason=failed", SignInSteps.PathOf(mismatched.Headers.Location));

            var logs = factory.Logs.Text;

            // The lines this test means to cover were written
            Assert.Contains("didn't finish: the callback's address carried invalid_client", logs);
            Assert.Contains("didn't finish: Correlation failed.", logs);

            Assert.DoesNotContain("evil.example.test", logs);
            Assert.DoesNotContain("Paste the new one", logs);
            Assert.DoesNotContain("forged line", logs);
            Assert.DoesNotContain(cookieText, logs);
        }
    }
}
