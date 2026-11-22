using System.Net;
using System.Security.Claims;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Tests.Database;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Tests
{
    // A sign-in from the last 15 minutes, for the controls that are hard to take back: a sign-in again goes to Discord even
    // for someone signed in and comes back with a new session, every sign-in asks Discord for consent, and the sender takes
    // the time from the session's own row, through the app's own authentication, as a page's handler sees it
    [Collection("Database")]
    public class FreshSignInTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        // The controls that need a fresh sign-in, a status text and a leave included
        public static TheoryData<string> FreshKinds => [RequestKinds.BotRestart, RequestKinds.PostsPause, RequestKinds.SteamReconnect, RequestKinds.SteamRenew,
            RequestKinds.StatusSet, RequestKinds.GuildLeave];

        // fresh=1 skips the shortcut that sends someone signed in straight back; any other value doesn't. Fails if /signin
        // ignores fresh.
        [Fact]
        public async Task FreshSignInGoesToDiscordEvenWhenSignedIn()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var owner = factory.Visitor("203.0.113.160");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);

            var fresh = await owner.Client.GetAsync("/signin?fresh=1&returnUrl=%2Fadmin%2Fbot%2Fcontrols", Token);
            Assert.Equal(HttpStatusCode.Redirect, fresh.StatusCode);
            Assert.Equal(factory.Discord.AuthorizeEndpoint, fresh.Headers.Location!.GetLeftPart(UriPartial.Path));

            foreach (var other in new[] { "yes", "true", "0", "" })
            {
                var back = await owner.Client.GetAsync($"/signin?fresh={other}&returnUrl=%2Fadmin%2Fbot%2Fcontrols", Token);
                Assert.Equal("/admin/bot/controls", SignInSteps.PathOf(back.Headers.Location));
            }
        }

        // Back from Discord, the browser has a new session, dated now, and the one it had is gone, so a copy of the old cookie
        // stops working; the way back is the page the link named
        [Fact]
        public async Task FreshSignInReturnsWithANewSessionAndTheOldOneEnded()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.161");
            var old = await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);

            factory.Clock.Advance(TimeSpan.FromMinutes(20));
            var back = await SignInSteps.SignInAgainAsync(factory, owner, TestUsers.Owner, "/admin/bot/controls");

            Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
            Assert.Equal("/admin/bot/controls", SignInSteps.PathOf(back.Headers.Location));
            Assert.NotEqual(old, owner.SessionKey);
            Assert.Null(await db.SessionAsync(old));

            var session = await db.SessionAsync(owner.SessionKey!);
            Assert.Equal(factory.Clock.GetUtcNow().UtcDateTime.Ticks, session!.created_at.Ticks);
            Assert.Equal(2, (await db.AuditAsync()).Count(entry => entry.action == "auth.signin"));
        }

        // Discord's own Authorize screen on every sign-in, an ordinary one and a sign-in again. Fails if prompt=consent isn't
        // sent.
        [Fact]
        public async Task EverySignInAsksDiscordForConsent()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var owner = factory.Visitor("203.0.113.162");

            var first = await SignInSteps.StartAsync(owner, authorizeEndpoint: factory.Discord.AuthorizeEndpoint);
            Assert.Equal("consent", first.Query["prompt"]);

            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var again = await SignInSteps.StartAgainAsync(factory, owner, "/admin/bot/controls");
            Assert.Equal("consent", again.Query["prompt"]);
        }

        // Each control that needs a fresh sign-in is sent at 14 minutes 59 seconds after signing in, and refused at 15
        // minutes 1 second, with no request written, and the refusal recorded. The requests are cleared between, so the
        // bot's limit can't answer first. Fails if the check is gone, its window is longer, or a status text doesn't need it.
        [Theory]
        [MemberData(nameof(FreshKinds))]
        public async Task ASignInIsFreshForFifteenMinutes(string kind)
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.163");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);

            factory.Clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));

            await using (var scope = await RequestSteps.ScopeAsync(factory, owner))
                Assert.IsType<SendOutcome.Sent>(await scope.SendAsync(RequestSteps.Order(kind)));

            Assert.Single(await db.SentAsync());
            await db.ClearAsync(MariaDbFixture.RequestTable, MariaDbFixture.AuditTable);
            factory.Clock.Advance(TimeSpan.FromSeconds(2));

            await using (var scope = await RequestSteps.ScopeAsync(factory, owner, "203.0.113.164"))
            {
                var refused = Assert.IsType<SendOutcome.Refused>(await scope.SendAsync(RequestSteps.Order(kind)));
                var signedIn = FreshSignIn.SignedInAt(scope.Context)!.Value.UtcDateTime;

                Assert.Equal(SendRefusal.NotFresh, refused.Why);
                Assert.StartsWith($"This needs a sign-in from the last 15 minutes, and yours is from {RequestWords.Clock(signedIn)}, so nothing was sent. Sign in again with Discord",
                    refused.Words);
                Assert.Equal(StatusCodes.Status403Forbidden, RequestWords.Status(refused));
            }

            Assert.Empty(await db.RequestsAsync());
            var audit = Assert.Single(await db.AuditAsync());
            Assert.Equal((kind, AuditOutcome.Refused, "request", RequestWords.NotFreshDetail, "203.0.113.164"), (audit.action, audit.outcome, audit.target_type!, audit.detail!, audit.ip!));
            Assert.Null(audit.target_id);

            // The controls page draws no form for it now, only the way to sign in again, back to its own section, and so does
            // a leave's own page, for a server MinerBot is in, back to itself
            if (ControlsHandler(kind) is { } handler)
            {
                var page = await RequestSteps.ControlsAsync(owner);
                Assert.DoesNotContain(handler, page.Handlers);
                Assert.Contains(WebUtility.HtmlEncode(FreshSignIn.SignInAgainLink("/admin/bot/controls#" + ControlsSection(kind))), page.Html);
            }
            else
            {
                var leave = $"/admin/servers/{StatusSteps.ServerId}/leave";
                await StatusSteps.ServersAsync(db);
                var page = await owner.Client.GetStringAsync(leave, Token);

                Assert.Contains("MinerBot leaves this server", WebUtility.HtmlDecode(page));
                Assert.DoesNotContain("name=\"confirm\"", page);
                Assert.DoesNotContain($"action=\"{leave}\"", page);
                Assert.Contains(WebUtility.HtmlEncode(FreshSignIn.SignInAgainLink(leave)), page);
            }
        }

        // A refusal for a sign-in that's become too old since the page was drawn shows what was typed, read-only and encoded,
        // to copy, in place of the form, with the way to sign in again; nothing is sent
        [Fact]
        public async Task ARefusalForAnOldSignInShowsWhatWasTyped()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.169");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var page = await RequestSteps.ControlsAsync(owner);
            Assert.Contains(ControlWords.Pause, page.Handlers);

            factory.Clock.Advance(TimeSpan.FromMinutes(16));
            var refused = await RequestSteps.PostControlAsync(owner, page, ControlWords.Pause, ("reason", "<b>Back</b> after the update"), ("confirm", "pause"));
            var html = await refused.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("Sign in again with Discord, then send it again. What you typed is below, to copy.", WebUtility.HtmlDecode(html));
            Assert.Contains("What you typed: <bdi class=\"revealed\">&lt;b&gt;Back&lt;/b&gt; after the update</bdi>", html);
            Assert.DoesNotContain("<b>Back", html);
            Assert.DoesNotContain(ControlWords.Pause, RequestSteps.ReadControls(html).Handlers);
            Assert.Contains(WebUtility.HtmlEncode(FreshSignIn.SignInAgainLink("/admin/bot/controls#posts")), html);
            Assert.Empty(await db.SentAsync());
            PageTests.AssertAccessible("a refusal for a sign-in that's too old", html);
        }

        // The controls page's handler for each kind that needs a fresh sign-in, and its section
        static string? ControlsHandler(string kind) => kind switch
        {
            RequestKinds.BotRestart => ControlWords.Restart,
            RequestKinds.PostsPause => ControlWords.Pause,
            RequestKinds.SteamReconnect => ControlWords.ReconnectSteam,
            RequestKinds.SteamRenew => ControlWords.RenewToken,
            RequestKinds.StatusSet => ControlWords.SetStatus,
            _ => null
        };

        static string ControlsSection(string kind) => kind switch
        {
            RequestKinds.BotRestart => "restart",
            RequestKinds.PostsPause => "posts",
            RequestKinds.StatusSet => "status-text",
            _ => "steam"
        };

        // The time is the session row's created_at, which the sign-in wrote and RetrieveAsync reads on every request: moved
        // 20 minutes back, a restart is refused, and moved to now, it's sent. Fails if the time comes from anywhere else,
        // such as the session's end or its last use.
        [Fact]
        public async Task FreshnessIsTheSessionRowsSignInTime()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.165");
            var key = await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var hash = (await db.SessionAsync(key))!.id_hash;

            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.SessionTable} SET created_at = @at WHERE id_hash = @hash", new { at = now.AddMinutes(-20), hash });

            await using (var scope = await RequestSteps.ScopeAsync(factory, owner))
                Assert.Equal(SendRefusal.NotFresh, Assert.IsType<SendOutcome.Refused>(await scope.SendAsync(RequestSteps.Order(RequestKinds.BotRestart))).Why);

            Assert.Empty(await db.SentAsync());
            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.SessionTable} SET created_at = @at WHERE id_hash = @hash", new { at = now, hash });

            await using (var scope = await RequestSteps.ScopeAsync(factory, owner))
                Assert.IsType<SendOutcome.Sent>(await scope.SendAsync(RequestSteps.Order(RequestKinds.BotRestart)));

            Assert.Single(await db.SentAsync());
        }

        // The safe directions and the controls that do nothing the schedule doesn't need no fresh sign-in: 20 minutes after
        // signing in, resuming posts, the default status, the jobs and the cache clear are each sent. Fails if one of them
        // is put on the fresh list.
        [Fact]
        public async Task ResumeDefaultStatusAndJobsNeedNoFreshSignIn()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.166");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);

            factory.Clock.Advance(TimeSpan.FromMinutes(20));

            await using var scope = await RequestSteps.ScopeAsync(factory, owner);
            Assert.False(FreshSignIn.IsFresh(scope.Context, factory.Clock.GetUtcNow()));

            foreach (var kind in new[] { RequestKinds.PostsResume, RequestKinds.JobColors, RequestKinds.JobNotifications, RequestKinds.CachesClear })
                Assert.True(await scope.SendAsync(RequestSteps.Order(kind)) is SendOutcome.Sent, $"{kind} wasn't sent");

            // Back to the default status is the status kind with no text
            Assert.IsType<SendOutcome.Sent>(await scope.SendAsync(new SendOrder(RequestKinds.StatusSet, RequestFields.NewKey())));

            var sent = await db.SentAsync();
            Assert.Equal([RequestKinds.PostsResume, RequestKinds.JobColors, RequestKinds.JobNotifications, RequestKinds.CachesClear, RequestKinds.StatusSet],
                sent.Select(row => row.kind));
            Assert.Null(sent[^1].argument);
        }

        // The forms' antiforgery token is tied to the person, not the session, so a form drawn before a sign-in again still
        // posts after it, from the same browser: here the blacklist's add form, the one the pages have now. Fails if signing
        // in again expires the forms already open.
        [Fact]
        public async Task AFormDrawnBeforeASignInAgainStillPosts()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.168");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var token = SignInSteps.FormToken(await owner.Client.GetStringAsync("/admin/blacklist/add", Token));

            factory.Clock.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(HttpStatusCode.Redirect, (await SignInSteps.SignInAgainAsync(factory, owner, TestUsers.Owner, "/admin/blacklist/add")).StatusCode);

            var added = await BlacklistSteps.CraftedAsync(owner, "/admin/blacklist/add", token, ("steamid", "12345"), ("name", "Test Player"), ("reason", "Testing"),
                ("expires", ""), ("notes", ""));

            Assert.Equal(HttpStatusCode.Redirect, added.StatusCode);
            Assert.Equal("/admin/blacklist/12345?done=added", SignInSteps.PathOf(added.Headers.Location));
        }

        // The link a control draws while the sign-in isn't fresh, with its way back to the control in small letters, as
        // Cloudflare Access's path rule needs, and the round trip landing there, anchor and all
        [Fact]
        public async Task TheSignInAgainLinkComesBackLowercase()
        {
            Assert.Equal("/signin?fresh=1&returnUrl=%2Fadmin%2Fbot%2Fcontrols%23restart", FreshSignIn.SignInAgainLink("/admin/bot/controls#restart"));

            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var owner = factory.Visitor("203.0.113.167");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);

            var back = await SignInSteps.SignInAgainAsync(factory, owner, TestUsers.Owner, "/admin/bot/controls#restart");

            Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
            Assert.Equal("/admin/bot/controls#restart", SignInSteps.PathOf(back.Headers.Location));
        }

        // The rule itself: fresh from the sign-in to just before 15 minutes, and never without a time, or with one ahead of
        // the clock
        [Fact]
        public void TheWindowIsFifteenMinutesAndFailsClosed()
        {
            var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

            Assert.Equal(TimeSpan.FromMinutes(15), FreshSignIn.Window);
            Assert.True(FreshSignIn.IsFresh(Signed(now), now));
            // The ticket keeps its time in whole seconds, so the last fresh moment is the second before
            Assert.True(FreshSignIn.IsFresh(Signed(now.AddMinutes(-15).AddSeconds(1)), now));
            Assert.False(FreshSignIn.IsFresh(Signed(now.AddMinutes(-15).AddMilliseconds(999)), now));
            Assert.False(FreshSignIn.IsFresh(Signed(now.AddMinutes(-15)), now));
            Assert.False(FreshSignIn.IsFresh(Signed(now.AddSeconds(1)), now));
            Assert.False(FreshSignIn.IsFresh(Signed(null), now));
            Assert.False(FreshSignIn.IsFresh(new DefaultHttpContext(), now));
            Assert.Equal(now.AddMinutes(15), FreshSignIn.Until(Signed(now)));

            // Which requests need it: the status kind only with a text
            Assert.True(FreshSignIn.Needs(RequestKinds.Find(RequestKinds.StatusSet)!, "Back soon"));
            Assert.False(FreshSignIn.Needs(RequestKinds.Find(RequestKinds.StatusSet)!, null));
            Assert.Equal([RequestKinds.SteamRenew, RequestKinds.PostsPause, RequestKinds.SteamReconnect, RequestKinds.GuildLeave, RequestKinds.BotRestart],
                RequestKinds.All.Where(kind => FreshSignIn.Needs(kind, null)).Select(kind => kind.Word));
        }

        static HttpContext Signed(DateTimeOffset? at)
        {
            var context = new DefaultHttpContext();
            var properties = new AuthenticationProperties { IssuedUtc = at };
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(), properties, DashboardAuth.CookieScheme);
            context.Features.Set<IAuthenticateResultFeature>(new Feature(AuthenticateResult.Success(ticket)));
            return context;
        }

        sealed class Feature(AuthenticateResult result) : IAuthenticateResultFeature
        {
            public AuthenticateResult? AuthenticateResult { get; set; } = result;
        }
    }
}
