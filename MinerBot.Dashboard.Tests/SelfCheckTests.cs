using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary;
using DataAccessLibrary.Dashboard;
using DataAccessLibrary.Dashboard.Models;
using DataAccessLibrary.Requests.Models;
using DataAccessLibrary.Tests;
using DataAccessLibrary.Tests.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Settings;
using MinerBot.Dashboard.Status;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Tests
{
    // The Sessions page's checks of the dashboard itself, each with its rule and words: the login's rights and limits, the
    // keys folder, which keeps no probe file, MinerBot's database structure, Cloudflare Access with keys that went stale,
    // the owner's direct messages and the sign-in messages, and a check that fails saying why without its message
    [Collection("Database")]
    public class SelfCheckTests(MariaDbFixture fixture, AccessTestKeys keys) : IClassFixture<AccessTestKeys>
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        static readonly DateTime Now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

        const string Site = MariaDbFixture.SiteDatabase;
        const string Bot = MariaDbFixture.BotDatabase;

        static OwnRights Rights(IReadOnlyList<string> lines, long? connections = DashboardRights.Connections, decimal? seconds = 20.000000m) =>
            new([.. lines.Order(StringComparer.Ordinal)], DashboardRights.For(Site, Bot), connections, seconds);

        // The check's row on the Sessions page, as its result and its detail
        static string Check(string html, string name)
        {
            var row = Regex.Match(html, $"<th scope=\"row\">{Regex.Escape(name)}</th>\\s*<td><span class=\"marker ([a-z-]+)\">([A-Za-z]+)</span></td>\\s*<td>(.*?)</td>",
                RegexOptions.Singleline);
            Assert.True(row.Success, $"The page has no check called {name}");
            return $"{row.Groups[2].Value}: {WebUtility.HtmlDecode(row.Groups[3].Value)}";
        }

        // Exactly the list with deploy's limits passes; a right missing is a problem and one more a note, each named in
        // deploy's words; limits other than 15 and 20 are a problem with both
        [Fact]
        public void RightsNameWhatsMissingAndWhatsExtra()
        {
            var expected = DashboardRights.For(Site, Bot);

            var exact = SelfChecks.OfRights(Rights(expected));
            Assert.Equal((CheckResult.Ok, "The login has exactly the rights this version uses, with at most 15 connections and 20 seconds a statement."), (exact.Result, exact.Detail));

            var missing = SelfChecks.OfRights(Rights([.. expected.Where(line => !line.EndsWith(" schema_version", StringComparison.Ordinal))]));
            Assert.Equal(CheckResult.Problem, missing.Result);
            Assert.Equal($"The login lacks rights this version uses: table SELECT {Bot} schema_version. Pages that need them fail. ./setup-dashboard.sh --no-questions "
                + "applies them again.", missing.Detail);

            var extra = SelfChecks.OfRights(Rights([.. expected, $"table DELETE {Bot} armor_reviews"]));
            Assert.Equal(CheckResult.Note, extra.Result);
            Assert.Equal($"The login has rights this version doesn't use: table DELETE {Bot} armor_reviews. deploy.sh keeps a previous version's rights for a rollback for a "
                + "deploy or two after one is dropped; ./setup-dashboard.sh --no-questions takes away one added by hand.", extra.Detail);

            var both = SelfChecks.OfRights(Rights([.. expected.Skip(1), $"table DELETE {Bot} armor_reviews"]));
            Assert.Equal(CheckResult.Problem, both.Result);
            Assert.StartsWith("The login lacks rights this version uses: ", both.Detail);
            Assert.Contains(" The login has rights this version doesn't use: ", both.Detail);

            var looser = SelfChecks.OfRights(Rights(expected, 100, 0m));
            Assert.Equal(CheckResult.Problem, looser.Result);
            Assert.Equal("The login has at most 100 connections and no statement time limit, where this version expects at most 15 connections and 20 seconds a "
                + "statement. ./setup-dashboard.sh --no-questions sets them again.", looser.Detail);

            Assert.Contains("no connection limit and 3.5 seconds a statement", SelfChecks.OfRights(Rights(expected, 0, 3.5m)).Detail);
            Assert.Contains("a connection limit MariaDB doesn't give as a number", SelfChecks.OfRights(Rights(expected, null, 20m)).Detail);
            Assert.Equal(CheckResult.Problem, SelfChecks.OfRights(Rights(expected, 15, null)).Result);
        }

        // Every migration of this version recorded with its checksum is up to date; one not recorded, one recorded with
        // another checksum, and one this version doesn't know are each named. Fails if checksums aren't compared.
        [Fact]
        public void StructureNamesBehindChangedAndAhead()
        {
            var expected = DatabaseMigrations.Expected();
            Assert.Equal(5, expected.Count);

            Assert.Equal((CheckResult.Ok, "Up to date: migrations 1 to 5."), Result(SelfChecks.OfStructure(expected, expected)));

            var behind = SelfChecks.OfStructure(expected, [.. expected.Take(4)]);
            Assert.Equal((CheckResult.Problem, $"Migration {expected[4].Name} hasn't run. deploy.sh runs the migrations before it swaps the dashboard, so ./status.sh says "
                + "what's deployed."), Result(behind));

            var changed = SelfChecks.OfStructure(expected, [.. expected.Take(4), expected[4] with { Checksum = new string('0', 64) }]);
            Assert.Equal((CheckResult.Problem, $"Migration {expected[4].Name} in the database isn't this version's file, so the database may not match the code."),
                Result(changed));

            var ahead = SelfChecks.OfStructure(expected, [.. expected, new MigrationInfo(6, "0006_later", new string('a', 64))]);
            Assert.Equal((CheckResult.Note, "MinerBot's database has migration 6, which this version doesn't: a newer version ran it, as after ./rollback.sh dashboard. "
                + "Migrations only add, so this version still works."), Result(ahead));

            Assert.Equal((CheckResult.Problem, "The login can't read schema_version. ./setup-dashboard.sh --no-questions applies its rights again."),
                Result(SelfChecks.StructureUnreadable()));
        }

        static (CheckResult, string) Result(SelfCheck check) => (check.Result, check.Detail);

        // The keys check writes and deletes the validator's probe file, leaving none, and counts the keys there; a folder that
        // can't take a key is a problem in the validator's own words. Fails if the probe file stays.
        [Fact]
        public async Task TheKeysFolderCheckLeavesNoProbe()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("198.51.100.210");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);

            var html = await owner.Client.GetStringAsync("/admin/sessions", Token);
            var count = Directory.GetFiles(factory.KeysFolder, "key-*.xml").Length;

            Assert.Empty(Directory.GetFiles(factory.KeysFolder, DashboardOptionsValidator.ProbeName));
            Assert.True(count > 0, "The keys folder holds no key after a sign-in");
            Assert.Equal($"OK: Writable: {factory.KeysFolder}, with {TimeWords.Count(count, "sign-in key")}, and so is the temporary folder each new key passes through.",
                Check(html, SelfChecks.Keys));

            var file = Path.Combine(factory.KeysFolder, "not-a-folder");
            await File.WriteAllTextAsync(file, "", Token);
            var problem = DashboardOptionsValidator.KeysFolderProblem(file);

            Assert.NotNull(problem);
            Assert.Equal((CheckResult.Problem, problem), Result(SelfChecks.OfKeys(file, problem, null, 0)));
            Assert.Equal((CheckResult.Problem, "Run it. Fix it."), Result(SelfChecks.OfKeys(file, "Run it.", "Fix it.", 0)));
        }

        // A check that throws says so in its own row, with the exception's type and MariaDB's error number, never its message,
        // which can name the login and the host; the page and the other checks go on. Fails if a check's failure reaches the
        // page as an error, or shows its message.
        [Fact]
        public async Task AFailingCheckSaysWhyWithoutTheMessage()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IDashboardChecks>(_ => new FailingChecks()))
            });
            var owner = factory.Visitor("198.51.100.211");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);

            var response = await owner.Client.GetAsync("/admin/sessions", Token);
            var html = await response.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Problem: Couldn't check: MySqlException, error 1045.", Check(html, SelfChecks.Rights));
            Assert.Equal("Problem: Couldn't check: TimeoutException.", Check(html, SelfChecks.Structure));
            Assert.StartsWith("OK: Writable: ", Check(html, SelfChecks.Keys));
            Assert.DoesNotContain(FailingChecks.Secret, html, StringComparison.Ordinal);
            Assert.DoesNotContain("10.20.30.40", html, StringComparison.Ordinal);
            Assert.False(factory.Logs.Text.Contains(FailingChecks.Secret, StringComparison.Ordinal), "A log line holds the exception's message");
            PageTests.AssertAccessible("the Sessions page with checks that failed", html);
        }

        // A check's reads that fail as a database's would, with messages that name a login and a host
        sealed class FailingChecks : IDashboardChecks
        {
            public const string Secret = "Access denied for user 'minerbot-dashboard'@'10.20.30.40' (using password: YES)";

            public Task<OwnRights> ListOwnRights() => throw MySqlErrors.Error(1045, Secret);

            public Task<IReadOnlyList<MigrationInfo>> ListMigrations() => throw new TimeoutException(Secret);
        }

        // Off is a note; on, the page came through Access, with the keys and when they were fetched; keys older than an hour
        // mean every fetch since failed, a warning that says when /admin will refuse everyone. Fails if stale keys pass.
        [Fact]
        public async Task AccessOnOffAndStaleKeys()
        {
            var limits = new CloudflareAccessLimits();
            var now = new DateTimeOffset(Now);
            const string Team = "test-team.cloudflareaccess.com";

            Assert.Equal((CheckResult.Note, "Off: /admin opens with a Discord sign-in alone. The plan recommends Access."),
                Result(SelfChecks.OfAccess(false, "", null, limits, now)));
            Assert.Equal((CheckResult.Ok, $"On, for {Team}: this page came through Cloudflare Access, checked with 2 keys fetched at 2026-11-21 11:50:00 UTC."),
                Result(SelfChecks.OfAccess(true, Team, (2, now.AddMinutes(-10)), limits, now)));
            Assert.Equal((CheckResult.Warning, "On, but Cloudflare's keys were fetched at 2026-11-21 10:59:00 UTC and every fetch since has failed. Once they're 24 hours "
                + "old, /admin refuses everyone until a fetch works."), Result(SelfChecks.OfAccess(true, Team, (2, now.AddMinutes(-61)), limits, now)));

            // On the page, through the fake Cloudflare, with keys that go stale after 5 minutes while every fetch fails
            var access = new FakeCloudflareAccess(keys);
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Settings = new Dictionary<string, string?> { ["CloudflareAccess:TeamDomain"] = FakeCloudflareAccess.TeamDomain, ["CloudflareAccess:Audience"] = access.Audience },
                Services = services => access.Install(services, new CloudflareAccessLimits { FreshFor = TimeSpan.FromMinutes(5) })
            });
            access.Clock = factory.Clock;
            var owner = factory.Visitor("198.51.100.212");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);

            async Task<string> SessionsAsync()
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/sessions");
                request.Headers.Add(CloudflareAccessCheck.HeaderName, access.Mint());
                var response = await owner.Client.SendAsync(request, Token);

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return await response.Content.ReadAsStringAsync(Token);
            }

            var fetched = factory.Clock.GetUtcNow().UtcDateTime;
            var fresh = Check(await SessionsAsync(), SelfChecks.Access);

            Assert.Equal($"OK: On, for {FakeCloudflareAccess.TeamDomain}: this page came through Cloudflare Access, checked with 1 key fetched at {TimeWords.Utc(fetched)}.",
                fresh);

            access.FailWith(AccessFailure.ServerError);
            factory.Clock.Advance(TimeSpan.FromMinutes(6));
            var stale = await SessionsAsync();

            Assert.Equal($"Warning: On, but Cloudflare's keys were fetched at {TimeWords.Utc(fetched)} and every fetch since has failed. Once they're 24 hours old, /admin "
                + "refuses everyone until a fetch works.", Check(stale, SelfChecks.Access));
            Assert.DoesNotContain(access.Audience, stale, StringComparison.Ordinal);
            Assert.All(access.Minted, token => Assert.DoesNotContain(token, stale, StringComparison.Ordinal));
        }

        // Requests to the bot: on, with nothing from the bot, passes; the dashboard without its key, the bot unable to check
        // them since this run started, and a request it refused as not the dashboard's in the last day are each a problem, in
        // the banners' own words. An earlier run's word, or a day-old refusal, isn't. Never a key.
        [Fact]
        public void RequestsCheckSaysWhatTheBannersSay()
        {
            var status = new DataAccessLibrary.Status.Models.BotStatusRow { started_at = Now.AddHours(-1) };
            DataAccessLibrary.Status.Models.BotEventRow Event(string kind, DateTime at, string? detail = null) => new() { kind = kind, at = at, detail = detail };

            Assert.Equal((CheckResult.Ok, "On: the dashboard signs its requests."), Result(SelfChecks.OfRequests(true, status, null, null, Now)));
            Assert.Equal((CheckResult.Ok, "On: the dashboard signs its requests."), Result(SelfChecks.OfRequests(true, null, null, null, Now)));
            Assert.Equal((CheckResult.Problem, RequestBanners.DashboardOff), Result(SelfChecks.OfRequests(false, status, null, null, Now)));

            var off = Event(EventWords.RequestsOff, Now.AddMinutes(-59), "MINERBOT_REQUEST_KEY isn't set");
            var botOff = SelfChecks.OfRequests(true, status, off, null, Now);
            Assert.Equal(CheckResult.Problem, botOff.Result);
            Assert.Equal("On: the dashboard signs its requests. " + RequestBanners.BotOff(status, off), botOff.Detail);
            Assert.Contains("it found that MINERBOT_REQUEST_KEY isn't set.", botOff.Detail);
            Assert.Equal(CheckResult.Ok, SelfChecks.OfRequests(true, status, Event(EventWords.RequestsOff, Now.AddHours(-2)), null, Now).Result);

            var rejected = SelfChecks.OfRequests(true, status, null, Event(EventWords.RequestRejected, Now.AddHours(-23)), Now);
            Assert.Equal(CheckResult.Problem, rejected.Result);
            Assert.Contains("The bot refused a request as not made by this dashboard at 13:00:00 UTC, 23 hours ago", rejected.Detail);
            Assert.Equal(CheckResult.Ok, SelfChecks.OfRequests(true, status, null, Event(EventWords.RequestRejected, Now.AddHours(-25)), Now).Result);

            // A later refusal of a request the dashboard signed keeps an earlier one of a row it didn't, as banner 4 does
            var both = SelfChecks.OfRequests(true, status, null, Event(EventWords.RequestRejected, Now.AddHours(-1), "request 99 refused: its kind isn't on the bot's list"), Now,
                Event(EventWords.RequestRejected, Now.AddHours(-3), "request 98 refused: its signature didn't match"));
            Assert.Equal(CheckResult.Problem, both.Result);
            Assert.Contains("The bot refused a request as not made by this dashboard at 09:00:00 UTC, 3 hours ago", both.Detail);
            Assert.Contains("It also refused a request this dashboard signed, at 11:00:00 UTC, 1 hour ago", both.Detail);
        }

        // The last direct message to the owner: none yet is a note, sent passes, failed is a problem with what Discord wants.
        // The sign-in messages: on or off, the last hour's count against the bot's limit, the latest's state, a refusal in the
        // last day a problem, the latest or one before it, and an expiry a warning.
        [Fact]
        public void DirectMessageAndAlertChecks()
        {
            Assert.Equal((CheckResult.Note, "The bot hasn't recorded a direct message to the owner yet."), Result(SelfChecks.OfDirectMessages(null)));

            var dm = new RequestPageRow { id = 41, kind = RequestKinds.BotRestart, state = RequestStates.Done, ended_at = Now.AddMinutes(-3), dm_state = RequestDmStates.Sent };
            Assert.Equal((CheckResult.Ok, "The bot's last direct message to the owner went at 2026-11-21 11:57:00 UTC (request 41)."), Result(SelfChecks.OfDirectMessages(dm)));

            dm.dm_state = RequestDmStates.Failed;
            Assert.Equal((CheckResult.Problem, "The bot's last direct message to the owner failed at 2026-11-21 11:57:00 UTC (request 41). Discord refuses them while the owner "
                + "doesn't allow direct messages from members of a server they share with MinerBot."), Result(SelfChecks.OfDirectMessages(dm)));

            Assert.Equal((CheckResult.Ok, "On: every sign-in asks the bot to send the owner a direct message. 0 sign-ins asked for a message in the last hour, of the bot's 20. "
                + "No sign-in has asked for one yet."), Result(SelfChecks.OfSigninMessages(true, 0, null, null, Now)));
            Assert.Equal(CheckResult.Problem, SelfChecks.OfSigninMessages(false, 0, null, null, Now).Result);
            Assert.StartsWith("Off: the dashboard has no request-signing key, so sign-ins send the owner no message.", SelfChecks.OfSigninMessages(false, 0, null, null, Now).Detail);

            RequestPageRow Alert(string? state, DateTime at, string? result = null, ulong id = 7) => new()
            {
                id = id, kind = RequestKinds.OwnerSigninAlert, actor_name = "Test Owner", actor_role = RequestRoles.Owner, created_at = at, expires_at = at + RequestKinds.SlowExpiry,
                state = state, ended_at = state == null ? null : at.AddSeconds(5), result = result
            };

            var done = SelfChecks.OfSigninMessages(true, 3, Alert(RequestStates.Done, Now.AddMinutes(-5), "Told the owner."), null, Now);
            Assert.Equal((CheckResult.Ok, "On: every sign-in asks the bot to send the owner a direct message. 3 sign-ins asked for a message in the last hour, of the bot's "
                + "20. The latest, request 7 at 2026-11-21 11:55:00 UTC: Done."), Result(done));

            var latestRefused = Alert(RequestStates.Refused, Now.AddHours(-2), "The bot takes this at most 20 times an hour.");
            var refused = SelfChecks.OfSigninMessages(true, 21, latestRefused, latestRefused, Now);
            Assert.Equal(CheckResult.Problem, refused.Result);
            Assert.EndsWith("The latest, request 7 at 2026-11-21 10:00:00 UTC: Refused. The bot refused it: The bot takes this at most 20 times an hour.", refused.Detail);

            var old = Alert(RequestStates.Refused, Now.AddDays(-2), "Over the limit.");
            Assert.Equal(CheckResult.Ok, SelfChecks.OfSigninMessages(true, 0, old, old, Now).Result);

            // A refusal stays a problem for its day once later sign-ins' messages went, the owner's own among them
            var earlier = SelfChecks.OfSigninMessages(true, 1, Alert(RequestStates.Done, Now.AddMinutes(-5), "Told the owner.", id: 9), latestRefused, Now);
            Assert.Equal((CheckResult.Problem, "On: every sign-in asks the bot to send the owner a direct message. 1 sign-in asked for a message in the last hour, of the bot's "
                + "20. The latest, request 9 at 2026-11-21 11:55:00 UTC: Done. The bot refused an earlier one, request 7 at 2026-11-21 10:00:00 UTC: The bot takes this at "
                + "most 20 times an hour."), Result(earlier));
            Assert.Equal(CheckResult.Ok, SelfChecks.OfSigninMessages(true, 1, Alert(RequestStates.Done, Now.AddMinutes(-5), "Told the owner.", id: 9), old, Now).Result);

            var expired = SelfChecks.OfSigninMessages(true, 1, Alert(null, Now.AddMinutes(-11)), null, Now);
            Assert.Equal(CheckResult.Warning, expired.Result);
            Assert.EndsWith("Expired. It expired without the bot taking it, so its message never went.", expired.Detail);
        }
    }
}
