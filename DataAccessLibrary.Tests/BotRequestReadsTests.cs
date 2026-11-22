using Dapper;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Requests.Models;
using DataAccessLibrary.Tests.Database;
using MinerBot.Shared.Requests;
using MySql.Data.MySqlClient;
using MySql.Data.Types;
using System.Data;
using System.Security.Cryptography;

namespace DataAccessLibrary.Tests
{
    // The dashboard's reads of its requests to the bot, for its pages and for what it counts before it sends one. Requests
    // are added through the dashboard's own code, and decided, taken and finished through the bot's, as
    // BotRequestDataTests makes them.
    [Collection("Database")]
    public class BotRequestReadsTests(MariaDbFixture fixture)
    {
        // A made-up request-signing key, the 32 bytes 00 01 ... 1f
        static readonly byte[] Key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

        static readonly DateTime Noon = new(2026, 11, 20, 12, 0, 0, 123, DateTimeKind.Utc);

        // This run, and an earlier run or another process
        const ulong Run = 18446744073709551615, OtherRun = 42;

        const ulong OwnerId = 1000000000000000001, AdminId = 1000000000000000002;
        const ulong GuildId = 1000000000000000005, OtherGuildId = 1000000000000000006, ChannelId = 2000000000000000001;

        // A login of this test's own, which may read every column of bot_commands but the key and the signature
        const string ReadsUser = "minerbot-reads-check";
        const string ReadsLogin = "'" + ReadsUser + "'@'%'";

        static CancellationToken Token => TestContext.Current.CancellationToken;

        async Task<MariaDbFixture> StartAsync()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.RequestTable, MariaDbFixture.SettingsTable);
            return db;
        }

        // A request as the dashboard writes one, asked for at the time given by the Owner or an Admin, with the server,
        // channel and text its kind takes
        static RequestFields Request(string kind, DateTime at, ulong actorId = OwnerId, ulong? guildId = null)
        {
            var details = RequestKinds.Find(kind)!;
            var argument = details.Argument switch
            {
                RequestArgument.Reason => "Testing",
                RequestArgument.StatusText => "Back soon",
                RequestArgument.CustomLinkName => "some_name",
                _ => null
            };
            var (name, role) = actorId == AdminId ? ("Test Admin", RequestRoles.Admin) : ("Test Owner", RequestRoles.Owner);

            return RequestFields.New(RequestFields.NewKey(), details, actorId, name, role, details.NeedsServer ? guildId ?? GuildId : null,
                details.NeedsChannel ? ChannelId : null, argument, at);
        }

        // Added through the dashboard's own code, signed with the test key
        static Task<ulong> AddAsync(MariaDbFixture db, RequestFields fields)
        {
            var queue = db.RequestQueue();
            return queue.RunInTransaction(transaction => queue.AddRequest(fields, RequestSigning.Sign(Key, fields), transaction));
        }

        // Asked for at the first time, taken by the run given at the second, then finished a second later, or left running
        static async Task<ulong> TakenAsync(MariaDbFixture db, string kind, DateTime askedAt, DateTime claimedAt, ulong runId = Run, ulong? guildId = null,
            string? state = null)
        {
            var id = await AddAsync(db, Request(kind, askedAt, guildId: guildId));
            Assert.True(await db.Requests().Claim(id, runId, claimedAt));

            if (state != null)
                Assert.True(await db.Requests().Finish(id, runId, state, "Ended", null, claimedAt.AddSeconds(1)));

            return id;
        }

        // Asked for at the time given, then refused or expired a second later without being taken
        static async Task<ulong> DecidedAsync(MariaDbFixture db, string kind, DateTime askedAt, string state, ulong actorId = OwnerId)
        {
            var id = await AddAsync(db, Request(kind, askedAt, actorId));
            Assert.True(await db.Requests().Decide(id, Run, state, state == RequestStates.Refused ? "Refused" : "Expired", askedAt.AddSeconds(1)));
            return id;
        }

        async Task<BotRequestRow> RowAsync(ulong id) =>
            (await fixture.RootQueryAsync<BotRequestRow>($"SELECT * FROM {MariaDbFixture.RequestTable} WHERE id = @id", new { id })).Single();

        // Who asked for each control last, when, and how the bot answered: the newest of each kind however it ended, with
        // every column a page shows as the row holds it, and nothing for a kind no request has
        [Fact]
        public async Task LatestOfEachKindIsTheNewestRow()
        {
            var db = await StartAsync();
            var reads = db.RequestQueue();
            var requests = db.Requests();

            await TakenAsync(db, RequestKinds.CachesClear, Noon, Noon.AddSeconds(1), state: RequestStates.Done);
            var clear = await TakenAsync(db, RequestKinds.CachesClear, Noon.AddSeconds(2), Noon.AddSeconds(3));
            Assert.True(await requests.Finish(clear, Run, RequestStates.Done, "Cleared the armor-name and latest-maps caches.", 7, Noon.AddSeconds(4)));
            Assert.True(await requests.SetDm(clear, RequestDmStates.Sent));
            var restart = await DecidedAsync(db, RequestKinds.BotRestart, Noon.AddSeconds(5), RequestStates.Refused);
            var pause = await AddAsync(db, Request(RequestKinds.PostsPause, Noon.AddSeconds(6)));
            await AddAsync(db, Request(RequestKinds.GuildTestMessage, Noon.AddSeconds(7)));
            var test = await AddAsync(db, Request(RequestKinds.GuildTestMessage, Noon.AddSeconds(8), guildId: OtherGuildId));

            var latest = await reads.ListLatest([RequestKinds.CachesClear, RequestKinds.BotRestart, RequestKinds.PostsPause, RequestKinds.GuildTestMessage,
                RequestKinds.StatusSet]);

            Assert.Equal([clear, restart, pause, test], latest.Select(row => row.id));

            // Each column as the row holds it, the bot's included, and every time marked UTC
            var row = await RowAsync(clear);
            var view = latest[0];
            Assert.Equal((row.kind, row.actor_id, row.actor_name, row.actor_role, row.guild_id, row.channel_id, row.argument),
                (view.kind, view.actor_id, view.actor_name, view.actor_role, view.guild_id, view.channel_id, view.argument));
            Assert.Equal(((DateTime?)row.created_at, (DateTime?)row.expires_at, row.claimed_at, row.ended_at), (view.created_at, view.expires_at, view.claimed_at, view.ended_at));
            Assert.Equal((RequestStates.Done, (ulong?)Run, "Cleared the armor-name and latest-maps caches.", (ulong?)7, RequestDmStates.Sent),
                (view.state, view.run_id, view.result, view.result_number, view.dm_state));
            Assert.All(new[] { view.created_at, view.expires_at, view.claimed_at, view.ended_at }, time => Assert.Equal(DateTimeKind.Utc, time!.Value.Kind));

            // A request the bot decided was never taken, and one that waits has nothing of the bot's
            Assert.Equal((RequestStates.Refused, (DateTime?)null, (DateTime?)Noon.AddSeconds(6), "Refused"), (latest[1].state, latest[1].claimed_at, latest[1].ended_at, latest[1].result));
            Assert.Equal(((string?)null, (ulong?)null, (DateTime?)null, (string?)null, "Testing"), (latest[2].state, latest[2].run_id, latest[2].ended_at, latest[2].result, latest[2].argument));
            Assert.Equal((OtherGuildId, ChannelId), (latest[3].guild_id, latest[3].channel_id));

            // One request on its own is the same as in the list
            Assert.Equivalent(view, await reads.GetRequestView(clear), strict: true);
            Assert.Null(await reads.GetRequestView(test + 1));

            // A kind has to be on the list, in its own letters, and the list has to name one
            await Assert.ThrowsAsync<ArgumentException>(() => reads.ListLatest(["caches.Clear"]));
            await Assert.ThrowsAsync<ArgumentException>(() => reads.ListLatest([]));
        }

        // A server's page shows its own latest test message, refresh and leave, never another server's, and nothing for a
        // kind it has none of
        [Fact]
        public async Task LatestForAServerIsThatServersAlone()
        {
            var db = await StartAsync();
            var reads = db.RequestQueue();
            string[] kinds = [RequestKinds.GuildTestMessage, RequestKinds.GuildRefresh, RequestKinds.GuildLeave];

            var test = await AddAsync(db, Request(RequestKinds.GuildTestMessage, Noon, guildId: GuildId));
            await AddAsync(db, Request(RequestKinds.GuildRefresh, Noon.AddSeconds(1), guildId: GuildId));
            var refresh = await AddAsync(db, Request(RequestKinds.GuildRefresh, Noon.AddSeconds(2), guildId: GuildId));
            var otherTest = await AddAsync(db, Request(RequestKinds.GuildTestMessage, Noon.AddSeconds(3), guildId: OtherGuildId));
            var otherLeave = await AddAsync(db, Request(RequestKinds.GuildLeave, Noon.AddSeconds(4), guildId: OtherGuildId));
            await AddAsync(db, Request(RequestKinds.CachesClear, Noon.AddSeconds(5)));

            Assert.Equal([test, refresh], (await reads.ListLatest(kinds, GuildId)).Select(row => row.id));
            Assert.Equal([otherTest, otherLeave], (await reads.ListLatest(kinds, OtherGuildId)).Select(row => row.id));
            Assert.Equal([refresh, otherTest, otherLeave], (await reads.ListLatest(kinds)).Select(row => row.id));
            Assert.Empty(await reads.ListLatest(kinds, 1000000000000000009));
        }

        // The dashboard checks the bot's limit before it writes a request, from the claims of the last hour, so they must be
        // what the bot counts. Rows in every state, taken by two runs, for two servers, many asked for before a window's
        // start and taken inside it: for every kind, its window and each server, the count and the oldest claim worked out
        // from ListClaimsSince, as BotLimits works them out, are what the bot's own CountClaims gives on the same rows.
        [Fact]
        public async Task PreCheckCountsAsTheBotCounts()
        {
            var db = await StartAsync();
            var requests = db.Requests();
            var now = Noon.AddHours(1);

            DateTime Before(double minutes) => now - TimeSpan.FromMinutes(minutes);

            // Asked for before the hour and taken in it, the second exactly at its start; refused, expired and waiting ones,
            // which were never taken
            await TakenAsync(db, RequestKinds.CachesClear, Before(61), Before(59.5), state: RequestStates.Done);
            await TakenAsync(db, RequestKinds.CachesClear, Before(60.5), Before(60), OtherRun, state: RequestStates.Failed);
            await DecidedAsync(db, RequestKinds.CachesClear, Before(30), RequestStates.Refused);
            await DecidedAsync(db, RequestKinds.CachesClear, Before(20), RequestStates.Expired);
            await AddAsync(db, Request(RequestKinds.CachesClear, Before(1)));

            // A minute's window: taken inside it, before it, and exactly at its start
            await TakenAsync(db, RequestKinds.JobNotifications, Before(2), Before(50 / 60.0), OtherRun, state: RequestStates.Failed);
            await TakenAsync(db, RequestKinds.JobNotifications, Before(3), Before(2), state: RequestStates.Done);
            await TakenAsync(db, RequestKinds.JobColors, Before(70 / 60.0), Before(1), state: RequestStates.Done);
            await TakenAsync(db, RequestKinds.JobColors, Before(1.5), Before(61 / 60.0), state: RequestStates.Done);

            // Pausing and resuming share a limit: one marked unknown, one still running, and one taken before the hour
            await TakenAsync(db, RequestKinds.PostsPause, Before(65), Before(58), OtherRun, state: RequestStates.Unknown);
            await TakenAsync(db, RequestKinds.PostsResume, Before(30), Before(29));
            await TakenAsync(db, RequestKinds.PostsPause, Before(75), Before(66), state: RequestStates.Done);

            await TakenAsync(db, RequestKinds.StatusSet, Before(62), Before(60.5), state: RequestStates.Done);
            await TakenAsync(db, RequestKinds.StatusSet, Before(10), Before(9), state: RequestStates.Done);
            await TakenAsync(db, RequestKinds.SteamReconnect, Before(6), Before(4.5), state: RequestStates.Done);
            await TakenAsync(db, RequestKinds.SteamRenew, Before(12), Before(10.5), state: RequestStates.Done);
            await TakenAsync(db, RequestKinds.SteamRenew, Before(11), Before(9.5), state: RequestStates.Failed);

            // Per server: each server's own, and a waiting one that doesn't count
            await TakenAsync(db, RequestKinds.GuildTestMessage, Before(11), Before(9.5), guildId: GuildId, state: RequestStates.Done);
            await TakenAsync(db, RequestKinds.GuildTestMessage, Before(12), Before(11), guildId: GuildId, state: RequestStates.Done);
            await TakenAsync(db, RequestKinds.GuildTestMessage, Before(3), Before(2), OtherRun, OtherGuildId, RequestStates.Failed);
            await AddAsync(db, Request(RequestKinds.GuildTestMessage, Before(1), guildId: OtherGuildId));
            await TakenAsync(db, RequestKinds.GuildRefresh, Before(1.5), Before(0.5), guildId: GuildId, state: RequestStates.Done);
            await TakenAsync(db, RequestKinds.GuildRefresh, Before(2), Before(70 / 60.0), guildId: OtherGuildId, state: RequestStates.Done);
            await AddAsync(db, Request(RequestKinds.GuildRefresh, Before(0.5), guildId: OtherGuildId));

            // A leave isn't counted per server
            await TakenAsync(db, RequestKinds.GuildLeave, Before(40), Before(39), guildId: GuildId, state: RequestStates.Done);
            await TakenAsync(db, RequestKinds.GuildLeave, Before(41), Before(40.5), OtherRun, OtherGuildId, RequestStates.Done);

            await TakenAsync(db, RequestKinds.SteamLookup, Before(30), Before(29), state: RequestStates.Failed);
            await DecidedAsync(db, RequestKinds.SteamLookup, Before(25), RequestStates.Refused);
            await TakenAsync(db, RequestKinds.BotRestart, Before(11), Before(9.9), state: RequestStates.Done);
            await DecidedAsync(db, RequestKinds.BotRestart, Before(5), RequestStates.Refused);
            await TakenAsync(db, RequestKinds.OwnerSigninAlert, Before(61), Before(55), OtherRun, state: RequestStates.Done);
            await DecidedAsync(db, RequestKinds.OwnerSigninAlert, Before(10), RequestStates.Refused);

            var claims = await db.RequestQueue().ListClaimsSince(now.AddHours(-1));
            var counts = new Dictionary<string, long>();

            foreach (var kind in RequestKinds.All)
            {
                foreach (var guildId in kind.PerServer ? new ulong?[] { GuildId, OtherGuildId, 1000000000000000009 } : [null])
                {
                    var since = now - kind.Window;
                    var counted = claims.Where(claim => kind.CountedWith.Contains(claim.kind) && claim.claimed_at >= since && (guildId == null || claim.guild_id == guildId))
                        .ToList();
                    var oldest = counted.Min(claim => claim.claimed_at);
                    var bot = await requests.CountClaims(kind.CountedWith, since, guildId);
                    var name = kind.Word + (guildId == GuildId ? " A" : guildId == OtherGuildId ? " B" : guildId != null ? " C" : "");

                    Assert.True(bot.count == counted.Count && bot.oldest == oldest,
                        $"{name}: the bot counts {bot.count}, the oldest at {bot.oldest:O}, and the dashboard {counted.Count}, the oldest at {oldest:O}");
                    counts[name] = counted.Count;
                }
            }

            // What the rows above make of each, so no kind's comparison is an empty one by mistake
            Assert.Equal(new Dictionary<string, long>
            {
                [RequestKinds.CachesClear] = 2, [RequestKinds.JobNotifications] = 1, [RequestKinds.JobColors] = 1, [RequestKinds.SteamRenew] = 1,
                [RequestKinds.PostsPause] = 2, [RequestKinds.PostsResume] = 2, [RequestKinds.StatusSet] = 1, [RequestKinds.SteamReconnect] = 1,
                [RequestKinds.GuildTestMessage + " A"] = 1, [RequestKinds.GuildTestMessage + " B"] = 1, [RequestKinds.GuildTestMessage + " C"] = 0,
                [RequestKinds.GuildRefresh + " A"] = 1, [RequestKinds.GuildRefresh + " B"] = 0, [RequestKinds.GuildRefresh + " C"] = 0,
                [RequestKinds.SteamLookup] = 1, [RequestKinds.GuildLeave] = 2, [RequestKinds.BotRestart] = 1, [RequestKinds.OwnerSigninAlert] = 1
            }, counts);
            Assert.All(claims, claim => Assert.Equal(DateTimeKind.Utc, claim.claimed_at!.Value.Kind));
            await Assert.ThrowsAsync<ArgumentException>(() => db.RequestQueue().ListClaimsSince(DateTime.Now));
        }

        // What waits: neither taken nor decided, and not past its expiry at the time given, oldest first, with its kind,
        // server and times. One past its expiry that the bot hasn't seen yet still has no state, but the bot will never
        // take it, so it doesn't wait.
        [Fact]
        public async Task WaitingRowsAreUnexpiredAndUntaken()
        {
            var db = await StartAsync();
            var reads = db.RequestQueue();
            var now = Noon.AddMinutes(5);

            await AddAsync(db, Request(RequestKinds.CachesClear, now.AddMinutes(-3)));
            var restart = await AddAsync(db, Request(RequestKinds.BotRestart, now.AddMinutes(-1)));
            await TakenAsync(db, RequestKinds.JobColors, now.AddMinutes(-1), now.AddSeconds(-30));
            await DecidedAsync(db, RequestKinds.StatusSet, now.AddMinutes(-1), RequestStates.Refused);
            var test = await AddAsync(db, Request(RequestKinds.GuildTestMessage, now.AddSeconds(-10), guildId: OtherGuildId));
            var pause = await AddAsync(db, Request(RequestKinds.PostsPause, now.AddMinutes(-9)));
            await TakenAsync(db, RequestKinds.CachesClear, now.AddSeconds(-20), now.AddSeconds(-10), state: RequestStates.Done);

            var waiting = await reads.ListWaiting(now);

            Assert.Equal([(restart, RequestKinds.BotRestart, (ulong?)null), (test, RequestKinds.GuildTestMessage, (ulong?)OtherGuildId), (pause, RequestKinds.PostsPause, (ulong?)null)],
                waiting.Select(row => (row.id, row.kind, row.guild_id)));
            Assert.Equal(((DateTime?)now.AddMinutes(-1), (DateTime?)now.AddMinutes(1)), (waiting[0].created_at, waiting[0].expires_at));
            Assert.All(waiting, row => Assert.Equal((DateTimeKind.Utc, DateTimeKind.Utc), (row.created_at!.Value.Kind, row.expires_at!.Value.Kind)));

            // Each stops waiting at its expiry
            Assert.Equal([test], (await reads.ListWaiting(now.AddMinutes(1))).Select(row => row.id));
            Assert.Empty(await reads.ListWaiting(now.AddMinutes(2)));
            await Assert.ThrowsAsync<ArgumentException>(() => reads.ListWaiting(DateTime.Now));
        }

        // One person's requests since a time, in any state, refused and expired ones included, since each was sent, and
        // never another person's
        [Fact]
        public async Task ByActorCountsOnlyThatPerson()
        {
            var db = await StartAsync();
            var reads = db.RequestQueue();
            var now = Noon.AddHours(1);

            await AddAsync(db, Request(RequestKinds.CachesClear, now.AddMinutes(-61)));
            await DecidedAsync(db, RequestKinds.CachesClear, now.AddMinutes(-60), RequestStates.Refused);
            await DecidedAsync(db, RequestKinds.JobNotifications, now.AddMinutes(-30), RequestStates.Expired);
            await TakenAsync(db, RequestKinds.SteamLookup, now.AddMinutes(-20), now.AddMinutes(-19), state: RequestStates.Done);
            await AddAsync(db, Request(RequestKinds.CachesClear, now.AddMinutes(-50), AdminId));
            await DecidedAsync(db, RequestKinds.OwnerSigninAlert, now.AddMinutes(-5), RequestStates.Refused, AdminId);
            await AddAsync(db, Request(RequestKinds.OwnerSigninAlert, now.AddMinutes(-1)));

            var owner = await reads.ListAskedBy(OwnerId, now.AddHours(-1));

            Assert.Equal([(RequestKinds.CachesClear, now.AddMinutes(-60)), (RequestKinds.JobNotifications, now.AddMinutes(-30)), (RequestKinds.SteamLookup, now.AddMinutes(-20)),
                (RequestKinds.OwnerSigninAlert, now.AddMinutes(-1))], owner.Select(row => (row.kind, row.created_at!.Value)));
            Assert.All(owner, row => Assert.Equal(DateTimeKind.Utc, row.created_at!.Value.Kind));

            Assert.Equal([RequestKinds.CachesClear, RequestKinds.OwnerSigninAlert], (await reads.ListAskedBy(AdminId, now.AddHours(-1))).Select(row => row.kind));
            Assert.Empty(await reads.ListAskedBy(OwnerId + 7, now.AddHours(-1)));
            await Assert.ThrowsAsync<ArgumentException>(() => reads.ListAskedBy(OwnerId, DateTime.Now));
        }

        // The bot's last direct message to the owner, sent or failed, is the newest request that has one, whatever came after
        // without one. The sign-in alerts since a time count in any state, everyone's, and nothing else. The latest alert the
        // bot refused stays that one whatever came after it, alerts in other states and other kinds refused.
        [Fact]
        public async Task TheLatestDirectMessageAndTheAlertsSinceATime()
        {
            var db = await StartAsync();
            var reads = db.RequestQueue();
            var requests = db.Requests();

            Assert.Null(await reads.LatestDm());
            Assert.Equal(0, await reads.CountAlertsSince(Noon.AddHours(-1)));
            Assert.Null(await reads.LatestRefusedAlert());

            var sent = await TakenAsync(db, RequestKinds.BotRestart, Noon, Noon.AddSeconds(1), state: RequestStates.Done);
            Assert.True(await requests.SetDm(sent, RequestDmStates.Sent));
            var failed = await TakenAsync(db, RequestKinds.OwnerSigninAlert, Noon.AddSeconds(2), Noon.AddSeconds(3), state: RequestStates.Done);
            Assert.True(await requests.SetDm(failed, RequestDmStates.Failed));
            await TakenAsync(db, RequestKinds.PostsPause, Noon.AddSeconds(4), Noon.AddSeconds(5), state: RequestStates.Done);
            var refused = await DecidedAsync(db, RequestKinds.OwnerSigninAlert, Noon.AddSeconds(6), RequestStates.Refused);
            await AddAsync(db, Request(RequestKinds.OwnerSigninAlert, Noon.AddSeconds(7), AdminId));
            await AddAsync(db, Request(RequestKinds.OwnerSigninAlert, Noon.AddHours(-2)));
            await AddAsync(db, Request(RequestKinds.CachesClear, Noon.AddSeconds(8)));

            var dm = await reads.LatestDm();
            Assert.Equal((failed, RequestKinds.OwnerSigninAlert, RequestDmStates.Failed), (dm?.id, dm?.kind, dm?.dm_state));
            Assert.Equal((DateTimeKind.Utc, DateTimeKind.Utc), (dm!.claimed_at!.Value.Kind, dm.ended_at!.Value.Kind));

            Assert.Equal(3, await reads.CountAlertsSince(Noon.AddHours(-1)));
            Assert.Equal(2, await reads.CountAlertsSince(Noon.AddSeconds(6)));
            Assert.Equal(0, await reads.CountAlertsSince(Noon.AddSeconds(8)));
            await Assert.ThrowsAsync<ArgumentException>(() => reads.CountAlertsSince(DateTime.Now));

            await DecidedAsync(db, RequestKinds.CachesClear, Noon.AddSeconds(9), RequestStates.Refused);
            await DecidedAsync(db, RequestKinds.OwnerSigninAlert, Noon.AddSeconds(10), RequestStates.Expired, AdminId);
            await TakenAsync(db, RequestKinds.OwnerSigninAlert, Noon.AddSeconds(11), Noon.AddSeconds(12), state: RequestStates.Done);

            var alert = await reads.LatestRefusedAlert();
            Assert.Equal((refused, RequestKinds.OwnerSigninAlert, RequestStates.Refused, "Refused"), (alert?.id, alert?.kind, alert?.state, alert?.result));
            Assert.Equal((DateTimeKind.Utc, DateTimeKind.Utc), (alert!.created_at!.Value.Kind, alert.ended_at!.Value.Kind));
        }

        // MariaDB's default mode lets the dashboard's own login write a time of 0000-00-00, or one with a month of 0, and
        // MySql.Data can't read either. A guard that only looks at the year misses the second, which compares as a time in
        // 2027, so it still waits and still counts. Every read gives such a time as none and the rest of the row as it is, so
        // one such row can't take a page down. The bot's own columns get theirs as root, since only the bot's login could
        // write them.
        [Fact]
        public async Task ZeroAndZeroMonthDatesComeBackAsNoTime()
        {
            var db = await StartAsync();
            var reads = db.RequestQueue();
            ulong zero = 0, zeroMonth = 0;

            await DashboardLogin.AsDashboardAsync(db, async password =>
            {
                await using var connection = new MySqlConnection(DashboardLogin.ConnectionAs(db, password, MariaDbFixture.BotDatabase));
                await connection.OpenAsync(Token);

                async Task<ulong> AddByHandAsync(string kind, string argument, string time)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        $@"INSERT INTO {MariaDbFixture.RequestTable} (request_key, kind, actor_id, actor_name, actor_role, argument, created_at, expires_at, signature)
                           VALUES (@key, @kind, @actor, 'Test Owner', 'owner', @argument, @time, @time, REPEAT('s', 32))",
                        new { key = RequestFields.NewKey(), kind, actor = OwnerId, argument, time }, cancellationToken: Token));
                    return await connection.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT LAST_INSERT_ID()", cancellationToken: Token));
                }

                zero = await AddByHandAsync(RequestKinds.PostsPause, "Testing", "0000-00-00 00:00:00.000");
                zeroMonth = await AddByHandAsync(RequestKinds.StatusSet, "Back soon", "2027-00-10 00:00:00.000");
            });

            var taken = await TakenAsync(db, RequestKinds.CachesClear, Noon, Noon.AddSeconds(1), state: RequestStates.Done);
            await db.RootExecuteAsync($@"UPDATE {MariaDbFixture.RequestTable} SET claimed_at = '2027-00-10 00:00:00.000', ended_at = '0000-00-00 00:00:00.000', dm_state = 'sent'
                                         WHERE id = @taken", new { taken });

            // MySql.Data itself can't read them, which is what the guard is for
            var unreadable = await Assert.ThrowsAsync<DataException>(() => db.RequestQueue().GetRequest(zeroMonth));
            Assert.IsType<MySqlConversionException>(unreadable.InnerException);

            foreach (var id in new[] { zero, zeroMonth })
            {
                var view = await reads.GetRequestView(id);
                Assert.NotNull(view);
                Assert.Equal(((DateTime?)null, (DateTime?)null, "Test Owner"), (view.created_at, view.expires_at, view.actor_name));
            }

            var latest = await reads.ListLatest([RequestKinds.PostsPause, RequestKinds.StatusSet, RequestKinds.CachesClear]);
            Assert.Equal([zero, zeroMonth, taken], latest.Select(row => row.id));
            Assert.Equal(["Testing", "Back soon"], latest.Take(2).Select(row => row.argument));
            Assert.All(latest.Take(2), row => Assert.Equal(((DateTime?)null, (DateTime?)null), (row.created_at, row.expires_at)));
            Assert.Equal(((DateTime?)Noon, (DateTime?)null, (DateTime?)null, RequestStates.Done), (latest[2].created_at, latest[2].claimed_at, latest[2].ended_at, latest[2].state));

            var waiting = Assert.Single(await reads.ListWaiting(Noon));
            Assert.Equal((zeroMonth, (DateTime?)null, (DateTime?)null), (waiting.id, waiting.created_at, waiting.expires_at));
            Assert.Equal([(RequestKinds.StatusSet, (DateTime?)null), (RequestKinds.CachesClear, (DateTime?)Noon)],
                (await reads.ListAskedBy(OwnerId, Noon.AddHours(-1))).Select(row => (row.kind, row.created_at)));

            var claim = Assert.Single(await reads.ListClaimsSince(Noon.AddHours(-1)));
            Assert.Equal((taken, RequestKinds.CachesClear, (DateTime?)null), (claim.id, claim.kind, claim.claimed_at));

            var dm = await reads.LatestDm();
            Assert.Equal((taken, (DateTime?)null, (DateTime?)null, RequestDmStates.Sent), (dm?.id, dm?.claimed_at, dm?.ended_at, dm?.dm_state));
        }

        // No page needs a request's key or its signature. Every read works as a login that may read every column of
        // bot_commands but those two, which MariaDB refuses with 1143 for a statement that names either or asks for every
        // column, so none of them reads either. And no read's row has room for them.
        [Fact]
        public async Task NoReadReturnsTheKeyOrTheSignature()
        {
            var db = await StartAsync();

            var taken = await TakenAsync(db, RequestKinds.CachesClear, Noon, Noon.AddSeconds(1), state: RequestStates.Done);
            Assert.True(await db.Requests().SetDm(taken, RequestDmStates.Sent));
            var test = await AddAsync(db, Request(RequestKinds.GuildTestMessage, Noon.AddSeconds(2)));
            var alert = await AddAsync(db, Request(RequestKinds.OwnerSigninAlert, Noon.AddSeconds(3)));

            var columns = await db.RootQueryAsync<string>(
                "SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = 'bot_commands' ORDER BY ORDINAL_POSITION",
                new { schema = MariaDbFixture.BotDatabase });
            Assert.Contains("request_key", columns);
            Assert.Contains("signature", columns);
            var readable = string.Join(", ", columns.Where(column => column is not ("request_key" or "signature")).Select(column => $"`{column}`"));
            var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

            await db.RootExecuteAsync($"DROP USER IF EXISTS {ReadsLogin}");
            await db.RootExecuteAsync($"CREATE USER {ReadsLogin} IDENTIFIED BY '{password}'");

            try
            {
                await db.RootExecuteAsync($"GRANT SELECT ({readable}) ON {MariaDbFixture.RequestTable} TO {ReadsLogin}");

                var connection = new MySqlConnectionStringBuilder(db.ConnectionAs(ReadsUser, password, MariaDbFixture.BotDatabase)) { Pooling = false }.ConnectionString;
                var connections = new Dictionary<DBConnectionName, string>(db.Connections) { [DBConnectionName.Bot] = connection };
                var reads = new BotRequestQueue(new SQLConnectionFactory(connections), new DatabaseNames(connections));

                // The login can't read either column itself
                await using (var login = new MySqlConnection(connection))
                {
                    await login.OpenAsync(Token);
                    var refused = await Assert.ThrowsAsync<MySqlException>(() => login.ExecuteScalarAsync(new CommandDefinition(
                        $"SELECT signature FROM {MariaDbFixture.RequestTable} LIMIT 1", cancellationToken: Token)));
                    Assert.Equal(1143, refused.Number);
                }

                Assert.Equal(taken, (await reads.GetRequestView(taken))?.id);
                Assert.Equal([taken, test, alert], (await reads.ListLatest([.. RequestKinds.All.Select(kind => kind.Word)])).Select(row => row.id));
                Assert.Equal(RequestKinds.CachesClear, Assert.Single(await reads.ListClaimsSince(Noon.AddHours(-1))).kind);
                Assert.Equal([test, alert], (await reads.ListWaiting(Noon)).Select(row => row.id));
                Assert.Equal(3, (await reads.ListAskedBy(OwnerId, Noon.AddHours(-1))).Count);
                Assert.Equal(taken, (await reads.LatestDm())?.id);
                Assert.Null(await reads.LatestRefusedAlert());
                Assert.Equal(1, await reads.CountAlertsSince(Noon.AddHours(-1)));
            }
            finally
            {
                await db.RootExecuteAsync($"DROP USER IF EXISTS {ReadsLogin}");
            }

            foreach (var type in new[] { typeof(RequestPageRow), typeof(ClaimRow), typeof(WaitingRow), typeof(AskedRow) })
                Assert.DoesNotContain(type.GetProperties(), property => property.Name is "request_key" or "signature");
        }
    }
}
