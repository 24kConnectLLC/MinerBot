using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Tests.Database;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Tests
{
    // The one way a page sends a request, called as a page's handler would: in its order of checks, each refusal writing
    // nothing, the dashboard's own refusals of a person recorded, a form sent twice answered with its first request, and the
    // bot's limit met before writing what the bot would refuse
    [Collection("Database")]
    public class RequestSenderTests(MariaDbFixture fixture)
    {
        public static TheoryData<string> OwnerKinds => [.. RequestKinds.All.Where(kind => kind.OwnerOnly).Select(kind => kind.Word)];

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

        static SendOutcome.Refused Refused(SendOutcome outcome, SendRefusal why)
        {
            var refused = Assert.IsType<SendOutcome.Refused>(outcome);
            Assert.Equal(why, refused.Why);
            return refused;
        }

        // An Admin's try at a control only the Owner has is refused before anything else is looked at, and recorded: one
        // audit row with the kind, the person, why, and the address, and no request. Fails if the sender leaves the role to
        // a page's own checks.
        [Theory]
        [MemberData(nameof(OwnerKinds))]
        public async Task AnAdminCantSendAnOwnerKind(string kind)
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "203.0.113.170");

            await using (var scope = await RequestSteps.ScopeAsync(factory, admin, "203.0.113.171"))
            {
                var refused = Refused(await scope.SendAsync(RequestSteps.Order(kind)), SendRefusal.Forbidden);
                Assert.Equal("You can't open that page.", refused.Words);
                Assert.Equal(StatusCodes.Status403Forbidden, RequestWords.Status(refused));

                var audit = Assert.Single(await db.AuditAsync(), entry => entry.action == kind);
                Assert.Equal((AuditOutcome.Refused, AuditRole.Admin, (ulong?)TestUsers.Admin.Id, "Test Admin"), (audit.outcome, audit.actor_role, audit.actor_id, audit.actor_name!));
                Assert.Equal((AuditWords.RequestTarget, (string?)null), (audit.target_type!, audit.target_id));
                Assert.Equal(("Only the Owner can ask for this", "203.0.113.171", scope.Context.TraceIdentifier), (audit.detail!, audit.ip!, audit.request_id!));
            }

            Assert.Empty(await db.SentAsync());
        }

        // The order of the checks: the role before a fresh sign-in, a fresh sign-in before the typed word, the typed word
        // before the write, a form's key already used before every limit, so a second sending of a restart the bot has taken
        // is the first request, never the bot's limit, and the person's limit before the bot's, so a person past their share
        // is recorded even when the bot would refuse too. Fails if any of these change places.
        [Fact]
        public async Task TheChecksRunInOrder()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "203.0.113.172");
            var admin = factory.Visitor("203.0.113.173");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            // Both sign-ins stale, and the restart's word left out
            factory.Clock.Advance(TimeSpan.FromMinutes(16));
            var unconfirmed = RequestSteps.Order(RequestKinds.BotRestart) with { Typed = null };

            Refused(await SendAsync(factory, admin, unconfirmed), SendRefusal.Forbidden);
            Refused(await SendAsync(factory, owner, unconfirmed), SendRefusal.NotFresh);

            Assert.Equal(System.Net.HttpStatusCode.Redirect, (await SignInSteps.SignInAgainAsync(factory, owner, TestUsers.Owner, "/admin/bot/controls#restart")).StatusCode);
            var notConfirmed = Refused(await SendAsync(factory, owner, unconfirmed), SendRefusal.NotConfirmed);
            Assert.Equal("Type restart in the box to confirm. Nothing was sent.", notConfirmed.Words);
            Assert.Empty(await db.SentAsync());

            var restart = RequestSteps.Order(RequestKinds.BotRestart);
            var sent = Assert.IsType<SendOutcome.Sent>(await SendAsync(factory, owner, restart));
            Assert.False(sent.Repeated);

            // The bot takes it, which uses up its limit: the same form again is still the first request
            await RequestSteps.ClaimAsync(db, sent.Id, factory.Clock.GetUtcNow().UtcDateTime.AddSeconds(1));
            Assert.Equal(new SendOutcome.Sent(sent.Id, Repeated: true), await SendAsync(factory, owner, restart));

            // A new form for another restart meets the bot's limit
            Refused(await SendAsync(factory, owner, RequestSteps.Order(RequestKinds.BotRestart)), SendRefusal.BotLimit);
            Assert.Single(await db.SentAsync());

            // An Admin who has used up their share of the notification job, 20 seconds after the bot last took one, meets their
            // own limit, which the audit log records, and not the bot's, which it doesn't
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            await RequestSteps.AddRefusedAsync(db, RequestKinds.JobNotifications, TestUsers.Admin, Enumerable.Range(0, 10).Select(i => now.AddMinutes(-50 + i)));
            await RequestSteps.TakenAsync(db, factory, RequestKinds.JobNotifications, now.AddSeconds(-20));

            Refused(await SendAsync(factory, admin, RequestSteps.Order(RequestKinds.JobNotifications)), SendRefusal.PersonLimit);
            Assert.Single(await db.AuditAsync(), entry => entry.detail == RequestWords.PersonLimitDetail);
        }

        // A form's key already used comes before the fresh sign-in too: a restart's form sent again, from a page a browser
        // kept, once the sign-in that sent it is more than 15 minutes old, is still its first request, and no refusal is
        // recorded, while a new form for the same control is refused for the sign-in. Fails if the look for the key comes after
        // the fresh sign-in.
        [Fact]
        public async Task AFormSentAgainOnceItsSignInIsStaleIsTheFirstRequest()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "203.0.113.230");

            var restart = RequestSteps.Order(RequestKinds.BotRestart);
            var sent = Assert.IsType<SendOutcome.Sent>(await SendAsync(factory, owner, restart));

            factory.Clock.Advance(TimeSpan.FromMinutes(16));
            Assert.Equal(new SendOutcome.Sent(sent.Id, Repeated: true), await SendAsync(factory, owner, restart));
            Assert.DoesNotContain(await db.AuditAsync(), entry => entry.outcome == AuditOutcome.Refused);

            Refused(await SendAsync(factory, owner, RequestSteps.Order(RequestKinds.BotRestart)), SendRefusal.NotFresh);
            Assert.Single(await db.SentAsync());
        }

        // A request as the bot will verify it: the kind, the person, their role, the text without the white space around it,
        // the dashboard's time, a signature the factory's key verifies, and one audit row with the address and the trace ID
        [Fact]
        public async Task ASentRequestIsTheOneTheBotVerifies()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "203.0.113.174");

            await using var scope = await RequestSteps.ScopeAsync(factory, owner, "203.0.113.175");
            var sent = Assert.IsType<SendOutcome.Sent>(await scope.SendAsync(RequestSteps.Order(RequestKinds.PostsPause) with { Argument = "  Testing the controls \t" }));

            var row = Assert.Single(await db.SentAsync());
            Assert.Equal(sent.Id, row.id);
            Assert.Equal((RequestKinds.PostsPause, TestUsers.Owner.Id, "Test Owner", RequestRoles.Owner, "Testing the controls"),
                (row.kind, row.actor_id, row.actor_name, row.actor_role, row.argument!));
            Assert.Equal(factory.Clock.GetUtcNow().UtcDateTime, row.created_at);
            Assert.True(RequestSigning.Verify(Convert.FromHexString(factory.RequestSigningKey), row.Fields(), row.signature));

            var audit = Assert.Single(await db.AuditAsync(), entry => entry.action == RequestKinds.PostsPause);
            Assert.Equal((AuditOutcome.Ok, sent.Id.ToString(CultureInfo.InvariantCulture), "203.0.113.175", scope.Context.TraceIdentifier),
                (audit.outcome, audit.target_id!, audit.ip!, audit.request_id!));
        }

        // A form someone made up, with no key, a key in another form, or a server or channel of 0, is refused before any read,
        // and writes nothing, not even an audit row
        [Fact]
        public async Task AMadeUpFormIsRefusedBeforeAnyRead()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "203.0.113.176");
            var before = (await db.AuditAsync()).Count;

            SendOrder[] madeUp =
            [
                RequestSteps.Order(RequestKinds.CachesClear) with { FormKey = null },
                RequestSteps.Order(RequestKinds.CachesClear) with { FormKey = RequestFields.NewKey().ToUpperInvariant() },
                RequestSteps.Order(RequestKinds.CachesClear) with { FormKey = RequestFields.NewKey()[..31] },
                RequestSteps.Order(RequestKinds.CachesClear) with { FormKey = RequestFields.NewKey()[..31] + "g" },
                RequestSteps.Order(RequestKinds.GuildTestMessage) with { GuildId = 0 },
                RequestSteps.Order(RequestKinds.GuildTestMessage) with { ChannelId = 0 }
            ];

            foreach (var order in madeUp)
                Assert.Equal(StatusCodes.Status400BadRequest, RequestWords.Status(Refused(await SendAsync(factory, owner, order), SendRefusal.MadeUp)));

            Assert.Empty(await db.SentAsync());
            Assert.Equal(before, (await db.AuditAsync()).Count);
        }

        // The same form sent again with other details, such as a status text changed in a page a browser kept, isn't sent
        // again under the first one's key: it says the form was used, as request N, and the row keeps the first text. Fails
        // if a repeat's details aren't compared.
        [Fact]
        public async Task ASecondSendingWithOtherDetailsSaysTheFormWasUsed()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "203.0.113.177");
            var form = RequestSteps.Order(RequestKinds.StatusSet) with { Argument = "Back soon" };

            var first = Assert.IsType<SendOutcome.Sent>(await SendAsync(factory, owner, form));
            var again = Assert.IsType<SendOutcome.SentBefore>(await SendAsync(factory, owner, form with { Argument = "Back later" }));

            Assert.Equal(first.Id, again.Id);
            Assert.Equal(StatusCodes.Status409Conflict, RequestWords.Status(again));
            Assert.Equal($"This form was sent already, as request {first.Id}, with other details, so nothing more was sent. The form below is new: send it again to ask with these.",
                RequestWords.SentBefore(again.Id));
            Assert.Equal(["Back soon"], (await db.SentAsync()).Select(row => row.argument));

            // The same text with white space around it is the same form
            Assert.Equal(new SendOutcome.Sent(first.Id, Repeated: true), await SendAsync(factory, owner, form with { Argument = " Back soon " }));
        }

        // A form's key that's another person's request, or another kind's, is refused with words of its own, writing
        // nothing, never an error page
        [Fact]
        public async Task AFormKeyOfAnotherPersonOrKindIsRefused()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "203.0.113.178");
            var admin = factory.Visitor("203.0.113.179");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);
            var clear = RequestSteps.Order(RequestKinds.CachesClear);

            Assert.IsType<SendOutcome.Sent>(await SendAsync(factory, owner, clear));

            var otherPerson = Refused(await SendAsync(factory, admin, clear), SendRefusal.KeyTaken);
            var otherKind = Refused(await SendAsync(factory, owner, RequestSteps.Order(RequestKinds.JobNotifications) with { FormKey = clear.FormKey }), SendRefusal.KeyTaken);

            Assert.Equal("This form's key belongs to another request, so nothing was sent. Reload the page and send it again.", otherPerson.Words);
            Assert.Equal(otherPerson.Words, otherKind.Words);
            Assert.Equal(StatusCodes.Status409Conflict, RequestWords.Status(otherKind));
            Assert.Single(await db.SentAsync());
        }

        // A key that another request takes between the sender's look and its write, as a second tab can: the write's own
        // duplicate key answers it, with the first request when it's the same person's same form, and the key's refusal when
        // it isn't, never an exception. Fails if FormKeyTakenException isn't caught.
        [Fact]
        public async Task AKeyTakenDuringTheSendIsAnsweredToo()
        {
            var db = await fixture.ReadyAsync();
            var racing = new ConcurrentDictionary<string, bool>();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider =>
                    new RacingQueue(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider), racing)))
            });
            var owner = await SignedInAsync(factory, TestUsers.Owner, "203.0.113.180");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var theirs = RequestFields.NewKey();
            await RequestSteps.AddAsync(db, factory, RequestKinds.CachesClear, now, TestUsers.Admin, key: theirs);
            Refused(await SendAsync(factory, owner, RequestSteps.Order(RequestKinds.CachesClear) with { FormKey = theirs }), SendRefusal.KeyTaken);

            var mine = RequestFields.NewKey();
            var first = await RequestSteps.AddAsync(db, factory, RequestKinds.CachesClear, now, TestUsers.Owner, key: mine);
            Assert.Equal(new SendOutcome.Sent(first, Repeated: true), await SendAsync(factory, owner, RequestSteps.Order(RequestKinds.CachesClear) with { FormKey = mine }));

            Assert.True(racing.ContainsKey(theirs) && racing.ContainsKey(mine), "The look before the write didn't miss the keys");
            Assert.Equal(2, (await db.SentAsync()).Count);
        }

        // A form's first sending that lands after the second sending's look at the key, as one whose save the database didn't
        // confirm can, since the person's turn keeps out any other: the limits then count it, as one that waits, for a restart
        // the bot takes once in 10 minutes, and as the last of an Admin's share of cache clears. The second still shows the
        // first request, as any double click does, and records no refusal. RacingQueue makes the look miss. Fails if a limit
        // refuses without looking for the key again.
        [Fact]
        public async Task ADoubleClickThatMeetsALimitShowsTheFirstRequest()
        {
            var db = await fixture.ReadyAsync();
            var racing = new ConcurrentDictionary<string, bool>();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider =>
                    new RacingQueue(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider), racing)))
            });
            var owner = await SignedInAsync(factory, TestUsers.Owner, "203.0.113.165");
            var admin = await SignedInAsync(factory, TestUsers.Admin, "203.0.113.166");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            // Each form's first sending, written just after its second sending's look missed it
            var restart = RequestSteps.Order(RequestKinds.BotRestart);
            var firstRestart = await RequestSteps.AddAsync(db, factory, RequestKinds.BotRestart, now, key: restart.FormKey);
            Assert.Equal(new SendOutcome.Sent(firstRestart, Repeated: true), await SendAsync(factory, owner, restart));

            await RequestSteps.AddRefusedAsync(db, RequestKinds.CachesClear, TestUsers.Admin, Enumerable.Range(0, 19).Select(i => now.AddMinutes(-50).AddSeconds(i)));
            var clear = RequestSteps.Order(RequestKinds.CachesClear);
            var lastClear = await RequestSteps.AddAsync(db, factory, RequestKinds.CachesClear, now, TestUsers.Admin, key: clear.FormKey);
            Assert.Equal(new SendOutcome.Sent(lastClear, Repeated: true), await SendAsync(factory, admin, clear));

            Assert.True(racing.ContainsKey(restart.FormKey!) && racing.ContainsKey(clear.FormKey!), "The look before the limits didn't miss the keys");
            Assert.DoesNotContain(await db.AuditAsync(), entry => entry.outcome == AuditOutcome.Refused);
            Assert.Equal(21, (await db.SentAsync()).Count);
        }

        // The bot's own limit, met before writing: a restart the bot took 2 minutes ago makes another one a refusal in the
        // bot's own words, which says when to try again, and writes nothing. Refused and expired restarts, which the bot never
        // took, don't count. Fails if the check is gone or counts the wrong rows.
        [Fact]
        public async Task TheBotsLimitIsCheckedBeforeWriting()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "203.0.113.181");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            await RequestSteps.TakenAsync(db, factory, RequestKinds.BotRestart, now.AddMinutes(-2).AddSeconds(-1));

            var refused = Refused(await SendAsync(factory, owner, RequestSteps.Order(RequestKinds.BotRestart)), SendRefusal.BotLimit);
            Assert.Equal(now.AddMinutes(8), refused.After);
            Assert.Equal($"The bot takes this at most once every 10 minutes, and last took one at {RequestWords.Clock(now.AddMinutes(-2))}, so nothing was sent. "
                + $"Try again after {RequestWords.Clock(now.AddMinutes(8))}.", refused.Words);
            Assert.Equal(StatusCodes.Status409Conflict, RequestWords.Status(refused));
            Assert.Single(await db.SentAsync());

            await db.ClearAsync(MariaDbFixture.RequestTable);
            await RequestSteps.DecideAsync(db, await RequestSteps.AddAsync(db, factory, RequestKinds.BotRestart, now.AddMinutes(-3)), RequestStates.Refused,
                "The bot takes this at most once every 10 minutes.", now.AddMinutes(-3).AddSeconds(1));
            await RequestSteps.DecideAsync(db, await RequestSteps.AddAsync(db, factory, RequestKinds.BotRestart, now.AddMinutes(-5)), RequestStates.Expired,
                "Expired", now.AddMinutes(-3));

            Assert.IsType<SendOutcome.Sent>(await SendAsync(factory, owner, RequestSteps.Order(RequestKinds.BotRestart)));
            Assert.Equal(3, (await db.SentAsync()).Count);
        }

        // A restart that waits for the bot, not yet taken, fills the limit the bot will count it toward: another is refused,
        // naming it, until it expires, when one is sent. Fails if waiting requests are left out of the check.
        [Fact]
        public async Task AWaitingRequestCountsTowardTheLimit()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "203.0.113.182");
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var waiting = await RequestSteps.AddAsync(db, factory, RequestKinds.BotRestart, now.AddSeconds(-30));

            var refused = Refused(await SendAsync(factory, owner, RequestSteps.Order(RequestKinds.BotRestart)), SendRefusal.Waiting);
            Assert.Equal(waiting, refused.Waiting);
            Assert.Equal(now.AddSeconds(90), refused.After);
            Assert.Equal($"A request like this is already waiting for the bot: request {waiting}, asked for at {RequestWords.Clock(now.AddSeconds(-30))}. The bot takes it within "
                + $"seconds while it's on Discord, or it expires at {RequestWords.Clock(now.AddSeconds(90))}. Nothing more was sent.", refused.Words);
            Assert.Single(await db.SentAsync());

            factory.Clock.Advance(TimeSpan.FromSeconds(91));
            Assert.IsType<SendOutcome.Sent>(await SendAsync(factory, owner, RequestSteps.Order(RequestKinds.BotRestart)));
            Assert.Equal(2, (await db.SentAsync()).Count);
        }

        // Test messages are limited for each server apart: three waiting for one server fill its 3, and a test message to
        // another server is still sent. Fails if the server is left out of the check.
        [Fact]
        public async Task PerServerLimitsCountEachServer()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "203.0.113.183");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            for (var i = 3; i > 0; i--)
                await RequestSteps.AddAsync(db, factory, RequestKinds.GuildTestMessage, now.AddSeconds(-10 * i));

            Refused(await SendAsync(factory, admin, RequestSteps.Order(RequestKinds.GuildTestMessage)), SendRefusal.Waiting);
            Assert.IsType<SendOutcome.Sent>(await SendAsync(factory, admin, RequestSteps.Order(RequestKinds.GuildTestMessage, RequestSteps.OtherServerId)));

            Assert.Equal([RequestSteps.ServerId, RequestSteps.ServerId, RequestSteps.ServerId, RequestSteps.OtherServerId],
                (await db.SentAsync()).Select(row => row.guild_id!.Value));
        }

        // The bot's limit comes from two reads, the requests that wait and the ones the bot took, and the bot's 3-second tick
        // can take a request between them: it's then counted as taken, never missed by both. An Admin's notification job
        // waits, and the bot takes it between the Owner's two reads, so the Owner's is refused for the bot's limit, as the
        // bot would refuse it, and the controls page's line says the same of the next one. Fails if the claims are read first.
        [Fact]
        public async Task ARequestTakenBetweenTheLimitReadsIsCounted()
        {
            var db = await fixture.ReadyAsync();
            var between = new StrongBox<Func<Task>?>();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IBotRequestReads>(provider =>
                    new BetweenLimitReads(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider), between)))
            });
            var owner = await SignedInAsync(factory, TestUsers.Owner, "203.0.113.167");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var waiting = await RequestSteps.AddAsync(db, factory, RequestKinds.JobNotifications, now.AddSeconds(-1), TestUsers.Admin);
            between.Value = () => RequestSteps.ClaimAsync(db, waiting, now);

            var refused = Refused(await SendAsync(factory, owner, RequestSteps.Order(RequestKinds.JobNotifications)), SendRefusal.BotLimit);
            Assert.Null(between.Value);
            Assert.Equal(now.AddMinutes(1), refused.After);
            Assert.Single(await db.SentAsync());

            // A minute later another waits, and the bot takes it between the page's two reads
            factory.Clock.Advance(TimeSpan.FromSeconds(61));
            var later = factory.Clock.GetUtcNow().UtcDateTime;
            var next = await RequestSteps.AddAsync(db, factory, RequestKinds.JobNotifications, later.AddSeconds(-1), TestUsers.Admin);
            between.Value = () => RequestSteps.ClaimAsync(db, next, later);

            var html = await owner.Client.GetStringAsync("/admin/bot/controls", TestContext.Current.CancellationToken);
            Assert.Null(between.Value);
            Assert.Contains($"The bot takes this again after {RequestWords.Clock(later.AddMinutes(1))}.", html);
        }

        // A commit the database didn't confirm may have landed: the sender says it can't tell, with the form's key, which the
        // form drawn again keeps, so sending it again finds the first request rather than writing a second. Every other
        // answer draws the form with a new key. Fails if an unsure save gets a new key.
        [Fact]
        public async Task AnUnconfirmedSaveIsUnsureAndKeepsItsKey()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider =>
                    new UnconfirmedQueue(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider))))
            });
            var owner = await SignedInAsync(factory, TestUsers.Owner, "203.0.113.184");
            var clear = RequestSteps.Order(RequestKinds.CachesClear);

            var unsure = Assert.IsType<SendOutcome.Unsure>(await SendAsync(factory, owner, clear));
            Assert.Equal(clear.FormKey, unsure.Key);
            Assert.Equal(clear.FormKey, FormKeys.For(unsure));
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, RequestWords.Status(unsure));

            var row = Assert.Single(await db.SentAsync());
            Assert.Equal(new SendOutcome.Sent(row.id, Repeated: true), await SendAsync(factory, owner, clear with { FormKey = FormKeys.For(unsure) }));
            Assert.Single(await db.SentAsync());
        }

        // Only the form drawn again after an unsure save keeps its key: every other render, refusals included, draws a new
        // one, since a refusal wrote nothing
        [Fact]
        public void OnlyAnUnsureSaveKeepsItsFormKey()
        {
            var key = RequestFields.NewKey();
            SendOutcome?[] others = [null, new SendOutcome.Sent(41, false), new SendOutcome.Sent(41, true), new SendOutcome.SentBefore(41),
                new SendOutcome.Refused(SendRefusal.Rule, "made up")];

            Assert.Equal(key, FormKeys.For(new SendOutcome.Unsure(key)));

            var fresh = others.Select(FormKeys.For).Append(FormKeys.For(null)).ToList();
            Assert.All(fresh, made => Assert.Matches("^[0-9a-f]{32}$", made));
            Assert.DoesNotContain(key, fresh);
            Assert.Equal(fresh.Count, fresh.Distinct().Count());
        }

        // With no request-signing key nothing can be sent: a crafted form is refused with words that say how to turn requests
        // on, and writes nothing
        [Fact]
        public async Task RequestsOffSendsNothing()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions { Settings = new Dictionary<string, string?> { ["Dashboard:RequestSigningKey"] = "" } });
            var owner = await SignedInAsync(factory, TestUsers.Owner, "203.0.113.185");

            var off = Refused(await SendAsync(factory, owner, RequestSteps.Order(RequestKinds.CachesClear)), SendRefusal.Off);

            Assert.Equal("Requests to the bot are off: the dashboard has no request-signing key, so nothing was sent. On the server, ./setup-dashboard.sh --no-questions makes one.",
                off.Words);
            Assert.Equal(StatusCodes.Status409Conflict, RequestWords.Status(off));
            Assert.Empty(await db.RequestsAsync());
        }

        // A text the bot would refuse is refused before anything is written, in the rule's own words, which never quote it
        [Fact]
        public async Task ARuleRefusalNamesTheRule()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "203.0.113.186");

            var link = Refused(await SendAsync(factory, owner, RequestSteps.Order(RequestKinds.StatusSet) with { Argument = "Join x.com/minerbot" }), SendRefusal.Rule);
            Assert.Equal("The bot would refuse this, so nothing was sent: the status text has a link.", link.Words);
            Assert.Equal(StatusCodes.Status400BadRequest, RequestWords.Status(link));

            var hidden = Refused(await SendAsync(factory, owner, RequestSteps.Order(RequestKinds.PostsPause) with { Argument = "Wait\u200Bing" }), SendRefusal.Rule);
            Assert.Equal("The bot would refuse this, so nothing was sent: the reason has a character that shows nothing or changes the text's direction.", hidden.Words);

            var empty = Refused(await SendAsync(factory, owner, RequestSteps.Order(RequestKinds.PostsPause) with { Argument = "  " }), SendRefusal.Rule);
            Assert.Equal("The bot would refuse this, so nothing was sent: the reason is empty.", empty.Words);

            Assert.Empty(await db.SentAsync());
        }

        // The status text's own form, sent empty, isn't Back to the default: it asks for the text and sends nothing. Back to
        // the default's form sends the status kind with no text.
        [Fact]
        public async Task AnEmptyStatusTextIsNotTheDefault()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "203.0.113.187");

            foreach (var text in new string?[] { null, "", "   " })
            {
                var empty = Refused(await SendAsync(factory, owner, RequestSteps.Order(RequestKinds.StatusSet) with { Argument = text }), SendRefusal.NotConfirmed);
                Assert.Equal("Type the status text, or use Back to the default.", empty.Words);
                Assert.Equal(StatusCodes.Status400BadRequest, RequestWords.Status(empty));
            }

            Assert.Empty(await db.SentAsync());
            Assert.IsType<SendOutcome.Sent>(await SendAsync(factory, owner, new SendOrder(RequestKinds.StatusSet, RequestFields.NewKey())));
            Assert.Null(Assert.Single(await db.SentAsync()).argument);
        }

        // The typed word is the control's own, trimmed and in any case, and a form without the box is refused like one with
        // the wrong word; a leave takes the server's name or ID, for the server it's about
        [Fact]
        public async Task AWrongOrMissingConfirmationSendsNothing()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "203.0.113.188");
            var restart = RequestSteps.Order(RequestKinds.BotRestart);

            foreach (var typed in new Confirmation?[] { null, new Confirmation.Word(""), new Confirmation.Word("restar"), new Confirmation.Word("pause"),
                new Confirmation.Server(StatusSteps.ServerId, "Test Server", RequestSteps.ServerId) })
            {
                var refused = Refused(await SendAsync(factory, owner, restart with { FormKey = RequestFields.NewKey(), Typed = typed }), SendRefusal.NotConfirmed);
                Assert.Equal("Type restart in the box to confirm. Nothing was sent.", refused.Words);
            }

            var leave = RequestSteps.Order(RequestKinds.GuildLeave);

            // The last confirms a server of its own, by its name and by its ID, but not the one the leave is about
            foreach (var typed in new Confirmation?[] { null, new Confirmation.Word("leave"), new Confirmation.Server("Another", "Test Server", RequestSteps.ServerId),
                new Confirmation.Server("Other Server", "Other Server", RequestSteps.OtherServerId),
                new Confirmation.Server(RequestSteps.OtherServerId.ToString(CultureInfo.InvariantCulture), "Other Server", RequestSteps.OtherServerId) })
            {
                var refused = Refused(await SendAsync(factory, owner, leave with { FormKey = RequestFields.NewKey(), Typed = typed }), SendRefusal.NotConfirmed);
                Assert.Equal("Type the server's name as it shows above, or its ID, to confirm. Nothing was sent.", refused.Words);
            }

            Assert.Empty(await db.SentAsync());

            Assert.IsType<SendOutcome.Sent>(await SendAsync(factory, owner, restart with { Typed = new Confirmation.Word("Restart ") }));
            await db.ClearAsync(MariaDbFixture.RequestTable);
            Assert.IsType<SendOutcome.Sent>(await SendAsync(factory, owner, restart with { FormKey = RequestFields.NewKey(), Typed = new Confirmation.Word("RESTART") }));
            Assert.IsType<SendOutcome.Sent>(await SendAsync(factory, owner, leave with { Typed = new Confirmation.Server(" test   SERVER ", "Test\u200B Server", RequestSteps.ServerId) }));
            Assert.Equal([RequestKinds.BotRestart, RequestKinds.GuildLeave], (await db.SentAsync()).Select(row => row.kind));
        }
    }
}
