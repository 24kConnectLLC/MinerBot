using DataAccessLibrary.Status;
using DataAccessLibrary.Tests.Database;
using MinerBot.Shared.Requests;

namespace DataAccessLibrary.Tests
{
    // The daily clean-up's rules, each with rows just either side of its cutoff. The cutoffs are what the job gives, so a
    // row exactly at one stays.
    [Collection("Database")]
    public class RetentionDataTests(MariaDbFixture fixture)
    {
        static readonly DateTime Cutoff = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        static readonly DateTime Today = new(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);
        static readonly DateTime KeepFrom = Today.AddDays(-90);
        const int Chunk = RetentionData.ChunkLimit;

        async Task<MariaDbFixture> StartAsync()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.AuditTable, MariaDbFixture.SessionTable, MariaDbFixture.EventTable, MariaDbFixture.JobRunTable,
                MariaDbFixture.SampleTable, MariaDbFixture.DailyTable, MariaDbFixture.GuildEventTable, MariaDbFixture.BotGuildTable, MariaDbFixture.GuildDailyTable,
                MariaDbFixture.RequestTable, MariaDbFixture.SettingsTable);
            return db;
        }

        Task AuditAsync(DateTime at, string? ip = "203.0.113.7") => fixture.RootExecuteAsync(
            $"INSERT INTO {MariaDbFixture.AuditTable} (at, actor_role, action, outcome, ip) VALUES (@at, 'admin', 'armor.approve', 'ok', @ip)", new { at, ip });

        // MariaDB's sequence table makes many rows in one statement, one second apart from the start given
        Task ManyAuditRowsAsync(int count, DateTime from) => fixture.RootExecuteAsync(
            $@"INSERT INTO {MariaDbFixture.AuditTable} (at, actor_role, action, outcome, ip)
               SELECT TIMESTAMPADD(SECOND, seq, @from), 'admin', 'armor.approve', 'ok', '203.0.113.7' FROM {MariaDbFixture.BotDatabase}.seq_1_to_{count}", new { from });

        async Task<List<DateTime>> TimesAsync(string column, string table) =>
            await fixture.RootQueryAsync<DateTime>($"SELECT {column} FROM {table} ORDER BY {column}");

        // Two years, by the cutoff the job gives
        [Fact]
        public async Task AuditRowsGoBeforeTheCutoff()
        {
            var db = await StartAsync();
            await AuditAsync(Cutoff.AddMilliseconds(-1));
            await AuditAsync(Cutoff);
            await AuditAsync(Cutoff.AddMilliseconds(1));

            Assert.Equal(1, await db.Retention().DeleteAuditRows(Cutoff, Chunk));
            Assert.Equal([Cutoff, Cutoff.AddMilliseconds(1)], await TimesAsync("at", MariaDbFixture.AuditTable));
        }

        // The rows stay, without the visitor's address. A row already without one isn't counted, or a chunk of them would
        // never come back short.
        [Fact]
        public async Task AddressesAreClearedBeforeTheCutoff()
        {
            var db = await StartAsync();
            await AuditAsync(Cutoff.AddMilliseconds(-2), ip: null);
            await AuditAsync(Cutoff.AddMilliseconds(-1));
            await AuditAsync(Cutoff);

            Assert.Equal(1, await db.Retention().ClearAuditAddresses(Cutoff, Chunk));
            Assert.Equal(0, await db.Retention().ClearAuditAddresses(Cutoff, Chunk));

            var rows = await fixture.RootQueryAsync<(DateTime, string?)>($"SELECT at, ip FROM {MariaDbFixture.AuditTable} ORDER BY at");
            Assert.Equal([(Cutoff.AddMilliseconds(-2), (string?)null), (Cutoff.AddMilliseconds(-1), null), (Cutoff, "203.0.113.7")], rows);
        }

        // Oldest first, a chunk at a time, until one comes back short
        [Fact]
        public async Task MoreRowsThanOneChunkGoInChunks()
        {
            var db = await StartAsync();
            var retention = db.Retention();
            await ManyAuditRowsAsync(6000, Cutoff.AddDays(-1));
            await AuditAsync(Cutoff.AddHours(1));

            // The oldest go first
            Assert.Equal(Chunk, await retention.ClearAuditAddresses(Cutoff, Chunk));
            Assert.Equal(Cutoff.AddDays(-1).AddSeconds(5001),
                (await fixture.RootQueryAsync<DateTime>($"SELECT MIN(at) FROM {MariaDbFixture.AuditTable} WHERE ip IS NOT NULL")).Single());
            Assert.Equal(1000, await retention.ClearAuditAddresses(Cutoff, Chunk));
            Assert.Equal(0, await retention.ClearAuditAddresses(Cutoff, Chunk));

            Assert.Equal(Chunk, await retention.DeleteAuditRows(Cutoff, Chunk));
            Assert.Equal(Cutoff.AddDays(-1).AddSeconds(5001), (await TimesAsync("at", MariaDbFixture.AuditTable))[0]);
            Assert.Equal(1000, await retention.DeleteAuditRows(Cutoff, Chunk));
            Assert.Equal([Cutoff.AddHours(1)], await TimesAsync("at", MariaDbFixture.AuditTable));
            Assert.Equal("203.0.113.7", (await fixture.RootQueryAsync<string?>($"SELECT ip FROM {MariaDbFixture.AuditTable}")).Single());
        }

        [Fact]
        public async Task EventsGoBeforeTheCutoff()
        {
            var db = await StartAsync();

            foreach (var (seq, at) in new[] { (1, Cutoff.AddMilliseconds(-1)), (2, Cutoff), (3, Cutoff.AddMilliseconds(1)) })
                await fixture.RootExecuteAsync($"INSERT INTO {MariaDbFixture.EventTable} (run_id, seq, at, kind, level) VALUES (1, @seq, @at, 'bot.started', 'info')", new { seq, at });

            Assert.Equal(1, await db.Retention().DeleteEvents(Cutoff, Chunk));
            Assert.Equal([Cutoff, Cutoff.AddMilliseconds(1)], await TimesAsync("at", MariaDbFixture.EventTable));
        }

        // The bot's joins and leaves of servers. The servers' rows and their days' member counts stay, however old.
        [Fact]
        public async Task GuildEventsGoBeforeTheCutoff()
        {
            var db = await StartAsync();

            foreach (var (seq, at) in new[] { (1, Cutoff.AddMilliseconds(-1)), (2, Cutoff), (3, Cutoff.AddMilliseconds(1)) })
                await fixture.RootExecuteAsync($"INSERT INTO {MariaDbFixture.GuildEventTable} (run_id, seq, guild_id, at, kind) VALUES (1, @seq, 2, @at, 'joined')", new { seq, at });

            await fixture.RootExecuteAsync($@"INSERT INTO {MariaDbFixture.BotGuildTable} (guild_id, name, first_seen_at, left_at, available, bot_administrator, bot_permissions, updated_at)
                                              VALUES (2, 'Test Server', @old, @old, 1, 0, 0, @old)", new { old = Cutoff.AddYears(-3) });
            await fixture.RootExecuteAsync($"INSERT INTO {MariaDbFixture.GuildDailyTable} (day, guild_id, member_count) VALUES (@day, 2, 12)", new { day = Cutoff.Date.AddYears(-3) });

            Assert.Equal(1, await db.Retention().DeleteGuildEvents(Cutoff, Chunk));
            Assert.Equal([Cutoff, Cutoff.AddMilliseconds(1)], await TimesAsync("at", MariaDbFixture.GuildEventTable));
            Assert.Equal(1, (await fixture.RootQueryAsync<long>($"SELECT COUNT(*) FROM {MariaDbFixture.BotGuildTable}")).Single());
            Assert.Equal(1, (await fixture.RootQueryAsync<long>($"SELECT COUNT(*) FROM {MariaDbFixture.GuildDailyTable}")).Single());
        }

        // By when each run started, so a run that was cut off and never ended goes too
        [Fact]
        public async Task JobRunsGoBeforeTheCutoff()
        {
            var db = await StartAsync();

            foreach (var (seq, at) in new[] { (1, Cutoff.AddMilliseconds(-1)), (2, Cutoff), (3, Cutoff.AddMilliseconds(1)) })
                await fixture.RootExecuteAsync($"INSERT INTO {MariaDbFixture.JobRunTable} (run_id, seq, job, started_at, result) VALUES (1, @seq, 'ArmorJob.AddColorArrays', @at, 'cut_off')", new { seq, at });

            Assert.Equal(1, await db.Retention().DeleteJobRuns(Cutoff, Chunk));
            Assert.Equal([Cutoff, Cutoff.AddMilliseconds(1)], await TimesAsync("started_at", MariaDbFixture.JobRunTable));
        }

        // The dashboard's requests to the bot, by when the dashboard made them, whatever became of them. One made at
        // 0000-00-00, which MariaDB's default mode lets the dashboard's login write and MySql.Data can't read, goes first.
        // The bot's settings stay, however old.
        [Fact]
        public async Task RequestsGoAfter90Days()
        {
            var db = await StartAsync();

            foreach (var (key, at, state) in new[] { ("a", KeepFrom.AddMilliseconds(-1), RequestStates.Done), ("b", KeepFrom, (string?)null), ("c", KeepFrom.AddMilliseconds(1), RequestStates.Refused) })
                await fixture.RootExecuteAsync($@"INSERT INTO {MariaDbFixture.RequestTable} (request_key, kind, actor_id, actor_name, actor_role, created_at, expires_at,
                                                  signature, state)
                                                  VALUES (REPEAT(@key, 32), 'posts.pause', 1, 'Test Owner', 'owner', @at, @at, REPEAT('s', 32), @state)", new { key, at, state });

            await fixture.RootExecuteAsync($@"INSERT INTO {MariaDbFixture.RequestTable} (request_key, kind, actor_id, actor_name, actor_role, created_at, expires_at,
                                              signature)
                                              VALUES (REPEAT('d', 32), 'posts.pause', 1, 'Test Owner', 'owner', '0000-00-00 00:00:00.000', @at, REPEAT('s', 32))",
                new { at = KeepFrom.AddDays(1) });
            await db.Requests().SavePause(true, "Testing", KeepFrom.AddYears(-1), 1, 1, KeepFrom.AddYears(-1));

            Assert.Equal(2, await db.Retention().DeleteRequests(KeepFrom, Chunk));
            Assert.Equal([KeepFrom, KeepFrom.AddMilliseconds(1)], await TimesAsync("created_at", MariaDbFixture.RequestTable));
            Assert.Equal(0, await db.Retention().DeleteRequests(KeepFrom, Chunk));
            Assert.Equal(1, (await fixture.RootQueryAsync<long>($"SELECT COUNT(*) FROM {MariaDbFixture.SettingsTable}")).Single());
        }

        // A request dated years ahead, which the dashboard's login can write without the key and the bot refuses, goes by
        // when the bot ended it, which only the bot's clock writes, so a flood of such rows doesn't stay for good. One
        // that hasn't ended stays.
        [Fact]
        public async Task RequestsDatedAheadGoByWhenTheBotEndedThem()
        {
            var db = await StartAsync();
            var ahead = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var requests = new (string Key, string? State, DateTime? Ended)[]
            {
                ("a", RequestStates.Refused, KeepFrom.AddMilliseconds(-1)),
                ("b", RequestStates.Refused, KeepFrom),
                ("c", null, null)
            };

            foreach (var (key, state, ended) in requests)
                await fixture.RootExecuteAsync($@"INSERT INTO {MariaDbFixture.RequestTable} (request_key, kind, actor_id, actor_name, actor_role, created_at, expires_at,
                                                  signature, state, ended_at)
                                                  VALUES (REPEAT(@key, 32), 'posts.pause', 1, 'Test Owner', 'owner', @ahead, @expires, REPEAT('s', 32), @state, @ended)",
                    new { key, ahead, expires = ahead.AddMinutes(1), state, ended });

            Assert.Equal(1, await db.Retention().DeleteRequests(KeepFrom, Chunk));
            Assert.Equal([new string('b', 32), new string('c', 32)],
                await fixture.RootQueryAsync<string>($"SELECT request_key FROM {MariaDbFixture.RequestTable} ORDER BY request_key"));
            Assert.Equal(0, await db.Retention().DeleteRequests(KeepFrom, Chunk));
        }

        // Unused for the time given, or ended that long ago
        [Fact]
        public async Task SessionsGoWhenTheyEndedOrWereLastUsedBeforeTheCutoff()
        {
            var db = await StartAsync();
            var sessions = new (string Key, DateTime LastSeen, DateTime Expires)[]
            {
                (new string('a', 43), Cutoff.AddDays(-2), Cutoff.AddMilliseconds(-1)),
                (new string('b', 43), Cutoff.AddMilliseconds(-1), Cutoff.AddDays(1)),
                (new string('c', 43), Cutoff, Cutoff),
                (new string('d', 43), Cutoff.AddDays(1), Cutoff.AddDays(2))
            };

            foreach (var (key, lastSeen, expires) in sessions)
                await db.Sessions().CreateSession(key, new Dashboard.Models.DashboardSession
                {
                    user_id = 1000000000000000001,
                    user_name = "Test User",
                    created_at = lastSeen.AddHours(-1),
                    last_seen_at = lastSeen,
                    expires_at = expires,
                    ticket = [1]
                });

            Assert.Equal(2, await db.Retention().DeleteSessions(Cutoff, Chunk));
            Assert.Equal([Cutoff, Cutoff.AddDays(1)], await TimesAsync("last_seen_at", MariaDbFixture.SessionTable));
        }

        // Every day from the first given up to today that has samples gets its totals, and a total written again counts
        // the samples that arrived late
        [Fact]
        public async Task DaysWithSamplesAreTotalled()
        {
            var db = await StartAsync();
            var retention = db.Retention();
            var dayOne = Today.AddDays(-2);
            var dayTwo = Today.AddDays(-1);

            // Before the first day kept, which is never totalled again, and today, which isn't over
            await SampleAsync(KeepFrom.AddMinutes(-1), true, true);
            await SampleAsync(Today, true, true);

            await SampleAsync(dayOne, true, false);
            await SampleAsync(dayOne.AddMinutes(1), true, true);
            await SampleAsync(dayOne.AddMinutes(2), false, false);
            await SampleAsync(dayTwo.AddHours(23).AddMinutes(59), true, true);

            Assert.Equal(2, await retention.TotalUptimeDays(KeepFrom, Today));
            Assert.Equal([(dayOne, 3, 2, 1), (dayTwo, 1, 1, 1)], await DailyAsync());

            // A late sample, written from the bot's memory after an outage, counts at the next run
            await SampleAsync(dayTwo, false, true);
            Assert.Equal(2, await retention.TotalUptimeDays(KeepFrom, Today));
            Assert.Equal([(dayOne, 3, 2, 1), (dayTwo, 2, 1, 2)], await DailyAsync());
        }

        // Whole days only: the minute before the first day kept goes, the first minute of it stays. A day's total stays when
        // its samples have gone.
        [Fact]
        public async Task SamplesGoInWholeDaysAndTheirTotalsStay()
        {
            var db = await StartAsync();
            var retention = db.Retention();
            var lastDayGoing = KeepFrom.AddDays(-1);

            await SampleAsync(lastDayGoing, true, true);
            await SampleAsync(KeepFrom.AddMinutes(-1), true, false);
            await SampleAsync(KeepFrom, false, false);

            // Totalled while all its samples were there, the night before they go
            Assert.Equal(1, await retention.TotalUptimeDays(lastDayGoing, KeepFrom));

            Assert.Equal(2, await retention.DeleteSamples(KeepFrom, Chunk));
            Assert.Equal([KeepFrom], await TimesAsync("minute", MariaDbFixture.SampleTable));
            Assert.Equal([(lastDayGoing, 2, 2, 1)], await DailyAsync());

            // The next night's totals start at the first day kept, so the day that went keeps its total
            Assert.Equal(1, await retention.TotalUptimeDays(KeepFrom, KeepFrom.AddDays(1)));
            Assert.Equal([(lastDayGoing, 2, 2, 1), (KeepFrom, 1, 0, 0)], await DailyAsync());
        }

        [Fact]
        public async Task MoreSamplesThanOneChunkGoInChunks()
        {
            var db = await StartAsync();
            var retention = db.Retention();

            await fixture.RootExecuteAsync(
                $@"INSERT INTO {MariaDbFixture.SampleTable} (minute, discord_up, steam_up)
                   SELECT TIMESTAMPADD(MINUTE, seq - 1, @from), 1, 0 FROM {MariaDbFixture.BotDatabase}.seq_1_to_6000", new { from = KeepFrom.AddDays(-10) });

            Assert.Equal(Chunk, await retention.DeleteSamples(KeepFrom, Chunk));
            Assert.Equal(KeepFrom.AddDays(-10).AddMinutes(Chunk), (await TimesAsync("minute", MariaDbFixture.SampleTable))[0]);
            Assert.Equal(1000, await retention.DeleteSamples(KeepFrom, Chunk));
            Assert.Equal(0, await retention.DeleteSamples(KeepFrom, Chunk));
        }

        Task SampleAsync(DateTime minute, bool discordUp, bool steamUp) => fixture.RootExecuteAsync(
            $"INSERT INTO {MariaDbFixture.SampleTable} (minute, discord_up, steam_up) VALUES (@minute, @discordUp, @steamUp)", new { minute, discordUp, steamUp });

        Task<List<(DateTime, int, int, int)>> DailyAsync() => fixture.RootQueryAsync<(DateTime, int, int, int)>(
            $"SELECT day, sampled_minutes, discord_minutes, steam_minutes FROM {MariaDbFixture.DailyTable} ORDER BY day");
    }
}
