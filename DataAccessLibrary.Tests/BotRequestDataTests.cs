using Dapper;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Requests.Models;
using DataAccessLibrary.Tests.Database;
using MinerBot.Shared.Requests;
using MySql.Data.MySqlClient;
using MySql.Data.Types;
using System.Data;

namespace DataAccessLibrary.Tests
{
    // The dashboard's requests to the bot, as the dashboard's code adds them and the bot's code reads, decides, takes and
    // finishes them, and the bot's settings. Each write says in its WHERE when it applies, so a request is acted on at most
    // once whatever reads it, and every time is the one the bot passes in.
    [Collection("Database")]
    public class BotRequestDataTests(MariaDbFixture fixture)
    {
        // A made-up request-signing key, the 32 bytes 00 01 ... 1f
        static readonly byte[] Key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

        static readonly DateTime Noon = new(2026, 11, 20, 12, 0, 0, 123, DateTimeKind.Utc);

        // This run, and an earlier run or another process
        const ulong Run = 18446744073709551615, OtherRun = 42;

        const ulong OwnerId = 1000000000000000001, GuildId = 1000000000000000005, OtherGuildId = 1000000000000000006, ChannelId = 2000000000000000001;

        static CancellationToken Token => TestContext.Current.CancellationToken;

        async Task<MariaDbFixture> StartAsync()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.RequestTable, MariaDbFixture.SettingsTable, MariaDbFixture.AuditTable);
            return db;
        }

        // A request as the dashboard writes one, made at the time given
        static RequestFields Request(string kind = RequestKinds.CachesClear, DateTime? at = null, ulong? guildId = null, ulong? channelId = null,
            string? argument = null, string key = "") =>
            RequestFields.New(key.Length > 0 ? key : RequestFields.NewKey(), RequestKinds.Find(kind)!, OwnerId, "Test Owner", RequestRoles.Owner, guildId, channelId,
                argument, at ?? Noon);

        // Added through the dashboard's own code, signed with the test key
        static Task<ulong> AddAsync(MariaDbFixture db, RequestFields fields)
        {
            var queue = db.RequestQueue();
            return queue.RunInTransaction(transaction => queue.AddRequest(fields, RequestSigning.Sign(Key, fields), transaction));
        }

        // Added, then taken by the run given at the time given
        static async Task<ulong> TakenAsync(MariaDbFixture db, ulong runId, DateTime claimedAt, string kind = RequestKinds.CachesClear, ulong? guildId = null)
        {
            var details = RequestKinds.Find(kind)!;
            var argument = details.Argument switch
            {
                RequestArgument.Reason => "Testing",
                RequestArgument.CustomLinkName => "some_name",
                _ => null
            };
            var id = await AddAsync(db, Request(kind, claimedAt.AddSeconds(-1), guildId ?? (details.NeedsServer ? GuildId : null),
                details.NeedsChannel ? ChannelId : null, argument));

            Assert.True(await db.Requests().Claim(id, runId, claimedAt));
            return id;
        }

        // A count as CountClaims gives it
        static (long, DateTime?) Counted(long count, DateTime? oldest) => (count, oldest);

        async Task<BotRequestRow> RowAsync(ulong id) =>
            (await fixture.RootQueryAsync<BotRequestRow>($"SELECT * FROM {MariaDbFixture.RequestTable} WHERE id = @id", new { id })).Single();

        // MariaDB's server default is latin1 here, as on the live server, and the connections name no character set, so only
        // the repositories' own utf8mb4 keeps the name and the text as they were signed: an emoji, a zero-width space and a
        // space at the end of the name, and a status text of 128 characters that ends in a combining mark. The dashboard
        // made it at a time past the millisecond, which DATETIME(3) would cut.
        [Fact]
        public async Task ARequestTheDashboardSignedVerifiesAsTheBotReadsIt()
        {
            var db = await StartAsync();
            var connections = new Dictionary<DBConnectionName, string>(db.Connections)
            {
                [DBConnectionName.Bot] = db.LoginConnectionWithoutCharacterSet(MariaDbFixture.BotDatabase)
            };
            var factory = new SQLConnectionFactory(connections);
            var names = new DatabaseNames(connections);
            var queue = new BotRequestQueue(factory, names);
            var bot = new BotRequestData(factory, names);

            var now = new DateTime(2026, 11, 20, 15, 4, 5, 123, DateTimeKind.Utc).AddTicks(4567);
            var status = new string('s', 126) + "e\u0301";
            var fields = RequestFields.New(RequestFields.NewKey(), RequestKinds.Find(RequestKinds.StatusSet)!, OwnerId, "Zo\u00EB \U0001F3AE\u200B ", RequestRoles.Owner,
                null, null, status, now);

            Assert.Equal(128, status.Length);
            Assert.Null(RequestRules.Problem(fields));

            var id = await queue.RunInTransaction(transaction => queue.AddRequest(fields, RequestSigning.Sign(Key, fields), transaction));
            var row = await bot.GetRequest(id);

            Assert.NotNull(row);
            Assert.Equal(fields, row.Fields());
            Assert.True(RequestSigning.Verify(Key, row.Fields(), row.signature));
            Assert.Equal(new DateTime(2026, 11, 20, 15, 4, 5, 123), row.created_at);
            Assert.Equal(DateTimeKind.Utc, row.created_at.Kind);
            Assert.Equal(DateTimeKind.Utc, row.expires_at.Kind);
            Assert.Null(row.state);

            // The dashboard reads the same row back
            var read = await queue.GetRequestByKey(fields.RequestKey);
            Assert.NotNull(read);
            Assert.True(RequestSigning.Verify(Key, read.Fields(), read.signature));
            Assert.Equal(id, (await queue.GetRequest(id))?.id);
        }

        // The waiting requests and this run's own unfinished ones, oldest first, and only the restarts when asked
        [Fact]
        public async Task OpenIdsAreWaitingRowsAndThisRunsOwn()
        {
            var db = await StartAsync();
            var requests = db.Requests();

            var waiting = await AddAsync(db, Request());
            var restart = await AddAsync(db, Request(RequestKinds.BotRestart));
            var ours = await TakenAsync(db, Run, Noon.AddSeconds(1));
            var theirs = await TakenAsync(db, OtherRun, Noon.AddSeconds(1));
            var decided = await AddAsync(db, Request());
            Assert.True(await requests.Decide(decided, Run, RequestStates.Refused, "Refused", Noon.AddSeconds(1)));
            var finished = await TakenAsync(db, Run, Noon.AddSeconds(1));
            Assert.True(await requests.Finish(finished, Run, RequestStates.Done, "Done", null, Noon.AddSeconds(2)));
            var waitingLater = await AddAsync(db, Request(RequestKinds.StatusSet));
            var restartLater = await AddAsync(db, Request(RequestKinds.BotRestart));

            Assert.Equal([waiting, restart, ours, waitingLater, restartLater], await requests.ListOpenIds(Run, 20, restartOnly: false));
            Assert.Equal([waiting, restart], await requests.ListOpenIds(Run, 2, restartOnly: false));
            Assert.Equal([restart, restartLater], await requests.ListOpenIds(Run, 20, restartOnly: true));
            Assert.Equal([waiting, restart, theirs, waitingLater, restartLater], await requests.ListOpenIds(OtherRun, 20, restartOnly: false));
        }

        // Every caller tries to take the same request at once, as two bot processes on one database would. Exactly one
        // matches, and nothing takes it after.
        [Fact]
        public async Task OnlyOneClaimWins()
        {
            var db = await StartAsync();
            var id = await AddAsync(db, Request());

            var claims = await Task.WhenAll(Enumerable.Range(1, 8).Select(run => db.Requests().Claim(id, (ulong)run, Noon.AddSeconds(1))));

            Assert.Single(claims, claimed => claimed);
            var row = await RowAsync(id);
            Assert.Equal(RequestStates.Running, row.state);
            Assert.Equal((ulong)(Array.IndexOf(claims, true) + 1), row.run_id);
            Assert.Equal(Noon.AddSeconds(1), row.claimed_at);

            Assert.False(await db.Requests().Claim(id, Run, Noon.AddSeconds(2)));
            Assert.Equal(row.run_id, (await RowAsync(id)).run_id);
        }

        // By the bot's clock, so a request the bot sees only after its expiry is never acted on
        [Fact]
        public async Task ClaimRefusesAnExpiredRow()
        {
            var db = await StartAsync();
            var requests = db.Requests();
            var fields = Request();
            var id = await AddAsync(db, fields);

            Assert.False(await requests.Claim(id, Run, fields.ExpiresAt));
            Assert.False(await requests.Claim(id, Run, fields.ExpiresAt.AddMinutes(1)));
            Assert.Null((await RowAsync(id)).state);

            Assert.True(await requests.Claim(id, Run, fields.ExpiresAt.AddMilliseconds(-1)));
            Assert.Equal(RequestStates.Running, (await RowAsync(id)).state);
        }

        // Refused or expired only while a request waits: a decision never undoes a claim, and a decided request is never
        // taken or decided again
        [Fact]
        public async Task DecideNeverOverwritesAClaim()
        {
            var db = await StartAsync();
            var requests = db.Requests();
            var taken = await TakenAsync(db, Run, Noon.AddSeconds(1));
            var waiting = await AddAsync(db, Request());

            Assert.False(await requests.Decide(taken, OtherRun, RequestStates.Expired, "Expired", Noon.AddMinutes(3)));
            var row = await RowAsync(taken);
            Assert.Equal((RequestStates.Running, (ulong?)Run, (DateTime?)Noon.AddSeconds(1), (DateTime?)null, (string?)null),
                (row.state, row.run_id, row.claimed_at, row.ended_at, row.result));

            Assert.True(await requests.Decide(waiting, OtherRun, RequestStates.Refused, "Refused", Noon.AddSeconds(2)));
            Assert.False(await requests.Decide(waiting, Run, RequestStates.Expired, "Expired", Noon.AddSeconds(3)));
            Assert.False(await requests.Claim(waiting, Run, Noon.AddSeconds(3)));
            row = await RowAsync(waiting);
            Assert.Equal((RequestStates.Refused, (ulong?)OtherRun, (DateTime?)null, (DateTime?)Noon.AddSeconds(2), "Refused"),
                (row.state, row.run_id, row.claimed_at, row.ended_at, row.result));
        }

        // Only the run that took a request writes how it ended, and its later write wins: over its own earlier one, which a
        // restart that couldn't be recorded corrects, and over a sweep that marked it unknown
        [Fact]
        public async Task FinishWritesOnlyTheClaimingRunAndALaterWriteWins()
        {
            var db = await StartAsync();
            var requests = db.Requests();
            var id = await TakenAsync(db, Run, Noon.AddSeconds(1));

            Assert.False(await requests.Finish(id, OtherRun, RequestStates.Done, "Done", null, Noon.AddSeconds(2)));
            Assert.Null((await RowAsync(id)).ended_at);

            Assert.True(await requests.Finish(id, Run, RequestStates.Done, "The bot is restarting.", null, Noon.AddSeconds(2)));
            Assert.True(await requests.Finish(id, Run, RequestStates.Failed, "The bot couldn't save the restart first.", null, Noon.AddSeconds(3)));
            var row = await RowAsync(id);
            Assert.Equal((RequestStates.Failed, "The bot couldn't save the restart first.", (DateTime?)Noon.AddSeconds(3)), (row.state, row.result, row.ended_at));

            // A run's late result, after the next run's sweep, wins, with its number
            var late = await TakenAsync(db, OtherRun, Noon.AddSeconds(4), RequestKinds.SteamLookup);
            Assert.Single(await requests.SweepOtherRuns(Run, Noon.AddMinutes(1), "The bot stopped while this ran.", Noon.AddMinutes(1)));
            Assert.Equal(RequestStates.Unknown, (await RowAsync(late)).state);
            Assert.True(await requests.Finish(late, OtherRun, RequestStates.Done, "That link is account [U:1:12345].", 12345, Noon.AddMinutes(2)));
            row = await RowAsync(late);
            Assert.Equal((RequestStates.Done, (ulong?)12345, (DateTime?)Noon.AddMinutes(2)), (row.state, row.result_number, row.ended_at));

            // A request that was decided rather than taken has no end to write, even by the run that decided it
            var decided = await AddAsync(db, Request());
            Assert.True(await requests.Decide(decided, Run, RequestStates.Refused, "Refused", Noon.AddSeconds(5)));
            Assert.False(await requests.Finish(decided, Run, RequestStates.Done, "Done", null, Noon.AddSeconds(6)));
            Assert.Equal(RequestStates.Refused, (await RowAsync(decided)).state);

            // A result past its column is cut, by code points
            Assert.True(await requests.Finish(id, Run, RequestStates.Failed, new string('r', 299) + "\U0001F600" + "rest", null, Noon.AddSeconds(7)));
            Assert.Equal(new string('r', 299) + "\U0001F600", (await RowAsync(id)).result);
        }

        // The sweep marks unknown only the requests other runs took before this run started. A claim this run made, or one
        // another process made after it started, stays running, and nothing that ended changes.
        [Fact]
        public async Task SweepTouchesOnlyOtherRunsClaimsFromBeforeThisRunStarted()
        {
            var db = await StartAsync();
            var requests = db.Requests();
            var startedAt = Noon.AddMinutes(10);

            var crashed = await TakenAsync(db, OtherRun, Noon.AddSeconds(1), RequestKinds.GuildLeave);
            var atTheStart = await TakenAsync(db, OtherRun, startedAt);
            var afterTheStart = await TakenAsync(db, OtherRun, startedAt.AddSeconds(1));
            var ours = await TakenAsync(db, Run, Noon.AddSeconds(2));
            var ended = await TakenAsync(db, OtherRun, Noon.AddSeconds(3));
            Assert.True(await requests.Finish(ended, OtherRun, RequestStates.Done, "Done", null, Noon.AddSeconds(4)));
            var waiting = await AddAsync(db, Request(at: startedAt));
            var decided = await AddAsync(db, Request());
            Assert.True(await requests.Decide(decided, OtherRun, RequestStates.Expired, "Expired", Noon.AddMinutes(5)));

            var swept = await requests.SweepOtherRuns(Run, startedAt, "The bot stopped while this ran, so it may or may not have happened.", startedAt.AddSeconds(5));

            Assert.Equal([(crashed, RequestKinds.GuildLeave)], swept.Select(row => (row.id, row.kind)));
            var row = await RowAsync(crashed);
            Assert.Equal((RequestStates.Unknown, (ulong?)OtherRun, (DateTime?)startedAt.AddSeconds(5), "The bot stopped while this ran, so it may or may not have happened."),
                (row.state, row.run_id, row.ended_at, row.result));

            Assert.Equal(RequestStates.Running, (await RowAsync(atTheStart)).state);
            Assert.Equal(RequestStates.Running, (await RowAsync(afterTheStart)).state);
            Assert.Equal(RequestStates.Running, (await RowAsync(ours)).state);
            Assert.Equal(RequestStates.Done, (await RowAsync(ended)).state);
            Assert.Null((await RowAsync(waiting)).state);
            Assert.Equal(RequestStates.Expired, (await RowAsync(decided)).state);

            // Once swept, there's nothing left to sweep
            Assert.Empty(await requests.SweepOtherRuns(Run, startedAt, "The bot stopped.", startedAt.AddSeconds(6)));
        }

        // The live server's clock is US Eastern, and every time here is the bot's, to the millisecond: one far from this
        // server's clock is what each row holds
        [Fact]
        public async Task TimesComeFromTheBotNotTheServer()
        {
            var db = await StartAsync();
            var requests = db.Requests();
            var at = new DateTime(2031, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc);

            var taken = await AddAsync(db, Request(at: at));
            Assert.True(await requests.Claim(taken, Run, at.AddMilliseconds(1)));
            Assert.True(await requests.Finish(taken, Run, RequestStates.Done, "Done", null, at.AddMilliseconds(2)));
            var decided = await AddAsync(db, Request(at: at));
            Assert.True(await requests.Decide(decided, Run, RequestStates.Expired, "Expired", at.AddMilliseconds(3)));
            await requests.SavePause(true, "Testing", at.AddMilliseconds(4), OwnerId, taken, at.AddMilliseconds(5));
            await requests.SaveStatus("Testing", at.AddMilliseconds(6), OwnerId, taken, at.AddMilliseconds(7));

            var row = await RowAsync(taken);
            Assert.Equal(new[] { at, at.AddMinutes(2), at.AddMilliseconds(1), at.AddMilliseconds(2) },
                new[] { row.created_at, row.expires_at, row.claimed_at!.Value, row.ended_at!.Value });
            Assert.Equal(at.AddMilliseconds(3), (await RowAsync(decided)).ended_at);

            var settings = await requests.GetSettings();
            Assert.NotNull(settings);
            var times = new[] { settings.pause_changed_at!.Value, settings.status_changed_at!.Value, settings.updated_at };
            Assert.Equal(new[] { at.AddMilliseconds(4), at.AddMilliseconds(6), at.AddMilliseconds(7) }, times);
            Assert.All(times, time => Assert.Equal(DateTimeKind.Utc, time.Kind));
        }

        // A limit counts the requests any run took of the kinds counted together, since the window's start, for one server
        // when it's per server. A request that was refused or expired was never taken, so it doesn't count. One that was
        // taken counts however it ended: done, as a restart's own row is before the stop, failed, so a broken channel can't
        // be tried again and again, or unknown after a later run's sweep.
        [Fact]
        public async Task CountsGoByKindGroupServerAndWindowAcrossRuns()
        {
            var db = await StartAsync();
            var requests = db.Requests();
            string[] pausing = [RequestKinds.PostsPause, RequestKinds.PostsResume];

            var paused = await TakenAsync(db, Run, Noon.AddSeconds(1), RequestKinds.PostsPause);
            var resumed = await TakenAsync(db, OtherRun, Noon.AddSeconds(2), RequestKinds.PostsResume);
            var earlier = await TakenAsync(db, OtherRun, Noon.AddMilliseconds(-1), RequestKinds.PostsPause);
            var refused = await AddAsync(db, Request(RequestKinds.PostsPause, Noon.AddSeconds(3), argument: "Testing"));
            Assert.True(await requests.Decide(refused, Run, RequestStates.Refused, "Refused", Noon.AddSeconds(3)));
            var expired = await AddAsync(db, Request(RequestKinds.PostsResume, Noon.AddSeconds(3)));
            Assert.True(await requests.Decide(expired, Run, RequestStates.Expired, "Expired", Noon.AddMinutes(11)));
            var lookup = await TakenAsync(db, Run, Noon.AddSeconds(4), RequestKinds.SteamLookup);
            var running = await TakenAsync(db, Run, Noon.AddSeconds(5), RequestKinds.GuildTestMessage, GuildId);
            await TakenAsync(db, OtherRun, Noon.AddSeconds(6), RequestKinds.GuildTestMessage, OtherGuildId);

            // Ended every way a taken request ends: this run's pause done and its lookup failed, and the other run's two
            // claims from before this run started marked unknown by its sweep
            Assert.True(await requests.Finish(paused, Run, RequestStates.Done, "Done", null, Noon.AddSeconds(7)));
            Assert.True(await requests.Finish(lookup, Run, RequestStates.Failed, "Failed", null, Noon.AddSeconds(7)));
            Assert.Equal([resumed, earlier], (await requests.SweepOtherRuns(Run, Noon.AddSeconds(3), "Unknown", Noon.AddSeconds(8))).Select(row => row.id));

            var states = new List<string?>();
            foreach (var id in new[] { paused, resumed, earlier, lookup, running })
                states.Add((await RowAsync(id)).state);

            Assert.Equal([RequestStates.Done, RequestStates.Unknown, RequestStates.Unknown, RequestStates.Failed, RequestStates.Running], states);

            async Task<(long, DateTime?)> CountAsync(IReadOnlyCollection<string> kinds, DateTime since, ulong? guildId = null)
            {
                var count = await requests.CountClaims(kinds, since, guildId);
                return (count.count, count.oldest);
            }

            Assert.Equal(Counted(2, Noon.AddSeconds(1)), await CountAsync(pausing, Noon));
            Assert.Equal(Counted(1, Noon.AddSeconds(1)), await CountAsync([RequestKinds.PostsPause], Noon));
            Assert.Equal(Counted(1, Noon.AddSeconds(2)), await CountAsync([RequestKinds.PostsResume], Noon));
            Assert.Equal(Counted(3, Noon.AddMilliseconds(-1)), await CountAsync(pausing, Noon.AddMilliseconds(-1)));
            Assert.Equal(Counted(0, null), await CountAsync([RequestKinds.SteamLookup], Noon.AddSeconds(5)));
            Assert.Equal(Counted(1, Noon.AddSeconds(4)), await CountAsync([RequestKinds.SteamLookup], Noon.AddSeconds(4)));
            Assert.Equal(Counted(1, Noon.AddSeconds(5)), await CountAsync([RequestKinds.GuildTestMessage], Noon, GuildId));
            Assert.Equal(Counted(1, Noon.AddSeconds(6)), await CountAsync([RequestKinds.GuildTestMessage], Noon, OtherGuildId));
            Assert.Equal(Counted(2, Noon.AddSeconds(5)), await CountAsync([RequestKinds.GuildTestMessage], Noon));
            Assert.Equal(DateTimeKind.Utc, (await requests.CountClaims(pausing, Noon)).oldest!.Value.Kind);
        }

        // Pausing and setting the status text each change only their own columns, whichever makes the row
        [Fact]
        public async Task SettingsUpsertsLeaveEachOtherAlone()
        {
            var db = await StartAsync();
            var requests = db.Requests();

            await requests.SaveStatus("Back soon", Noon, OwnerId, 1, Noon);
            var settings = await requests.GetSettings();
            Assert.NotNull(settings);
            Assert.Equal((false, (string?)null, (ulong?)null, "Back soon", (DateTime?)Noon, (ulong?)OwnerId, (ulong?)1, Noon),
                (settings.posts_paused, settings.pause_reason, settings.pause_request_id, settings.status_text, settings.status_changed_at, settings.status_changed_by,
                    settings.status_request_id, settings.updated_at));

            await requests.SavePause(true, "Live check", Noon.AddSeconds(1), OwnerId + 1, 2, Noon.AddSeconds(1));
            settings = await requests.GetSettings();
            Assert.NotNull(settings);
            Assert.Equal((true, "Live check", (DateTime?)Noon.AddSeconds(1), (ulong?)(OwnerId + 1), (ulong?)2, "Back soon", (ulong?)1, Noon.AddSeconds(1)),
                (settings.posts_paused, settings.pause_reason, settings.pause_changed_at, settings.pause_changed_by, settings.pause_request_id, settings.status_text,
                    settings.status_request_id, settings.updated_at));

            await requests.SaveStatus(null, Noon.AddSeconds(2), OwnerId, 3, Noon.AddSeconds(2));
            await requests.SavePause(false, null, Noon.AddSeconds(3), OwnerId, 4, Noon.AddSeconds(3));
            settings = await db.RequestQueue().GetSettings();
            Assert.NotNull(settings);
            Assert.Equal((false, (string?)null, (DateTime?)Noon.AddSeconds(3), (ulong?)4, (string?)null, (DateTime?)Noon.AddSeconds(2), (ulong?)3, Noon.AddSeconds(3)),
                (settings.posts_paused, settings.pause_reason, settings.pause_changed_at, settings.pause_request_id, settings.status_text, settings.status_changed_at,
                    settings.status_request_id, settings.updated_at));

            // A pause that makes the row leaves the status at its default
            await db.ClearAsync(MariaDbFixture.SettingsTable);
            await requests.SavePause(true, "Live check", Noon, OwnerId, 5, Noon);
            settings = await requests.GetSettings();
            Assert.NotNull(settings);
            Assert.Equal((true, (string?)null, (DateTime?)null, (ulong?)null), (settings.posts_paused, settings.status_text, settings.status_changed_at, settings.status_request_id));
            Assert.Equal(1, (await fixture.RootQueryAsync<long>($"SELECT COUNT(*) FROM {MariaDbFixture.SettingsTable}")).Single());
        }

        // No row is posts not paused and the default status, and an empty table gives nothing to act on
        [Fact]
        public async Task NoRowMeansDefaults()
        {
            var db = await StartAsync();
            var requests = db.Requests();
            var queue = db.RequestQueue();

            Assert.Null(await requests.GetSettings());
            Assert.Null(await queue.GetSettings());
            Assert.Null(await requests.GetRequest(1));
            Assert.Null(await queue.GetRequest(1));
            Assert.Null(await queue.GetRequestByKey("00112233445566778899aabbccddeeff"));
            Assert.Empty(await requests.ListOpenIds(Run, 20, restartOnly: false));
            Assert.Empty(await requests.ListUntoldRestarts(Run, Noon));
            Assert.Empty(await requests.SweepOtherRuns(Run, Noon, "Unknown", Noon));
            var count = await requests.CountClaims([RequestKinds.BotRestart], Noon);
            Assert.Equal(Counted(0, null), (count.count, count.oldest));
        }

        // A copy of a request, or a form sent twice, has the same key, which the table holds to one row: the second insert
        // fails with MariaDB's 1062, and the audit row it travels with goes with it. The table's collation ignores case, so a
        // copy with the key in capitals is the same key to it, and the dashboard's code refuses one before any SQL.
        [Fact]
        public async Task ACopiedRequestKeyIsRefused()
        {
            var db = await StartAsync();
            var queue = db.RequestQueue();
            var fields = Request(key: "00112233445566778899aabbccddeeff");
            var signature = RequestSigning.Sign(Key, fields);
            await AddAsync(db, fields);

            var copied = await Assert.ThrowsAsync<MySqlException>(() => queue.RunInTransaction(async transaction =>
            {
                var added = await queue.AddRequest(fields, signature, transaction);
                await db.Audit().AddAudit(new AuditEntry
                {
                    at = Noon,
                    actor_id = OwnerId,
                    actor_name = "Test Owner",
                    actor_role = AuditRole.Owner,
                    action = RequestKinds.CachesClear,
                    outcome = AuditOutcome.Ok,
                    target_type = "request"
                }, transaction);
                return added;
            }));
            Assert.Equal(1062, copied.Number);
            Assert.Equal(0, (await fixture.RootQueryAsync<long>($"SELECT COUNT(*) FROM {MariaDbFixture.AuditTable}")).Single());

            var capitals = fields with { RequestKey = fields.RequestKey.ToUpperInvariant() };
            await Assert.ThrowsAnyAsync<ArgumentException>(() => AddAsync(db, capitals));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => queue.GetRequestByKey(capitals.RequestKey));

            var byHand = await Assert.ThrowsAsync<MySqlException>(() => fixture.RootExecuteAsync(
                $@"INSERT INTO {MariaDbFixture.RequestTable} (request_key, kind, actor_id, actor_name, actor_role, created_at, expires_at, signature)
                   VALUES (@key, 'caches.clear', 1, 'x', 'owner', @at, @at, @signature)", new { key = capitals.RequestKey, at = Noon, signature }));
            Assert.Equal(1062, byHand.Number);

            Assert.Single(await fixture.RootQueryAsync<ulong>($"SELECT id FROM {MariaDbFixture.RequestTable}"));
        }

        // MariaDB's default mode lets the dashboard's own login write a time of 0000-00-00, which MySql.Data can't read. The
        // list of IDs still holds the row, reading it on its own fails without stopping the rows behind it, and it can be
        // decided by its ID, so it never stalls the queue.
        [Fact]
        public async Task AZeroDateRowIsDecidedNotStuck()
        {
            var db = await StartAsync();
            var requests = db.Requests();
            var before = await AddAsync(db, Request());
            ulong zero = 0;

            await DashboardLogin.AsDashboardAsync(db, async password =>
            {
                await using var connection = new MySqlConnection(DashboardLogin.ConnectionAs(db, password, MariaDbFixture.BotDatabase));
                await connection.OpenAsync(Token);
                await connection.ExecuteAsync(new CommandDefinition(
                    $@"INSERT INTO {MariaDbFixture.RequestTable} (request_key, kind, actor_id, actor_name, actor_role, created_at, expires_at, signature)
                       VALUES (REPEAT('0', 32), 'posts.pause', 1, 'x', 'owner', '0000-00-00 00:00:00.000', @expires, REPEAT('s', 32))",
                    new { expires = Noon.AddMinutes(2) }, cancellationToken: Token));
                zero = await connection.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT LAST_INSERT_ID()", cancellationToken: Token));
            });

            var after = await AddAsync(db, Request());

            Assert.NotEqual(0UL, zero);
            Assert.Equal([before, zero, after], await requests.ListOpenIds(Run, 20, restartOnly: false));

            // Dapper's DataException around MySql.Data's conversion error: neither is a MySqlException or a TimeoutException,
            // the two the bot takes for trouble with the database and waits out, so this one is the row's, and the bot decides
            // the row by its ID
            var unreadable = await Assert.ThrowsAsync<DataException>(() => requests.GetRequest(zero));
            Assert.IsType<MySqlConversionException>(unreadable.InnerException);
            Assert.NotNull(await requests.GetRequest(after));

            Assert.True(await requests.Decide(zero, Run, RequestStates.Refused, $"The bot couldn't read this request ({unreadable.GetType().Name}), so it didn't act on it.",
                Noon.AddSeconds(1)));
            Assert.Equal([before, after], await requests.ListOpenIds(Run, 20, restartOnly: false));
            Assert.Equal(RequestStates.Refused, (await fixture.RootQueryAsync<string>($"SELECT state FROM {MariaDbFixture.RequestTable} WHERE id = @zero", new { zero })).Single());
        }

        // Written once: a duplicate direct message, or a second write of the first, can't change what the first said
        [Fact]
        public async Task DmStateIsWrittenOnce()
        {
            var db = await StartAsync();
            var requests = db.Requests();
            var id = await TakenAsync(db, Run, Noon.AddSeconds(1), RequestKinds.BotRestart);

            Assert.True(await requests.SetDm(id, RequestDmStates.Failed));
            Assert.False(await requests.SetDm(id, RequestDmStates.Sent));
            Assert.Equal(RequestDmStates.Failed, (await RowAsync(id)).dm_state);
            Assert.False(await requests.SetDm(id + 1, RequestDmStates.Sent));
        }

        // The restarts an earlier run took and finished, in the time given, whose direct message never went
        [Fact]
        public async Task UntoldRestartsAreEarlierRunsDoneRestartsWithoutADm()
        {
            var db = await StartAsync();
            var requests = db.Requests();

            async Task<ulong> RestartAsync(ulong runId, DateTime claimedAt, string state = RequestStates.Done, string kind = RequestKinds.BotRestart)
            {
                var id = await TakenAsync(db, runId, claimedAt, kind);
                Assert.True(await requests.Finish(id, runId, state, "The bot is restarting.", null, claimedAt.AddSeconds(1)));
                return id;
            }

            var untold = await RestartAsync(OtherRun, Noon.AddMinutes(-30));
            var told = await RestartAsync(OtherRun, Noon.AddMinutes(-20));
            Assert.True(await requests.SetDm(told, RequestDmStates.Sent));
            await RestartAsync(Run, Noon.AddMinutes(-10));
            await RestartAsync(OtherRun, Noon.AddHours(-2));
            await RestartAsync(OtherRun, Noon.AddMinutes(-5), RequestStates.Failed);
            await RestartAsync(OtherRun, Noon.AddMinutes(-4), kind: RequestKinds.CachesClear);

            var rows = await requests.ListUntoldRestarts(Run, Noon.AddHours(-1));

            Assert.Equal([untold], rows.Select(row => row.id));
            Assert.Equal(("Test Owner", OwnerId), (rows[0].actor_name, rows[0].actor_id));
        }
    }
}
