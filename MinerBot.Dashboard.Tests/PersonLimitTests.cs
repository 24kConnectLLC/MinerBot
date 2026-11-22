using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Requests.Models;
using DataAccessLibrary.Tests.Database;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Tests
{
    // The dashboard's own limits on each person, so one person, or a stolen session, can't spend what everyone shares: an
    // hourly share of the bot's budget for each kind an Admin can ask for, and 60 requests an hour in all, counted from the
    // person's own rows in any state, the sign-in alerts left out. A refusal is recorded, unless nobody pressed anything.
    [Collection("Database")]
    public class PersonLimitTests(MariaDbFixture fixture)
    {
        static async Task<TestBrowser> SignedInAsync(DashboardFactory factory, TestUser user, string address)
        {
            var browser = factory.Visitor(address);
            await SignInSteps.SignedInAsync(factory, browser, user);
            return browser;
        }

        static async Task<SendOutcome> SendAsync(DashboardFactory factory, TestBrowser browser, SendOrder order)
        {
            await using var scope = await RequestSteps.ScopeAsync(factory, browser);
            return await scope.SendAsync(order);
        }

        static IEnumerable<DateTime> Minutes(DateTime now, int count, int from = 50) => Enumerable.Range(0, count).Select(i => now.AddMinutes(-from).AddSeconds(i));

        static Task<List<AuditEntry>> RefusalsAsync(MariaDbFixture db) =>
            db.RootQueryAsync<AuditEntry>($"SELECT * FROM {MariaDbFixture.AuditTable} WHERE outcome = 'refused' ORDER BY id");

        // The app with a count of each person's requests that answers 200 ms late, as a busy database's can, so forms posted
        // at once all count before any of them is written, unless the sender makes them take turns
        static DashboardFactory SlowCountFactory(MariaDbFixture db) => new(db, new DashboardFactoryOptions
        {
            Services = services => services.Replace(ServiceDescriptor.Transient<IBotRequestReads>(provider =>
                new SlowCountReads(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider), TimeSpan.FromMilliseconds(200))))
        });

        // The controls page's cache-clear form, posted the number of times given at once, each with a new key, as a script
        // can post it: how many were sent, and how many refused for a limit, which is every other answer
        static async Task<(int Sent, int Refused)> ClearsAtOnceAsync(TestBrowser browser, int forms)
        {
            var page = await RequestSteps.ControlsAsync(browser);
            var responses = await Task.WhenAll(Enumerable.Range(0, forms)
                .Select(_ => RequestSteps.PostControlAsync(browser, ControlWords.ClearCaches, page.Token, RequestFields.NewKey())));
            var statuses = responses.Select(response => response.StatusCode).ToList();

            Assert.All(statuses, status => Assert.Contains(status, new[] { HttpStatusCode.Redirect, HttpStatusCode.TooManyRequests }));
            return (statuses.Count(status => status == HttpStatusCode.Redirect), statuses.Count(status => status == HttpStatusCode.TooManyRequests));
        }

        // Each kind an Admin can ask for has its own share for each person: an Admin who has used up their 20 cache clears this
        // hour is refused another, while the Owner, who has asked for none, isn't, and every share is the kind's own. Fails
        // if the count is everyone's, or a share is the wrong size.
        [Fact]
        public async Task EachPersonHasTheirOwnCountForEachKind()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "203.0.113.190");
            var owner = await SignedInAsync(factory, TestUsers.Owner, "203.0.113.191");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            await RequestSteps.AddRefusedAsync(db, RequestKinds.CachesClear, TestUsers.Admin, Minutes(now, 19));
            Assert.IsType<SendOutcome.Sent>(await SendAsync(factory, admin, RequestSteps.Order(RequestKinds.CachesClear)));

            var refused = Assert.IsType<SendOutcome.Refused>(await SendAsync(factory, admin, RequestSteps.Order(RequestKinds.CachesClear)));
            Assert.Equal(SendRefusal.PersonLimit, refused.Why);
            Assert.StartsWith("You've asked for this 20 times in the last hour, the most the dashboard sends for one person, so nothing was sent.", refused.Words);
            Assert.IsType<SendOutcome.Sent>(await SendAsync(factory, owner, RequestSteps.Order(RequestKinds.CachesClear)));

            // The other shares, each used up by the Admin alone
            foreach (var (kind, share) in new[] { (RequestKinds.JobNotifications, 10), (RequestKinds.GuildTestMessage, 10), (RequestKinds.GuildRefresh, 20),
                (RequestKinds.SteamLookup, 20) })
            {
                Assert.Equal(share, PersonLimits.Share(RequestKinds.Find(kind)!));
                await RequestSteps.AddRefusedAsync(db, kind, TestUsers.Admin, Minutes(now, share));

                var over = Assert.IsType<SendOutcome.Refused>(await SendAsync(factory, admin, RequestSteps.Order(kind)));
                Assert.Equal(SendRefusal.PersonLimit, over.Why);
                Assert.StartsWith($"You've asked for this {share} times in the last hour", over.Words);
                Assert.IsType<SendOutcome.Sent>(await SendAsync(factory, owner, RequestSteps.Order(kind)));
            }

            // The Owner's kinds have no share of their own, only the total
            Assert.All(RequestKinds.All.Where(kind => kind.OwnerOnly), kind => Assert.Null(PersonLimits.Share(kind)));
            Assert.Equal(5, (await db.SentAsync()).Count(row => row.actor_id == TestUsers.Owner.Id));
        }

        // Every kind counts toward a person's 60 an hour, in any state, but the sign-in alerts, which every sign-in leaves
        // whoever it is: 59 requests and ten alerts leave room for one more, and then there's none, with the words of the
        // total. Fails if the alerts count, or the total isn't 60.
        [Fact]
        public async Task EveryKindButTheAlertCountsTowardTheHourlyTotal()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "203.0.113.192");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            await RequestSteps.AddRefusedAsync(db, RequestKinds.PostsResume, TestUsers.Owner, Minutes(now, 30, from: 55));
            await RequestSteps.AddRefusedAsync(db, RequestKinds.StatusSet, TestUsers.Owner, Minutes(now, 29, from: 45));
            await RequestSteps.AddRefusedAsync(db, RequestKinds.OwnerSigninAlert, TestUsers.Owner, Minutes(now, 10, from: 40));

            Assert.IsType<SendOutcome.Sent>(await SendAsync(factory, owner, RequestSteps.Order(RequestKinds.JobColors)));

            var refused = Assert.IsType<SendOutcome.Refused>(await SendAsync(factory, owner, RequestSteps.Order(RequestKinds.BotRestart)));
            Assert.Equal(SendRefusal.PersonLimit, refused.Why);
            Assert.Equal(now.AddMinutes(5), refused.After);
            Assert.Equal($"You've sent 60 requests to the bot in the last hour, the most the dashboard sends for one person, so nothing was sent. You can send another after {RequestWords.Clock(now.AddMinutes(5))}.",
                refused.Words);
        }

        // The refusal says how many, and when another fits, which the 429 gives as Retry-After too, and the audit log records
        // it once, with the kind, the person, why and the address
        [Fact]
        public async Task ALimitRefusalSaysWhenAndIsAudited()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "203.0.113.193");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            await RequestSteps.AddRefusedAsync(db, RequestKinds.JobNotifications, TestUsers.Admin, Enumerable.Range(0, 10).Select(i => now.AddMinutes(-55 + i)));

            await using var scope = await RequestSteps.ScopeAsync(factory, admin, "203.0.113.194");
            var refused = Assert.IsType<SendOutcome.Refused>(await scope.SendAsync(RequestSteps.Order(RequestKinds.JobNotifications)));

            Assert.Equal(SendRefusal.PersonLimit, refused.Why);
            Assert.Equal(now.AddMinutes(5), refused.After);
            Assert.Equal("You've asked for this 10 times in the last hour, the most the dashboard sends for one person, so nothing was sent. "
                + $"You can ask again after {RequestWords.Clock(now.AddMinutes(5))}.", refused.Words);
            Assert.Equal(StatusCodes.Status429TooManyRequests, RequestWords.Status(refused));
            Assert.Equal(300, RequestWords.RetryAfter(refused, now));

            var page = new DefaultHttpContext();
            RequestWords.Answer(page.Response, refused, now.AddSeconds(30.5));
            Assert.Equal((StatusCodes.Status429TooManyRequests, "270"), (page.Response.StatusCode, page.Response.Headers.RetryAfter.ToString()));

            var audit = Assert.Single(await RefusalsAsync(db));
            Assert.Equal((RequestKinds.JobNotifications, AuditRole.Admin, (ulong?)TestUsers.Admin.Id, AuditWords.RequestTarget), (audit.action, audit.actor_role, audit.actor_id, audit.target_type!));
            Assert.Equal(("Over the dashboard's limit for one person", "203.0.113.194", scope.Context.TraceIdentifier), (audit.detail!, audit.ip!, audit.request_id!));
            Assert.Null(audit.target_id);
            Assert.DoesNotContain(await db.SentAsync(), row => row.state == null);
        }

        // A refusal before anything is written, for the role, the typed word, the rules or the bot's limit, writes nothing,
        // so it never counts toward a person's limit: an Admin one request short of the total, refused many times, can still
        // send that one
        [Fact]
        public async Task RefusalsBeforeWritingDontCount()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "203.0.113.195");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            await RequestSteps.AddRefusedAsync(db, RequestKinds.CachesClear, TestUsers.Admin, Minutes(now, 15));
            await RequestSteps.AddRefusedAsync(db, RequestKinds.JobNotifications, TestUsers.Admin, Minutes(now, 9, from: 49));
            await RequestSteps.AddRefusedAsync(db, RequestKinds.GuildTestMessage, TestUsers.Admin, Minutes(now, 9, from: 48));
            await RequestSteps.AddRefusedAsync(db, RequestKinds.GuildRefresh, TestUsers.Admin, Minutes(now, 15, from: 47));
            await RequestSteps.AddRefusedAsync(db, RequestKinds.SteamLookup, TestUsers.Admin, Minutes(now, 11, from: 46));
            var rows = (await db.RequestsAsync()).Count;

            await RequestSteps.TakenAsync(db, factory, RequestKinds.JobNotifications, now.AddSeconds(-20));

            foreach (var kind in RequestKinds.All.Where(kind => kind.OwnerOnly))
                Assert.Equal(SendRefusal.Forbidden, Assert.IsType<SendOutcome.Refused>(await SendAsync(factory, admin, RequestSteps.Order(kind.Word))).Why);

            Assert.Equal(SendRefusal.Rule, Assert.IsType<SendOutcome.Refused>(await SendAsync(factory, admin, RequestSteps.Order(RequestKinds.SteamLookup) with { Argument = "not a name" })).Why);
            Assert.Equal(SendRefusal.BotLimit, Assert.IsType<SendOutcome.Refused>(await SendAsync(factory, admin, RequestSteps.Order(RequestKinds.JobNotifications))).Why);
            Assert.Equal(rows + 1, (await db.RequestsAsync()).Count);

            Assert.IsType<SendOutcome.Sent>(await SendAsync(factory, admin, RequestSteps.Order(RequestKinds.CachesClear)));
            Assert.Equal(SendRefusal.PersonLimit, Assert.IsType<SendOutcome.Refused>(await SendAsync(factory, admin, RequestSteps.Order(RequestKinds.CachesClear))).Why);
        }

        // The cache clear sent after an armor review counts like any other, and past the share it's refused quietly, with no
        // audit row, since nobody pressed anything; the same refusal from a press is recorded
        [Fact]
        public async Task TheAutomaticClearCountsAndIsSkippedQuietly()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "203.0.113.196");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            await RequestSteps.AddRefusedAsync(db, RequestKinds.CachesClear, TestUsers.Admin, Minutes(now, 19));

            Assert.IsType<SendOutcome.Sent>(await SendAsync(factory, admin, RequestSteps.Order(RequestKinds.CachesClear) with { Automatic = true }));

            var quiet = Assert.IsType<SendOutcome.Refused>(await SendAsync(factory, admin, RequestSteps.Order(RequestKinds.CachesClear) with { Automatic = true }));
            Assert.Equal(SendRefusal.PersonLimit, quiet.Why);
            Assert.Empty(await RefusalsAsync(db));

            Assert.Equal(SendRefusal.PersonLimit, Assert.IsType<SendOutcome.Refused>(await SendAsync(factory, admin, RequestSteps.Order(RequestKinds.CachesClear))).Why);
            Assert.Equal([RequestKinds.CachesClear], (await RefusalsAsync(db)).Select(entry => entry.action));
        }

        // Forms posted at once, each with its own key, as two tabs or a script with a stolen session can post them, take turns
        // for each person, so each counts what the one before it wrote: an Admin with 15 cache clears this hour who posts 10
        // more at once has 5 sent, and the Owner, 58 requests into the hour, 2 of 5; the rest are refused for the limit.
        // Each count answers late, so without the turns every form counts before any is written and all are sent, which the
        // bot can't stop, since its limits are for everyone. Fails if one person's sends can overlap.
        [Fact]
        public async Task FormsPostedAtOnceKeepThePersonsLimits()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = SlowCountFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "203.0.113.197");
            var owner = await SignedInAsync(factory, TestUsers.Owner, "203.0.113.198");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            await RequestSteps.AddRefusedAsync(db, RequestKinds.CachesClear, TestUsers.Admin, Minutes(now, 15));
            await RequestSteps.AddRefusedAsync(db, RequestKinds.PostsResume, TestUsers.Owner, Minutes(now, 30, from: 55));
            await RequestSteps.AddRefusedAsync(db, RequestKinds.StatusSet, TestUsers.Owner, Minutes(now, 28, from: 45));

            Assert.Equal((5, 5), await ClearsAtOnceAsync(admin, 10));
            Assert.Equal((2, 3), await ClearsAtOnceAsync(owner, 5));

            var sent = await db.SentAsync();
            Assert.Equal(20, sent.Count(row => row.actor_id == TestUsers.Admin.Id && row.kind == RequestKinds.CachesClear));
            Assert.Equal(60, sent.Count(row => row.actor_id == TestUsers.Owner.Id));
        }

        // One form posted three times at once, as a double click can post it, is one request even when it's the person's last
        // before their share: the look for its key takes its turn too, so the sends after the first find that request and
        // lead to it, where counting first would refuse them for the limit. Fails if the turn starts after the look and a
        // limit that refuses doesn't look for the key again; the next test fails on the turn alone.
        [Fact]
        public async Task AFormPostedAgainAtOnceIsOneRequestAtTheLimit()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = SlowCountFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "203.0.113.199");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            await RequestSteps.AddRefusedAsync(db, RequestKinds.CachesClear, TestUsers.Admin, Minutes(now, 19));
            var page = await RequestSteps.ControlsAsync(admin);

            var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => RequestSteps.PostControlAsync(admin, page, ControlWords.ClearCaches)));

            var row = Assert.Single(await db.SentAsync(), row => row.state == null);
            Assert.All(responses, response => Assert.Equal((HttpStatusCode.Redirect, $"/admin/bot/controls?request={row.id}#result"),
                (response.StatusCode, SignInSteps.PathOf(response.Headers.Location))));
            Assert.Empty(await RefusalsAsync(db));
        }

        // A double click as the sign-in's 15 minutes end: the first POST, still fresh, holds the person's turn while it counts,
        // and the second, 20 seconds later on the clock and no longer fresh, waits for its turn before it looks for the key,
        // so it finds the first request rather than meeting the fresh sign-in it no longer has, which no second look follows.
        // The second send runs on the test's thread up to its first wait, the turn or a look made before it, so a look made
        // first is counted as the send starts, and ends before the first sending is let go. Fails if the turn starts after
        // the look, when the second's look misses and its refusal is recorded.
        [Fact]
        public async Task ADoubleClickAsTheSignInGoesStaleShowsTheFirstRequest()
        {
            var db = await fixture.ReadyAsync();
            var hold = new SendHold();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services =>
                {
                    services.Replace(ServiceDescriptor.Transient<IBotRequestReads>(provider => new HeldCountReads(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider), hold)));
                    services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider => new CountedLooks(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider), hold)));
                }
            });
            var owner = await SignedInAsync(factory, TestUsers.Owner, "203.0.113.231");
            var bound = TimeSpan.FromSeconds(10);
            var token = TestContext.Current.CancellationToken;
            var restart = RequestSteps.Order(RequestKinds.BotRestart);

            factory.Clock.Advance(FreshSignIn.Window - TimeSpan.FromSeconds(10));
            await using var firstScope = await RequestSteps.ScopeAsync(factory, owner);
            Interlocked.Exchange(ref hold.Armed, 1);
            var first = firstScope.SendAsync(restart);
            await hold.Entered.Task.WaitAsync(bound, token);

            factory.Clock.Advance(TimeSpan.FromSeconds(20));
            await using var secondScope = await RequestSteps.ScopeAsync(factory, owner);
            var looks = Volatile.Read(ref hold.Looks);
            var lookDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref hold.LookDone, lookDone);
            var second = secondScope.SendAsync(restart);
            var lookedFirst = Volatile.Read(ref hold.Looks) != looks;

            if (lookedFirst)
                await lookDone.Task.WaitAsync(bound, token);

            hold.Release.SetResult();

            var sent = Assert.IsType<SendOutcome.Sent>(await first.WaitAsync(bound, token));
            Assert.False(sent.Repeated);
            var again = await second.WaitAsync(bound, token);
            Assert.True(again == new SendOutcome.Sent(sent.Id, Repeated: true), $"The second sending ended {again} (looked before its turn: {lookedFirst})");
            Assert.Empty(await RefusalsAsync(db));
            Assert.Single(await db.SentAsync());
        }

        // A person's sends take turns, and nobody waits for anyone else: the same person's next turn waits for the one they
        // hold, and past its limit ends as a database that didn't answer does, with nothing done, while another person's
        // comes at once. A turn that waits comes when the one before it ends, and ending a turn twice gives back one place.
        // Fails if the wait has no limit, the turns are everyone's, or a turn isn't given back exactly once.
        [Fact]
        public async Task APersonsSendsTakeTurnsThatWaitAtMostTheirLimit()
        {
            var turns = new PersonTurns { WaitLimit = TimeSpan.FromMilliseconds(500) };
            var bound = TimeSpan.FromSeconds(5);
            var token = TestContext.Current.CancellationToken;
            var first = await turns.TakeAsync(TestUsers.Admin.Id);

            (await turns.TakeAsync(TestUsers.Owner.Id).WaitAsync(bound, token)).Dispose();

            var started = Stopwatch.GetTimestamp();
            await Assert.ThrowsAsync<DashboardUnavailableException>(() => turns.TakeAsync(TestUsers.Admin.Id).WaitAsync(bound, token));
            Assert.InRange(Stopwatch.GetElapsedTime(started), TimeSpan.FromMilliseconds(400), bound);

            var waiting = turns.TakeAsync(TestUsers.Admin.Id);
            Assert.False(waiting.IsCompleted);
            first.Dispose();
            first.Dispose();

            using var second = await waiting.WaitAsync(bound, token);
            await Assert.ThrowsAsync<DashboardUnavailableException>(() => turns.TakeAsync(TestUsers.Admin.Id).WaitAsync(bound, token));
        }

        // A turn's wait outlasts the send before it on a database that answers slowly, whose several calls each take their own
        // limit, so a double click's second sending finds its first rather than the 503 that says nothing was changed: it's
        // as long as nginx waits for a page less one database call's limit, which the send needs for its own answer. Fails if
        // the wait is one call's limit, which five calls of 3 seconds outlast.
        [Fact]
        public void ATurnsWaitOutlastsASendOnASlowDatabase()
        {
            var call = new BotRequestQueue(null!, null!).CallLimit;
            var nginx = Regex.Match(DashboardLogin.DeployFile("minerbot-dashboard.conf"), @"(?m)^\s*proxy_read_timeout (\d+)s;");

            Assert.True(nginx.Success, "nginx's site has no proxy_read_timeout");
            Assert.Equal(TimeSpan.FromSeconds(int.Parse(nginx.Groups[1].Value, CultureInfo.InvariantCulture)) - call, new PersonTurns().WaitLimit);
            Assert.True(new PersonTurns().WaitLimit > 5 * TimeSpan.FromSeconds(3), "Five calls of 3 seconds outlast the wait");
        }

        // When the count is over the share, as request.sh's requests, which have no limit for one person, can leave it, the
        // time is the one after which it's back under, not the oldest; a row whose time can't be read counts, but never
        // leaves the hour
        [Fact]
        public void TheTimeIsWhenThereIsRoomAgain()
        {
            var now = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
            var clear = RequestKinds.Find(RequestKinds.CachesClear)!;
            var rows = Enumerable.Range(0, 22).Select(i => new AskedRow { kind = RequestKinds.CachesClear, created_at = now.AddMinutes(-50 + i) }).ToList();

            var check = PersonLimits.Check(clear, rows, now);
            Assert.Equal((true, true, 22, 20, (DateTime?)now.AddMinutes(12)), (check.Reached, check.ForKind, check.Count, check.Limit, check.After));

            rows.Insert(0, new AskedRow { kind = RequestKinds.CachesClear, created_at = null });
            Assert.Equal(now.AddMinutes(13), PersonLimits.Check(clear, rows, now).After);

            var unreadable = Enumerable.Range(0, 20).Select(_ => new AskedRow { kind = RequestKinds.CachesClear }).ToList();
            var never = PersonLimits.Check(clear, unreadable, now);
            Assert.Equal((true, (DateTime?)null), (never.Reached, never.After));
            Assert.EndsWith("You can ask again later.", RequestWords.PersonLimit(never));
            Assert.Equal(3600, RequestWords.RetryAfter(new SendOutcome.Refused(SendRefusal.PersonLimit, "", never.After), now));

            // Rows older than the hour, and the sign-in alerts, count for nothing
            var old = Enumerable.Range(0, 20).Select(i => new AskedRow { kind = RequestKinds.CachesClear, created_at = now.AddHours(-1).AddSeconds(-1 - i) });
            var alerts = Enumerable.Range(0, 70).Select(i => new AskedRow { kind = RequestKinds.OwnerSigninAlert, created_at = now.AddMinutes(-1) });
            Assert.False(PersonLimits.Check(clear, [.. old, .. alerts], now).Reached);
        }
    }
}
