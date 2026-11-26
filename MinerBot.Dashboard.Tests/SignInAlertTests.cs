using System.Data.Common;
using System.Net;
using DataAccessLibrary;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Requests.Models;
using DataAccessLibrary.Tests.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Tests
{
    // The owner's message about every sign-in to the admin side: a request the bot takes, written in the sign-in's own
    // transaction, so a sign-in that can't leave one doesn't happen, and nothing but a sign-in leaves one
    [Collection("Database")]
    public class SignInAlertTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        static Task<List<AuditEntry>> AlertAuditAsync(MariaDbFixture db) =>
            db.RootQueryAsync<AuditEntry>($"SELECT * FROM {MariaDbFixture.AuditTable} WHERE action = @action ORDER BY id", new { action = RequestKinds.OwnerSigninAlert });

        // The Owner's sign-in and an Admin's each leave one request for the bot to tell the owner, as the owner chose: the
        // person's ID, their name as a request carries it and their listed role, and nothing the bot could pass on beyond
        // that, signed with the dashboard's key, dated as the session is, and with its own audit row holding the address.
        // Fails if anyone's sign-in sends none, or the alert goes in outside the sign-in.
        [Fact]
        public async Task EverySignInQueuesOneAlertInItsOwnTransaction()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);

            var ownerKey = await SignInSteps.SignedInAsync(factory, factory.Visitor("203.0.113.150"), TestUsers.Owner);
            var adminKey = await SignInSteps.SignedInAsync(factory, factory.Visitor("203.0.113.151"), TestUsers.Admin with { GlobalName = "Test\u0007Admin" });

            var rows = await db.RequestsAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row => Assert.Equal(RequestKinds.OwnerSigninAlert, row.kind));
            Assert.Equal([TestUsers.Owner.Id, TestUsers.Admin.Id], rows.Select(row => row.actor_id));
            Assert.Equal(["Test Owner", "Test Admin"], rows.Select(row => row.actor_name));
            Assert.Equal([RequestRoles.Owner, RequestRoles.Admin], rows.Select(row => row.actor_role));

            var key = Convert.FromHexString(factory.RequestSigningKey);
            var sessions = new[] { await db.SessionAsync(ownerKey), await db.SessionAsync(adminKey) };

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                Assert.Null(row.guild_id);
                Assert.Null(row.channel_id);
                Assert.Null(row.argument);
                Assert.Null(row.state);
                Assert.Equal(RequestKinds.SlowExpiry, row.expires_at - row.created_at);
                Assert.True(RequestSigning.Verify(key, row.Fields(), row.signature), $"Request {row.id}'s signature doesn't verify");
                Assert.Equal(sessions[i]!.created_at.Ticks, row.created_at.Ticks);
            }

            var audit = await AlertAuditAsync(db);
            Assert.Equal(rows.Select(row => row.id.ToString(System.Globalization.CultureInfo.InvariantCulture)), audit.Select(entry => entry.target_id));
            Assert.Equal(["203.0.113.150", "203.0.113.151"], audit.Select(entry => entry.ip));
            Assert.All(audit, entry => Assert.Equal((AuditOutcome.Ok, AuditWords.RequestTarget, (string?)null), (entry.outcome, entry.target_type, entry.after_json)));
            Assert.Equal([AuditRole.Owner, AuditRole.Admin], audit.Select(entry => entry.actor_role));
        }

        // The alert's insert fails as a dropped connection would: the whole sign-in rolls back, with no session, no audit row
        // and no request, and the visitor gets the page that says the database didn't answer. Fails if the alert is written
        // after the sign-in's transaction, or on its own.
        [Fact]
        public async Task ASignInWhoseAlertCantBeWrittenDoesntHappen()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider =>
                    new FailingQueue(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider))))
            });
            var visitor = factory.Visitor("203.0.113.152");

            var response = await SignInSteps.SignInAsync(factory, visitor, TestUsers.Owner);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("can't reach its database", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token)));
            Assert.Null(visitor.SessionKey);
            Assert.Empty(await db.SessionsAsync());
            Assert.Empty(await db.AuditAsync());
            Assert.Empty(await db.RequestsAsync());
        }

        // The alert's own audit row fails the same way, after its request was written: the same, nothing left
        [Fact]
        public async Task ASignInWhoseAlertsAuditRowFailsDoesntHappen()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IAuditLogData>(provider =>
                    new FailingAudit(ActivatorUtilities.CreateInstance<AuditLogData>(provider), RequestKinds.OwnerSigninAlert)))
            });

            var response = await SignInSteps.SignInAsync(factory, factory.Visitor("203.0.113.153"), TestUsers.Admin);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Empty(await db.SessionsAsync());
            Assert.Empty(await db.AuditAsync());
            Assert.Empty(await db.RequestsAsync());
        }

        // As the dashboard's own login after it lost a right to add a request, as a REVOKE by hand would leave it: sessions
        // and audit rows can still be written, the alert can't, so no sign-in happens, and the visitor's page says only that
        // the database didn't answer. The log names MariaDB's error, 1143 for a column right, and never the driver's text,
        // which names the login and its host. Fails if the log can't tell a right the login lacks from a database that's down.
        [Fact]
        public async Task ASignInWhoseAlertTheLoginMayNotWriteSaysWhyInTheLog()
        {
            var db = await fixture.ReadyAsync();

            await DashboardLogin.AsDashboardAsync(db, async password =>
            {
                await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
                {
                    SiteConnection = DashboardLogin.ConnectionAs(db, password, MariaDbFixture.SiteDatabase),
                    BotConnection = DashboardLogin.ConnectionAs(db, password, MariaDbFixture.BotDatabase)
                });

                await db.RootExecuteAsync($"REVOKE INSERT (`request_key`) ON `{MariaDbFixture.BotDatabase}`.`bot_commands` FROM {DashboardLogin.Grantee}");
                var response = await SignInSteps.SignInAsync(factory, factory.Visitor("198.51.100.212"), TestUsers.Admin);

                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Empty(await db.SessionsAsync());
                Assert.Empty(await db.AuditAsync());
                Assert.Empty(await db.RequestsAsync());
                Assert.Contains("A database call failed with MySqlException, SQL state none, MariaDB error 1143", factory.Logs.Text);
                Assert.DoesNotContain("command denied", factory.Logs.Text, StringComparison.OrdinalIgnoreCase);
            });
        }

        // With no request-signing key, nothing can be sent to the bot, and a sign-in still works without its alert, with a
        // log line saying so: otherwise nobody could sign in before setup makes the key. Fails if requests being off stops
        // the sign-in.
        [Fact]
        public async Task WithRequestsOffASignInStillWorksWithoutAnAlert()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions { Settings = new Dictionary<string, string?> { ["Dashboard:RequestSigningKey"] = "" } });

            var key = await SignInSteps.SignedInAsync(factory, factory.Visitor("203.0.113.154"), TestUsers.Owner);

            Assert.NotNull(await db.SessionAsync(key));
            Assert.Equal(["auth.signin"], (await db.AuditAsync()).Select(entry => entry.action));
            Assert.Empty(await db.RequestsAsync());
            Assert.Contains("Requests to the bot are off, so this sign-in sends the owner no message", factory.Logs.Text);
        }

        // A sign-in that's refused makes no session, so it leaves no alert: only its own audit row
        [Fact]
        public async Task RefusedSignInsQueueNoAlert()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);

            var unlisted = await SignInSteps.SignInAsync(factory, factory.Visitor("203.0.113.155"), TestUsers.Visitor);
            var noTwoFactor = await SignInSteps.SignInAsync(factory, factory.Visitor("203.0.113.156"), TestUsers.Admin with { MfaEnabled = false });

            Assert.Equal("/denied?reason=unlisted", SignInSteps.PathOf(unlisted.Headers.Location));
            Assert.Equal("/denied?reason=mfa", SignInSteps.PathOf(noTwoFactor.Headers.Location));
            Assert.Empty(await db.SessionsAsync());
            Assert.Equal(["auth.refused", "auth.refused"], (await db.AuditAsync()).Select(entry => entry.action));
            Assert.Empty(await db.RequestsAsync());
        }

        // Only a sign-in that makes a session leaves an alert: page views that use the session, a status box's refresh, a
        // request box's look, a sign-in again cancelled at Discord and a sign-out leave none. Fails if anything that only
        // touches a session writes one.
        [Fact]
        public async Task NothingButASignInWritesAnAlert()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.157");
            var key = await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var alert = Assert.Single(await db.RequestsAsync());

            // Past the minute a session's use is written at most once in, so these views write it
            factory.Clock.Advance(TimeSpan.FromMinutes(2));

            foreach (var page in new[] { "/admin", "/admin/bot", "/admin/audit", "/admin/requests/" + alert.id })
                Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync(page, Token)).StatusCode);

            Assert.True((await db.SessionAsync(key))!.last_seen_at > (await db.SessionAsync(key))!.created_at);

            foreach (var look in new[] { "/admin?handler=status", $"/admin/requests/{alert.id}?handler=result" })
            {
                var request = new HttpRequestMessage(HttpMethod.Get, look);
                request.Headers.Add(BackgroundRefresh.Header, BackgroundRefresh.Value);
                request.Headers.Add("X-Requested-With", "XMLHttpRequest");
                Assert.Equal(HttpStatusCode.OK, (await owner.Client.SendAsync(request, Token)).StatusCode);
            }

            // A sign-in again that's cancelled at Discord ends nothing and writes nothing
            var again = await SignInSteps.StartAgainAsync(factory, owner, "/admin/bot/controls#restart");
            var cancelled = await owner.Client.GetAsync($"/signin-discord?error=access_denied&state={Uri.EscapeDataString(again.State)}", Token);
            Assert.Equal("/denied?reason=cancelled", SignInSteps.PathOf(cancelled.Headers.Location));
            Assert.NotNull(await db.SessionAsync(key));
            Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync("/admin", Token)).StatusCode);

            Assert.Equal(HttpStatusCode.Redirect, (await SignInSteps.SignOutAsync(owner)).StatusCode);
            Assert.Null(await db.SessionAsync(key));

            Assert.Equal([alert.id], (await db.RequestsAsync()).Select(row => row.id));
            Assert.Single(await AlertAuditAsync(db));
        }

        // The alert's row holds nothing of where the visitor signed in from: no column of it, as text, holds their address or
        // their browser, and it carries no text at all. The address is in its audit row alone, which only the Owner sees.
        [Fact]
        public async Task AnAlertCarriesNoAddressAndNoText()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = factory.Visitor("198.51.100.77");
            admin.Client.DefaultRequestHeaders.UserAgent.ParseAdd("MadeUpBrowser/7.7");

            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            var row = Assert.Single(await db.RootQueryAsync<dynamic>($"SELECT * FROM {MariaDbFixture.RequestTable}"));
            var columns = (IDictionary<string, object?>)row;

            Assert.Null(columns["argument"]);

            foreach (var (name, value) in columns)
            {
                var text = value is byte[] bytes ? Convert.ToHexString(bytes) + System.Text.Encoding.Latin1.GetString(bytes) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                Assert.False(text.Contains("198.51.100.77", StringComparison.Ordinal), $"bot_commands.{name} holds the visitor's address");
                Assert.False(text.Contains("MadeUpBrowser", StringComparison.Ordinal), $"bot_commands.{name} holds the visitor's browser");
            }

            Assert.Equal("198.51.100.77", Assert.Single(await AlertAuditAsync(db)).ip);
        }

        // Signing in again, as before a control that needs a sign-in from the last 15 minutes, is a sign-in like any other,
        // so the owner hears about that one too, and the session it replaced is gone
        [Fact]
        public async Task SigningInAgainSendsAnotherAlert()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.158");
            var first = await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);

            factory.Clock.Advance(TimeSpan.FromMinutes(20));
            var again = await SignInSteps.SignInAgainAsync(factory, owner, TestUsers.Owner, "/admin/bot/controls#restart");

            Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
            Assert.NotEqual(first, owner.SessionKey);
            Assert.Null(await db.SessionAsync(first));

            var rows = await db.RequestsAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row => Assert.Equal((RequestKinds.OwnerSigninAlert, TestUsers.Owner.Id), (row.kind, row.actor_id)));
            Assert.Equal(TimeSpan.FromMinutes(20), rows[1].created_at - rows[0].created_at);
            Assert.Equal(2, (await AlertAuditAsync(db)).Count);
        }

        // The bot takes 20 alerts an hour, everyone's sign-ins together, and refuses the rest without sending anything. The
        // dashboard still writes every one, so the record exists, and the sign-in never waits for the bot. Fails if a
        // sign-in past the bot's limit is stopped or skips its alert.
        [Fact]
        public async Task AlertsPastTheBotsLimitAreStillWritten()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            for (var i = 20; i > 0; i--)
                await RequestSteps.TakenAsync(db, factory, RequestKinds.OwnerSigninAlert, now.AddMinutes(-2 * i), TestUsers.Admin);

            var key = await SignInSteps.SignedInAsync(factory, factory.Visitor("203.0.113.159"), TestUsers.Owner);

            Assert.NotNull(await db.SessionAsync(key));
            var rows = await db.RequestsAsync();
            Assert.Equal(21, rows.Count);
            Assert.Equal((RequestKinds.OwnerSigninAlert, TestUsers.Owner.Id, (string?)null), (rows[^1].kind, rows[^1].actor_id, rows[^1].state));
        }

        // Writes its rows, then fails as a dropped connection would, for the sign-in's alert
        sealed class FailingQueue(BotRequestQueue real) : IBotRequestQueue
        {
            public async Task<ulong> AddRequest(RequestFields fields, byte[] signature, DataTransaction transaction)
            {
                var id = await real.AddRequest(fields, signature, transaction);
                return fields.Kind == RequestKinds.OwnerSigninAlert ? throw new DroppedException() : id;
            }

            public Task<BotRequestRow?> GetRequest(ulong id) => real.GetRequest(id);
            public Task<BotRequestRow?> GetRequestByKey(string requestKey) => real.GetRequestByKey(requestKey);
            public Task<BotSettingsRow?> GetSettings() => real.GetSettings();
            public Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work) => real.RunInTransaction(work);
        }

        sealed class FailingAudit(AuditLogData real, string action) : IAuditLogData
        {
            public async Task AddAudit(AuditEntry entry, DataTransaction? transaction = null)
            {
                await real.AddAudit(entry, transaction);

                if (entry.action == action)
                    throw new DroppedException();
            }

            public Task<IReadOnlyList<AuditEntry>> LatestChanges(string targetType, IReadOnlyCollection<string> targetIds) => real.LatestChanges(targetType, targetIds);
            public Task<Page<AuditEntry>> ListAudit(AuditQuery query) => real.ListAudit(query);
            public Task<IReadOnlyList<DateTime>> ListActionTimes(ulong actorId, string action, IReadOnlyCollection<string> outcomes, DateTime from, int take) =>
                real.ListActionTimes(actorId, action, outcomes, from, take);
        }

        // What the driver throws for a connection that dropped
        sealed class DroppedException() : DbException("The connection to the database dropped");
    }
}
