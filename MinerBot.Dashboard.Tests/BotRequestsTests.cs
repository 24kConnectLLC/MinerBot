using System.Globalization;
using System.Security.Cryptography;
using DataAccessLibrary;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Requests.Models;
using DataAccessLibrary.Tests.Database;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Settings;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Tests
{
    // The dashboard's requests to the bot, as PR 10's pages and request.sh's command send them: checked against the shared
    // list, signed, and written with their audit row in one transaction, against the test database
    [Collection("Database")]
    public class BotRequestsTests(MariaDbFixture fixture)
    {
        // Made up, as setup-dashboard.sh makes one: 64 hexadecimal digits in small letters
        static readonly string KeyText = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        const ulong OwnerId = 1000000000000000001;
        const ulong AdminId = 1000000000000000002;
        const ulong ServerId = 1000000000000000005;
        const ulong ChannelId = 1000000000000000006;

        static readonly RequestActor Owner = new(OwnerId, "Test Owner", RequestRoles.Owner);
        static readonly RequestActor Admin = new(AdminId, "Test Admin", RequestRoles.Admin);

        async Task<MariaDbFixture> ReadyAsync()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.RequestTable, MariaDbFixture.AuditTable);
            return db;
        }

        static BotRequests Requests(IBotRequestQueue queue, IAuditLogData audit, TimeProvider time, string key) =>
            new(queue, audit, Options.Create(new DashboardOptions { RequestSigningKey = key }), time, NullLogger<BotRequests>.Instance);

        static BotRequests Requests(MariaDbFixture db, TimeProvider? time = null) => Requests(db.RequestQueue(), db.Audit(), time ?? new FakeClock(), KeyText);

        static async Task<long> CountAsync(MariaDbFixture db, string table) => (await db.RootQueryAsync<long>($"SELECT COUNT(*) FROM {table}")).Single();

        // The request row and its audit row go in together, with everything each needs: the request as it was signed, and
        // who asked for which kind, about which request, from where
        [Fact]
        public async Task ARequestAndItsAuditRowCommitTogether()
        {
            var db = await ReadyAsync();
            var clock = new FakeClock();
            var requests = Requests(db, clock);

            var pause = await requests.SendAsync(new RequestOrder(RequestKinds.PostsPause, Argument: "Testing the queue"), Owner, "203.0.113.70", "made-up-trace-1");

            Assert.True(pause.Confirmed);
            Assert.False(pause.Repeated);
            Assert.Equal(RequestKinds.PostsPause, pause.Kind);
            Assert.Equal(RequestKinds.SlowExpiry, pause.ExpiresAt - pause.CreatedAt);
            var row = await db.RequestQueue().GetRequest(pause.Id);
            Assert.NotNull(row);
            Assert.Equal(pause.RequestKey, row.request_key);
            Assert.Equal(RequestKinds.PostsPause, row.kind);
            Assert.Equal(OwnerId, row.actor_id);
            Assert.Equal("Test Owner", row.actor_name);
            Assert.Equal(RequestRoles.Owner, row.actor_role);
            Assert.Equal("Testing the queue", row.argument);
            Assert.Null(row.guild_id);
            Assert.Null(row.channel_id);
            Assert.Equal(pause.CreatedAt, row.created_at);
            Assert.Equal(RequestSigning.SignatureLength, row.signature.Length);
            Assert.Null(row.state);
            Assert.Null(row.claimed_at);

            var audit = Assert.Single(await db.AuditAsync());
            Assert.Equal(RequestKinds.PostsPause, audit.action);
            Assert.Equal(AuditOutcome.Ok, audit.outcome);
            Assert.Equal(AuditWords.RequestTarget, audit.target_type);
            Assert.Equal(pause.Id.ToString(CultureInfo.InvariantCulture), audit.target_id);
            Assert.Equal(OwnerId, audit.actor_id);
            Assert.Equal("Test Owner", audit.actor_name);
            Assert.Equal(AuditRole.Owner, audit.actor_role);
            Assert.Null(audit.guild_id);
            Assert.Null(audit.before_json);
            Assert.Equal("""{"Text":"Testing the queue"}""", audit.after_json);
            Assert.Equal("203.0.113.70", audit.ip);
            Assert.Equal("made-up-trace-1", audit.request_id);
            Assert.Equal(pause.CreatedAt, DateTime.SpecifyKind(audit.at, DateTimeKind.Utc));

            // A test message an Admin asks for: its server in the audit row's own column, and its channel as text in the JSON
            var test = await requests.SendAsync(new RequestOrder(RequestKinds.GuildTestMessage, ServerId, ChannelId), Admin, null, null);

            var testRow = await db.RequestQueue().GetRequest(test.Id);
            Assert.Equal(ServerId, testRow?.guild_id);
            Assert.Equal(ChannelId, testRow?.channel_id);
            var second = (await db.AuditAsync())[^1];
            Assert.Equal(RequestKinds.GuildTestMessage, second.action);
            Assert.Equal(AuditRole.Admin, second.actor_role);
            Assert.Equal(ServerId, second.guild_id);
            Assert.Equal($$"""{"Channel":"{{ChannelId}}"}""", second.after_json);

            // An audit row whose connection drops right after it's written, before the commit: the whole transaction rolls
            // back, so neither the request nor its audit row is there, and the caller is told the database didn't answer
            var failing = Requests(db.RequestQueue(), new FailingAudit(db.Audit()), clock, KeyText);
            await Assert.ThrowsAsync<DashboardUnavailableException>(() => failing.SendAsync(new RequestOrder(RequestKinds.CachesClear), Owner, null, null));

            Assert.Equal(2, await CountAsync(db, MariaDbFixture.RequestTable));
            Assert.Equal(2, await CountAsync(db, MariaDbFixture.AuditTable));
        }

        // The role, the kind and the details are checked before anything is written, in the shared rules' words, which
        // never quote the request's text
        [Fact]
        public async Task ARoleTheKindDoesntAllowWritesNothing()
        {
            var db = await ReadyAsync();
            var requests = Requests(db);
            var serverAdmin = new RequestActor(1000000000000000003, "Test Server Admin", RequestRoles.ServerAdmin);

            async Task<string> RefusedAsync(RequestOrder order, RequestActor actor) =>
                (await Assert.ThrowsAsync<RequestRefusedException>(() => requests.SendAsync(order, actor, null, null))).Problem;

            Assert.Equal("only the Owner can ask for it", await RefusedAsync(new RequestOrder(RequestKinds.BotRestart), Admin));
            Assert.Equal("only the Owner can ask for it", await RefusedAsync(new RequestOrder(RequestKinds.GuildLeave, ServerId), Admin));
            Assert.Equal("only the Owner or an Admin can ask for it", await RefusedAsync(new RequestOrder(RequestKinds.CachesClear), serverAdmin));
            Assert.Equal("its kind isn't on the request list", await RefusedAsync(new RequestOrder("bot.shutdown"), Owner));
            Assert.Equal("its role isn't a word of the request list's form", await RefusedAsync(new RequestOrder(RequestKinds.CachesClear), Owner with { Role = "Owner" }));

            var link = await RefusedAsync(new RequestOrder(RequestKinds.StatusSet, Argument: "Join at https://evil.example.test"), Owner);
            Assert.Equal("the status text has a link", link);
            Assert.DoesNotContain("evil", link);
            Assert.Equal("it needs a channel", await RefusedAsync(new RequestOrder(RequestKinds.GuildTestMessage, ServerId), Owner));
            Assert.Equal("it takes no text", await RefusedAsync(new RequestOrder(RequestKinds.BotRestart, Argument: "now"), Owner));
            Assert.Equal("the reason is empty", await RefusedAsync(new RequestOrder(RequestKinds.PostsPause), Owner));

            Assert.Equal(0, await CountAsync(db, MariaDbFixture.RequestTable));
            Assert.Equal(0, await CountAsync(db, MariaDbFixture.AuditTable));
        }

        // Without a key, requests to the bot are off: nothing is signed or written, and the caller is told why
        [Theory]
        [InlineData("")]
        [InlineData("0123456789abcdef")]
        public async Task WithoutAKeyNothingIsWritten(string key)
        {
            var db = await ReadyAsync();
            var requests = Requests(db.RequestQueue(), db.Audit(), new FakeClock(), key);

            await Assert.ThrowsAsync<RequestsOffException>(() => requests.SendAsync(new RequestOrder(RequestKinds.CachesClear), Owner, null, null));

            Assert.Equal(0, await CountAsync(db, MariaDbFixture.RequestTable));
            Assert.Equal(0, await CountAsync(db, MariaDbFixture.AuditTable));
        }

        // The bot verifies the row as its own repository reads it back, so the signature must cover the times the table keeps:
        // a clock with ticks below the millisecond, which DATETIME(3) drops without a word, still gives a row that verifies.
        // Fails if the dashboard signed the time before cutting it.
        [Fact]
        public async Task TheSignatureVerifiesWithTheBotsCode()
        {
            var db = await ReadyAsync();
            var clock = new FakeClock();
            clock.Advance(TimeSpan.FromTicks(1_234_567));
            var requests = Requests(db, clock);
            var text = "Zoë " + char.ConvertFromUtf32(0x1F3AE) + " checks the queue";

            var queued = await requests.SendAsync(new RequestOrder(RequestKinds.StatusSet, Argument: text), Owner, null, null);
            var row = await db.Requests().GetRequest(queued.Id);

            Assert.NotNull(row);
            Assert.Equal(text, row.argument);
            Assert.Equal(0, row.created_at.Ticks % TimeSpan.TicksPerMillisecond);
            Assert.Equal(clock.GetUtcNow().UtcTicks / TimeSpan.TicksPerMillisecond, row.created_at.Ticks / TimeSpan.TicksPerMillisecond);
            Assert.True(RequestSigning.Verify(Convert.FromHexString(KeyText), row.Fields(), row.signature));
            Assert.False(RequestSigning.Verify(RandomNumberGenerator.GetBytes(32), row.Fields(), row.signature));
        }

        // The name goes into the signed row as the bot's checks take it: control characters as spaces, a broken character as
        // U+FFFD, and cut to 100 characters without splitting one. One with nothing left is the person's ID. Fails if a
        // name the bot would refuse got signed, or one was refused.
        [Fact]
        public async Task NamesAreCleanedBeforeSigning()
        {
            var db = await ReadyAsync();
            var requests = Requests(db);
            var bell = ((char)7).ToString();
            var lone = ((char)0xD800).ToString();
            var emoji = char.ConvertFromUtf32(0x1F3AE);
            var replacement = ((char)0xFFFD).ToString();

            // 13 characters, then 86, then an emoji as the 100th, then more
            var name = "Test" + bell + "Owner\r\n" + lone + new string('n', 86) + emoji + "and more";
            var cleaned = "Test Owner  " + replacement + new string('n', 86) + emoji;

            var queued = await requests.SendAsync(new RequestOrder(RequestKinds.CachesClear), Owner with { Name = name }, null, null);
            var row = await db.Requests().GetRequest(queued.Id);

            Assert.NotNull(row);
            Assert.Equal(cleaned, row.actor_name);
            Assert.Equal(100, row.actor_name.EnumerateRunes().Count());
            Assert.True(RequestSigning.Verify(Convert.FromHexString(KeyText), row.Fields(), row.signature));
            Assert.Equal(cleaned, Assert.Single(await db.AuditAsync()).actor_name);

            var blank = await requests.SendAsync(new RequestOrder(RequestKinds.CachesClear), Owner with { Name = ((char)9).ToString() + "  " }, null, null);
            Assert.Equal("Discord user 1000000000000000001", (await db.Requests().GetRequest(blank.Id))?.actor_name);

            Assert.Equal("Discord user 1000000000000000001", BotRequests.CleanName(null, OwnerId));
        }

        // A commit the database didn't confirm may have landed: the caller is told so and gets the key to look the request up
        // by, and an audit row of its own says so, with the request's action, target and text
        [Fact]
        public async Task AnUnconfirmedCommitWritesAnUnknownAuditRow()
        {
            var db = await ReadyAsync();
            var requests = Requests(new UnconfirmedQueue(db.RequestQueue()), db.Audit(), new FakeClock(), KeyText);

            var queued = await requests.SendAsync(new RequestOrder(RequestKinds.PostsPause, Argument: "Testing"), Owner, "203.0.113.71", "made-up-trace-2");

            Assert.False(queued.Confirmed);
            Assert.Equal(0UL, queued.Id);
            var row = await db.RequestQueue().GetRequestByKey(queued.RequestKey);
            Assert.NotNull(row);

            var rows = await db.AuditAsync();
            Assert.Equal([AuditOutcome.Ok, AuditOutcome.Unknown], rows.Select(entry => entry.outcome));
            Assert.Equal(RequestKinds.PostsPause, rows[1].action);
            Assert.Equal(AuditWords.RequestTarget, rows[1].target_type);
            Assert.Equal(row.id.ToString(CultureInfo.InvariantCulture), rows[1].target_id);
            Assert.Equal(rows[0].after_json, rows[1].after_json);
            Assert.Equal(UnconfirmedChanges.Detail, rows[1].detail);
            Assert.Equal("203.0.113.71", rows[1].ip);
            Assert.Equal("made-up-trace-2", rows[1].request_id);
        }

        // A page that passes the key it made when it drew its form gets one request for one form, however often it's sent:
        // the second sending is told about the first, and writes nothing. A key from another person's request isn't
        // answered with that request.
        [Fact]
        public async Task AFormKeyGivenTwiceWritesOneRow()
        {
            var db = await ReadyAsync();
            var clock = new FakeClock();
            var requests = Requests(db, clock);
            var formKey = RequestFields.NewKey();
            var order = new RequestOrder(RequestKinds.PostsResume, FormKey: formKey);

            var first = await requests.SendAsync(order, Owner, null, null);
            clock.Advance(TimeSpan.FromSeconds(5));
            var again = await requests.SendAsync(order, Owner, null, null);

            Assert.Equal(formKey, first.RequestKey);
            Assert.False(first.Repeated);
            Assert.True(again.Repeated);
            Assert.True(again.Confirmed);
            Assert.Equal((first.Id, first.CreatedAt, first.ExpiresAt), (again.Id, again.CreatedAt, again.ExpiresAt));
            Assert.Equal(1, await CountAsync(db, MariaDbFixture.RequestTable));
            Assert.Equal(1, await CountAsync(db, MariaDbFixture.AuditTable));

            await Assert.ThrowsAsync<InvalidOperationException>(() => requests.SendAsync(order, Owner with { Id = AdminId }, null, null));
            await Assert.ThrowsAsync<InvalidOperationException>(() => requests.SendAsync(new RequestOrder(RequestKinds.CachesClear, FormKey: formKey), Owner, null, null));
            Assert.Equal(1, await CountAsync(db, MariaDbFixture.RequestTable));
            Assert.Equal(1, await CountAsync(db, MariaDbFixture.AuditTable));
        }

        static TimeoutException Dropped() => new("The database didn't answer within 15 seconds");

        // Writes its row, then fails as a dropped connection would after the statement ran
        sealed class FailingAudit(AuditLogData real) : IAuditLogData
        {
            public async Task AddAudit(AuditEntry entry, DataTransaction? transaction = null)
            {
                await real.AddAudit(entry, transaction);
                throw Dropped();
            }

            public Task<IReadOnlyList<AuditEntry>> LatestChanges(string targetType, IReadOnlyCollection<string> targetIds) => real.LatestChanges(targetType, targetIds);
            public Task<Page<AuditEntry>> ListAudit(AuditQuery query) => real.ListAudit(query);
        }

        // The real queue, whose transactions commit and then time out, as a commit whose answer never came
        sealed class UnconfirmedQueue(BotRequestQueue real) : IBotRequestQueue
        {
            public async Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work)
            {
                await real.RunInTransaction(work);
                throw Dropped();
            }

            public Task<ulong> AddRequest(RequestFields fields, byte[] signature, DataTransaction transaction) => real.AddRequest(fields, signature, transaction);
            public Task<BotRequestRow?> GetRequest(ulong id) => real.GetRequest(id);
            public Task<BotRequestRow?> GetRequestByKey(string requestKey) => real.GetRequestByKey(requestKey);
            public Task<BotSettingsRow?> GetSettings() => real.GetSettings();
        }
    }
}
