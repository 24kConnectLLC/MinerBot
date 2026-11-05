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
    }
}
