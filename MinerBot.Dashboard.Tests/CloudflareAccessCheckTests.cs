using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Settings;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // The check on its own, without a server: what it guards, that the token never reaches anything after it, and what a
    // refused request gets
    public class CloudflareAccessCheckTests(AccessTestKeys keys) : IClassFixture<AccessTestKeys>
    {
        const string PublicOrigin = "https://dashboard.test";

        static readonly CloudflareAccessLimits Short = new() { FetchLimit = TimeSpan.FromSeconds(1), WaitLimit = TimeSpan.FromSeconds(2) };

        sealed class Next
        {
            public bool Ran { get; private set; }
            public bool HeaderWasThere { get; private set; }

            public Task Invoke(HttpContext context)
            {
                Ran = true;
                HeaderWasThere = context.Request.Headers.ContainsKey(CloudflareAccessCheck.HeaderName);
                return Task.CompletedTask;
            }
        }

        sealed record Result(DefaultHttpContext Context, Next Next, string Body, RecordingLoggerProvider Logs);

        FakeCloudflareAccess NewAccess() => new(keys) { Clock = new FakeClock() };

        static async Task<Result> RunAsync(FakeCloudflareAccess access, bool on, string path, Endpoint? endpoint = null, params string[] tokens)
        {
            var logs = new RecordingLoggerProvider();
            using var provider = access.Services(access.Clock, Short, logs);
            var settings = on ? new CloudflareAccessOptions { TeamDomain = FakeCloudflareAccess.TeamDomain, Audience = access.Audience } : new CloudflareAccessOptions();
            var next = new Next();
            var check = new CloudflareAccessCheck(next.Invoke, Options.Create(settings), Options.Create(new DashboardOptions { PublicOrigin = PublicOrigin }),
                provider.GetRequiredService<CloudflareAccessKeys>(), access.Clock, provider.GetRequiredService<ILogger<CloudflareAccessCheck>>());

            var context = new DefaultHttpContext();
            context.Request.Path = path;
            context.Response.Body = new MemoryStream();

            if (endpoint != null)
                context.SetEndpoint(endpoint);

            if (tokens.Length > 0)
                context.Request.Headers[CloudflareAccessCheck.HeaderName] = tokens;

            await check.InvokeAsync(context).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            return new Result(context, next, Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()), logs);
        }

        static Endpoint Page(params object[] metadata) => new(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test page");

        // Taken off before anything after the check runs, on guarded and unguarded paths, with the check on and off
        [Theory]
        [InlineData(true, "/admin")]
        [InlineData(true, "/")]
        [InlineData(false, "/admin")]
        [InlineData(false, "/")]
        public async Task HeaderIsGoneBeforeNextRuns(bool on, string path)
        {
            var access = NewAccess();
            var result = await RunAsync(access, on, path, tokens: access.Mint());

            Assert.True(result.Next.Ran);
            Assert.False(result.Next.HeaderWasThere);
            Assert.False(result.Context.Request.Headers.ContainsKey(CloudflareAccessCheck.HeaderName));
            Assert.Equal(StatusCodes.Status200OK, result.Context.Response.StatusCode);
        }

        // Off, nothing is checked or fetched, and the request goes on as before the check existed
        [Fact]
        public async Task OffLetsEverythingThroughWithoutAFetch()
        {
            var access = NewAccess();

            var result = await RunAsync(access, false, "/admin", Page(new AuthorizeAttribute(Policies.Admin)));

            Assert.True(result.Next.Ran);
            Assert.Equal(0, access.Fetches);
        }

        [Fact]
        public async Task RefusalIsA403SayingWhereToGo()
        {
            var access = NewAccess();
            var result = await RunAsync(access, true, "/admin", tokens: access.Mint(signWith: keys.Other));

            Assert.False(result.Next.Ran);
            Assert.False(result.Context.Request.Headers.ContainsKey(CloudflareAccessCheck.HeaderName));
            Assert.Equal(StatusCodes.Status403Forbidden, result.Context.Response.StatusCode);
            Assert.Equal("text/plain; charset=utf-8", result.Context.Response.ContentType);
            Assert.Equal("This part of the dashboard is behind Cloudflare Access, and this request didn't come through it. Open https://dashboard.test/admin and sign in with Cloudflare first.", result.Body);
            Assert.Contains(result.Logs.Entries, entry => entry.Level == LogLevel.Information
                && entry.Message.StartsWith("A request for an admin page was refused by the Cloudflare Access check: bad signature (request ", StringComparison.Ordinal));
        }

        // Exactly one header value
        [Fact]
        public async Task MissingOrDoubledHeaderIsRefused()
        {
            var access = NewAccess();

            var missing = await RunAsync(access, true, "/admin");
            var empty = await RunAsync(access, true, "/admin", tokens: "");
            var doubled = await RunAsync(access, true, "/admin", tokens: [access.Mint(), access.Mint()]);

            foreach (var (result, reason) in new[] { (missing, "missing"), (empty, "missing"), (doubled, "more than one") })
            {
                Assert.False(result.Next.Ran);
                Assert.Equal(StatusCodes.Status403Forbidden, result.Context.Response.StatusCode);
                Assert.Contains(result.Logs.Entries, entry => entry.Message.Contains($"Cloudflare Access check: {reason} (request ", StringComparison.Ordinal));
            }

            // Nothing needed Cloudflare's keys
            Assert.Equal(0, access.Fetches);
        }

        // The key is the one the token names, and no other is tried, even when it would pass
        [Fact]
        public async Task KeyIsFoundByItsIdAlone()
        {
            var access = NewAccess();
            access.Rotate();

            var otherKeysSignature = await RunAsync(access, true, "/admin", tokens: access.Mint(signWith: keys.Next));
            var unknownId = await RunAsync(access, true, "/admin", tokens: access.Mint(header: head => head["kid"] = "made-up-key"));
            var nextKey = await RunAsync(access, true, "/admin", tokens: access.Mint(header: head => head["kid"] = access.NextKeyId, signWith: keys.Next));

            Assert.False(otherKeysSignature.Next.Ran);
            Assert.Contains(otherKeysSignature.Logs.Entries, entry => entry.Message.Contains("check: bad signature (", StringComparison.Ordinal));
            Assert.False(unknownId.Next.Ran);
            Assert.Contains(unknownId.Logs.Entries, entry => entry.Message.Contains("check: unknown key (", StringComparison.Ordinal));
            Assert.True(nextKey.Next.Ran);
        }

        // Without usable keys nothing can be checked, so nothing is shown, and the visitor is told to come back
        [Fact]
        public async Task NoUsableKeysIsA503WithRetryAfter()
        {
            var access = NewAccess();
            access.FailWith(AccessFailure.Throw);

            var result = await RunAsync(access, true, "/admin", tokens: access.Mint());

            Assert.False(result.Next.Ran);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.Context.Response.StatusCode);
            Assert.Equal("60", result.Context.Response.Headers.RetryAfter);
            Assert.Equal("text/plain; charset=utf-8", result.Context.Response.ContentType);
            Assert.Equal("Cloudflare Access couldn't be checked just now, so this page isn't shown. Try again in a minute.", result.Body);
            Assert.Contains(result.Logs.Entries, entry => entry.Message.StartsWith("A request for an admin page wasn't shown, because the Cloudflare Access check has no usable keys", StringComparison.Ordinal));
        }

        // The path segment /admin in any letter case, and nothing that only starts with the same letters
        [Theory]
        [InlineData("/admin", true)]
        [InlineData("/ADMIN", true)]
        [InlineData("/Admin/", true)]
        [InlineData("/admin/nothing-here", true)]
        [InlineData("/", false)]
        [InlineData("/administrator", false)]
        [InlineData("/adminx/y", false)]
        [InlineData("/signin", false)]
        [InlineData("/css/site.css", false)]
        public void PathUnderAdminIsGuarded(string path, bool guarded)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path;

            Assert.Equal(guarded, CloudflareAccessCheck.Guards(context));
        }

        // Wherever a page is, the Admin and Owner policies need the token. Another policy, or none, doesn't.
        [Theory]
        [InlineData(Policies.Admin, true)]
        [InlineData(Policies.Owner, true)]
        [InlineData("ServerAdmin", false)]
        [InlineData(null, false)]
        public void PageWithTheAdminOrOwnerPolicyIsGuarded(string? policy, bool guarded)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = "/elsewhere";
            context.SetEndpoint(Page(new AuthorizeAttribute { Policy = policy }));

            Assert.Equal(guarded, CloudflareAccessCheck.Guards(context));
        }

        [Fact]
        public void PageWithoutAPolicyIsNotGuarded()
        {
            var context = new DefaultHttpContext();
            context.Request.Path = "/elsewhere";
            context.SetEndpoint(Page(new AllowAnonymousAttribute()));

            Assert.False(CloudflareAccessCheck.Guards(context));
        }
    }
}
