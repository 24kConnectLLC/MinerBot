using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Blacklist;
using DataAccessLibrary.Blacklist.Models;
using DataAccessLibrary.Dashboard;
using DataAccessLibrary.Dashboard.Models;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Guilds.Models;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using MinerBot.Shared.Armor;

namespace DataAccessLibrary.Tests
{
    // A value that can't be saved is refused before any SQL, so a change it travels with in a transaction doesn't happen
    // either. No database is needed: reaching one fails the test.
    public class WriteValidationTests
    {
        readonly NoDatabase database = new();

        static readonly string SessionKey = new('k', 43);

        async Task RefusedAsync(Func<Task> call)
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(call);
            Assert.Equal(0, database.Created);
        }

        static AuditEntry Entry() => new()
        {
            at = DateTime.UtcNow,
            actor_id = 1000000000000000001,
            actor_name = "Test Admin",
            actor_role = AuditRole.Admin,
            action = "armor.approve",
            outcome = AuditOutcome.Ok,
            target_type = "armor",
            target_id = "12"
        };

        static ArmorReview Review() => new()
        {
            armor_id = 12,
            from_status = ArmorStatus.Pending,
            to_status = ArmorStatus.Approved,
            reviewed_by = 1000000000000000001,
            reviewed_at = DateTime.UtcNow
        };

        [Theory]
        [InlineData(ArmorStatus.Pending, "Aproved")]
        [InlineData(ArmorStatus.Pending, "approved")]
        [InlineData(ArmorStatus.Pending, "")]
        [InlineData("pending approval", ArmorStatus.Approved)]
        [InlineData("Waiting", ArmorStatus.Approved)]
        [InlineData(ArmorStatus.Approved, ArmorStatus.Approved)]
        public async Task StatusChangeWithAWrongWordIsRefused(string? expected, string newStatus)
        {
            await RefusedAsync(() => new ArmorSubmissionData(database).ChangeStatus(12, expected, newStatus));
        }

        [Fact]
        public async Task ListOfAnUnknownStatusIsRefused()
        {
            await RefusedAsync(() => new ArmorSubmissionData(database).ListByStatus("approved", 0, 25));
            await RefusedAsync(() => new ArmorSubmissionData(database).Search("x", "Pending", 0, 25));
            await RefusedAsync(() => new ArmorSubmissionData(database).ListByStatus(ArmorStatus.Pending, 0, 101));
        }

        [Fact]
        public async Task ReviewThatCantBeSavedIsRefused()
        {
            var reviews = new ArmorReviewData(database, NoDatabase.Names);
            var review = Review();

            review.to_status = "Accepted";
            await RefusedAsync(() => reviews.AddReview(review));

            review = Review();
            review.from_status = "approved";
            await RefusedAsync(() => reviews.AddReview(review));

            review = Review();
            review.reviewed_at = DateTime.Now;
            await RefusedAsync(() => reviews.AddReview(review));

            // A reason is what the reviewer wrote, so it isn't cut
            review = Review();
            review.reason = new string('r', 501);
            await RefusedAsync(() => reviews.AddReview(review));
        }

        [Theory]
        [InlineData("unknown outcome")]
        [InlineData("outcome in capitals")]
        [InlineData("unknown role")]
        [InlineData("local time")]
        [InlineData("action with a capital")]
        [InlineData("action without a dot")]
        [InlineData("action with a space")]
        [InlineData("action with a line end")]
        [InlineData("action over 50 characters")]
        [InlineData("target over 40 characters")]
        [InlineData("target type over 30 characters")]
        [InlineData("JSON before over 16,000 characters")]
        [InlineData("JSON after over 16,000 characters")]
        public async Task AuditRowThatCantBeSavedIsRefused(string problem)
        {
            var entry = Entry();

            switch (problem)
            {
                case "unknown outcome": entry.outcome = "done"; break;
                case "outcome in capitals": entry.outcome = "OK"; break;
                case "unknown role": entry.actor_role = "moderator"; break;
                case "local time": entry.at = DateTime.Now; break;
                case "action with a capital": entry.action = "armor.Approve"; break;
                case "action without a dot": entry.action = "approve"; break;
                case "action with a space": entry.action = "armor. approve"; break;
                case "action with a line end": entry.action = "armor.approve\n"; break;
                case "action over 50 characters": entry.action = "armor." + new string('a', 45); break;
                case "target over 40 characters": entry.target_id = new string('1', 41); break;
                case "target type over 30 characters": entry.target_type = new string('t', 31); break;
                case "JSON before over 16,000 characters": entry.before_json = new string('j', 16001); break;
                case "JSON after over 16,000 characters": entry.after_json = new string('j', 16001); break;
                default: throw new ArgumentOutOfRangeException(nameof(problem), problem, "The test has no such case");
            }

            await RefusedAsync(() => new AuditLogData(database, NoDatabase.Names).AddAudit(entry));
        }

        // Each limit is the column's, so a row just inside them is fine
        [Fact]
        public async Task AuditRowAtTheLimitsPasses()
        {
            var entry = Entry();
            entry.action = "armor." + new string('a', 44);
            entry.target_id = new string('1', 40);
            entry.target_type = new string('t', 30);
            entry.before_json = new string('j', 16000);

            Assert.Equal(50, entry.action.Length);

            // It gets as far as building its SQL, which needs MinerBot's database
            var missing = new AuditLogData(database, new DatabaseNames(new Dictionary<DBConnectionName, string>()));
            await Assert.ThrowsAsync<InvalidOperationException>(() => missing.AddAudit(entry));
        }

        [Fact]
        public async Task AuditFiltersInLocalTimeAreRefused()
        {
            await RefusedAsync(() => new AuditLogData(database, NoDatabase.Names).ListAudit(new AuditQuery { From = DateTime.Now }));
            await RefusedAsync(() => new AuditLogData(database, NoDatabase.Names).ListAudit(new AuditQuery { To = DateTime.Now }));
            await RefusedAsync(() => new AuditLogData(database, NoDatabase.Names).ListAudit(new AuditQuery { PageSize = 0 }));
        }

        // The dashboard's keys are 256 random bits, so a short one is a mistake
        [Fact]
        public async Task ShortSessionKeyIsRefused()
        {
            var sessions = new DashboardSessionData(database, NoDatabase.Names);
            var shortKey = new string('k', 31);

            await RefusedAsync(() => sessions.CreateSession(shortKey, new DashboardSession { user_name = "Test User", ticket = [1] }));
            await RefusedAsync(() => sessions.GetSession(shortKey, DateTime.UtcNow));
            await RefusedAsync(() => sessions.TouchSession(shortKey, DateTime.UtcNow));
            await RefusedAsync(() => sessions.RenewSession(shortKey, [1]));
            await RefusedAsync(() => sessions.DeleteSession(shortKey));
            await RefusedAsync(() => sessions.DeleteOtherSessions(1, shortKey));
        }

        [Fact]
        public async Task KeepingFewerThanNoSessionsIsRefused()
        {
            await RefusedAsync(() => new DashboardSessionData(database, NoDatabase.Names).DeleteOldestSessions(1, -1));
        }

        [Fact]
        public async Task SessionTimesInLocalTimeAreRefused()
        {
            var sessions = new DashboardSessionData(database, NoDatabase.Names);

            await RefusedAsync(() => sessions.CreateSession(SessionKey, new DashboardSession { user_name = "Test User", ticket = [1], expires_at = DateTime.Now }));
            await RefusedAsync(() => sessions.GetSession(SessionKey, DateTime.Now));
            await RefusedAsync(() => sessions.DeleteExpiredSessions(DateTime.Now, DateTime.UtcNow));
            await RefusedAsync(() => sessions.DeleteExpiredSessions(DateTime.UtcNow, DateTime.Now));
        }

        [Fact]
        public async Task BlacklistPagesOutsideTheLimitsAreRefused()
        {
            await RefusedAsync(() => new BlacklistData(database).SearchBlacklist(null, null, -1, 25));
        }

        // Every way of writing a SteamID turns account 0 away, and so does the table's own code
        [Fact]
        public async Task BanOfAccountZeroIsRefused()
        {
            await RefusedAsync(() => new BlacklistData(database).InsertPlayerBan(new BlacklistModel { steam_id = 0, username = "Test Player", reason = "Testing", notes = "Made up" }));
        }

        // One character past each column's size, counted as MariaDB counts them, so an emoji is one. Strict mode would refuse
        // it with an error that looks like an outage, and the change it travels with would go too.
        [Theory]
        [InlineData("name", "a")]
        [InlineData("reason", "a")]
        [InlineData("notes", "a")]
        [InlineData("name", "\U0001F600")]
        [InlineData("reason", "\U0001F600")]
        [InlineData("notes", "\U0001F600")]
        public async Task BanTextPastTheColumnsIsRefused(string field, string character)
        {
            var limit = field switch
            {
                "name" => BlacklistData.UsernameLimit,
                "reason" => BlacklistData.ReasonLimit,
                _ => BlacklistData.NotesLimit
            };

            BlacklistModel Player(int length)
            {
                var text = string.Concat(Enumerable.Repeat(character, length));

                return new BlacklistModel
                {
                    steam_id = 12345,
                    username = field == "name" ? text : "Test Player",
                    reason = field == "reason" ? text : "Testing",
                    notes = field == "notes" ? text : "Made up"
                };
            }

            var blacklist = new BlacklistData(database);

            await RefusedAsync(() => blacklist.InsertPlayerBan(Player(limit + 1)));
            await RefusedAsync(() => blacklist.UpdatePlayerBan(Player(limit + 1)));

            // At the limit it gets as far as the database, which this one isn't
            await Assert.ThrowsAsync<InvalidOperationException>(() => blacklist.InsertPlayerBan(Player(limit)));
        }

        static BotEvent Event(uint seq = 1) => new() { run_id = 1, seq = seq, at = DateTime.UtcNow, kind = "discord.connected", level = EventLevel.Info };

        static BotStatus Status() => new()
        {
            run_id = 1,
            started_at = DateTime.UtcNow,
            heartbeat_at = DateTime.UtcNow,
            discord_state = DiscordState.Connected,
            steam_state = SteamState.LoggedOn
        };

        static JobRun Run() => new() { run_id = 1, seq = 1, job = "ArmorJob.AddColorArrays", started_at = DateTime.UtcNow, result = JobResult.Ok };

        static JobStatus Job() => new() { job = "ArmorJob.AddColorArrays", schedule = "0 * * * *", updated_at = DateTime.UtcNow };

        // A row the bot's tables can't take is a mistake in the bot, and the bot's writer drops what's refused here, before
        // any SQL, rather than failing every write after it
        [Theory]
        [InlineData("event in local time")]
        [InlineData("event kind without a dot")]
        [InlineData("event kind with two dots")]
        [InlineData("event kind in capitals")]
        [InlineData("event kind over 40 characters")]
        [InlineData("unknown event level")]
        [InlineData("event numbered 0")]
        [InlineData("more than 100 events")]
        public async Task EventThatCantBeSavedIsRefused(string problem)
        {
            var events = new List<BotEvent> { Event() };

            switch (problem)
            {
                case "event in local time": events[0].at = DateTime.Now; break;
                case "event kind without a dot": events[0].kind = "connected"; break;
                case "event kind with two dots": events[0].kind = "discord.gateway.connected"; break;
                case "event kind in capitals": events[0].kind = "Discord.Connected"; break;
                case "event kind over 40 characters": events[0].kind = "discord." + new string('c', 33); break;
                case "unknown event level": events[0].level = "notice"; break;
                case "event numbered 0": events[0].seq = 0; break;
                case "more than 100 events": events = Enumerable.Range(1, 101).Select(seq => Event((uint)seq)).ToList(); break;
                default: throw new ArgumentOutOfRangeException(nameof(problem), problem, "The test has no such case");
            }

            await RefusedAsync(() => new BotStatusData(database, NoDatabase.Names).AddEvents(events));
        }

        [Fact]
        public async Task StatusThatCantBeSavedIsRefused()
        {
            var status = new BotStatusData(database, NoDatabase.Names);
            var local = Status();
            local.heartbeat_at = DateTime.Now;
            var stopped = Status();
            stopped.stopped_at = DateTime.Now;
            var discord = Status();
            discord.discord_state = "up";
            var steam = Status();
            steam.steam_state = "online";
            var version = Status();
            version.version = new string('a', 41);

            foreach (var row in new[] { local, stopped, discord, steam, version })
                await RefusedAsync(() => status.SaveStatus(row));
        }

        [Fact]
        public async Task JobRowsThatCantBeSavedAreRefused()
        {
            var status = new BotStatusData(database, NoDatabase.Names);
            var result = Run();
            result.result = "timed_out";
            var started = Run();
            started.started_at = DateTime.Now;
            var job = Run();
            job.job = new string('j', 101);
            var lastResult = Job();
            lastResult.last_result = "passed";
            var schedule = Job();
            schedule.schedule = new string('*', 101);
            var updated = Job();
            updated.updated_at = DateTime.Now;

            await RefusedAsync(() => status.SaveJobRuns([result]));
            await RefusedAsync(() => status.SaveJobRuns([started]));
            await RefusedAsync(() => status.SaveJobRuns([job]));
            await RefusedAsync(() => status.SaveJobRuns(Enumerable.Range(1, 101).Select(_ => Run()).ToList()));
            await RefusedAsync(() => status.SaveJobStatus([lastResult]));
            await RefusedAsync(() => status.SaveJobStatus([schedule]));
            await RefusedAsync(() => status.SaveJobStatus([updated]));

            // NOT IN an empty list would delete every job's status
            await RefusedAsync(() => status.DeleteOtherJobs([]));
        }

        // StatusWriter drops a batch that's refused as it's called, the bot's own mistake, and keeps one whose write failed,
        // an outage. So a batch write refuses before it hands back a task, which awaiting the call can't tell apart.
        [Fact]
        public void BatchesAreRefusedBeforeTheCallReturns()
        {
            var status = new BotStatusData(database, NoDatabase.Names);
            var run = Run();
            run.result = "timed_out";
            var job = Job();
            job.updated_at = DateTime.Now;

            Assert.ThrowsAny<ArgumentException>(() => { _ = status.AddEvents([Event(seq: 0)]); });
            Assert.ThrowsAny<ArgumentException>(() => { _ = status.SaveJobRuns([run]); });
            Assert.ThrowsAny<ArgumentException>(() => { _ = status.SaveJobStatus([job]); });
            Assert.ThrowsAny<ArgumentException>(() => { _ = status.SaveSamples([new UptimeSample { minute = new DateTime(2026, 11, 14, 12, 0, 1, DateTimeKind.Utc) }]); });
            Assert.Equal(0, database.Created);
        }

        // A minute is its start. DATETIME keeps whole seconds, so a sample with seconds would be a second row for its
        // minute, and it drops a fraction of a second without a word.
        [Fact]
        public async Task SampleThatIsntAMinutesStartIsRefused()
        {
            var status = new BotStatusData(database, NoDatabase.Names);
            var minute = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

            await RefusedAsync(() => status.SaveSamples([new UptimeSample { minute = minute.AddSeconds(1) }]));
            await RefusedAsync(() => status.SaveSamples([new UptimeSample { minute = minute.AddMilliseconds(1) }]));
            await RefusedAsync(() => status.SaveSamples([new UptimeSample { minute = DateTime.SpecifyKind(minute, DateTimeKind.Local) }]));
        }

        // Each limit is the column's, so rows just inside them are fine
        [Fact]
        public async Task StatusRowsAtTheLimitsPass()
        {
            var events = Enumerable.Range(1, 100).Select(seq => Event((uint)seq)).ToList();
            events[0].kind = "discord." + new string('c', 32);
            var run = Run();
            run.job = new string('j', 100);
            var job = Job();
            job.schedule = new string('*', 100);
            var status = Status();
            status.version = new string('a', 40);

            Assert.Equal(40, events[0].kind.Length);

            // They get as far as building their SQL, which needs MinerBot's database
            var missing = new BotStatusData(database, new DatabaseNames(new Dictionary<DBConnectionName, string>()));
            await Assert.ThrowsAsync<InvalidOperationException>(() => missing.AddEvents(events));
            await Assert.ThrowsAsync<InvalidOperationException>(() => missing.SaveJobRuns([run]));
            await Assert.ThrowsAsync<InvalidOperationException>(() => missing.SaveJobStatus([job]));
            await Assert.ThrowsAsync<InvalidOperationException>(() => missing.SaveStatus(status));
            await Assert.ThrowsAsync<InvalidOperationException>(() => missing.SaveSamples([new UptimeSample { minute = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc) }]));
            Assert.Equal(0, database.Created);
        }

        // A chunk takes from 1 to 5,000 rows, and its cutoffs are UTC. The samples and the days to total go by whole days.
        [Fact]
        public async Task RetentionOutsideItsLimitsIsRefused()
        {
            var retention = new RetentionData(database, NoDatabase.Names);
            var day = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);

            await RefusedAsync(() => retention.DeleteAuditRows(day, 0));
            await RefusedAsync(() => retention.DeleteAuditRows(day, RetentionData.ChunkLimit + 1));
            await RefusedAsync(() => retention.DeleteAuditRows(DateTime.Now, 1));
            await RefusedAsync(() => retention.ClearAuditAddresses(DateTime.Now, 1));
            await RefusedAsync(() => retention.DeleteEvents(day, -1));
            await RefusedAsync(() => retention.DeleteJobRuns(DateTime.Now, 1));
            await RefusedAsync(() => retention.DeleteSessions(DateTime.Now, 1));
            await RefusedAsync(() => retention.DeleteSamples(day.AddMinutes(1), 1));
            await RefusedAsync(() => retention.TotalUptimeDays(day.AddHours(1), day.AddDays(1)));
            await RefusedAsync(() => retention.TotalUptimeDays(day, day.AddDays(-1)));
            await RefusedAsync(() => retention.TotalUptimeDays(DateTime.SpecifyKind(day, DateTimeKind.Local), day));
            await RefusedAsync(() => retention.DeleteGuildEvents(DateTime.Now, 1));
            await RefusedAsync(() => retention.DeleteGuildEvents(day, RetentionData.ChunkLimit + 1));
        }

        static BotGuild Server() => new() { guild_id = 1, name = "Test Server", available = true };

        static BotGuildChannel TextChannel(ulong id = 1) => new() { channel_id = id, name = "general", type = GuildChannelType.Text };

        // A server the tables can't take is a mistake in the bot, and the bot's writer drops what's refused here, before any
        // SQL, rather than trying it again at every tick
        [Theory]
        [InlineData("server written in local time")]
        [InlineData("join in local time")]
        [InlineData("event numbered 0")]
        [InlineData("server without a name")]
        [InlineData("icon hash over 64 characters")]
        [InlineData("unknown channel type")]
        [InlineData("channel without a name")]
        [InlineData("more than 500 channels")]
        public async Task ServerThatCantBeSavedIsRefused(string problem)
        {
            var server = Server();
            var channels = new List<BotGuildChannel> { TextChannel() };
            var at = DateTime.UtcNow;
            uint seq = 1;

            switch (problem)
            {
                case "server written in local time": at = DateTime.Now; break;
                case "join in local time": server = server with { bot_joined_at = DateTime.Now }; break;
                case "event numbered 0": seq = 0; break;
                case "server without a name": server = server with { name = "" }; break;
                case "icon hash over 64 characters": server = server with { icon_hash = new string('a', 65) }; break;
                case "unknown channel type": channels[0] = channels[0] with { type = "voice" }; break;
                case "channel without a name": channels[0] = channels[0] with { name = null! }; break;
                case "more than 500 channels": channels = Enumerable.Range(1, 501).Select(id => TextChannel((ulong)id)).ToList(); break;
                default: throw new ArgumentOutOfRangeException(nameof(problem), problem, "The test has no such case");
            }

            await RefusedAsync(() => new BotGuildData(database, NoDatabase.Names).SaveGuild(server, channels, at, 1, seq));
        }

        [Fact]
        public async Task LeavesAndCountsThatCantBeSavedAreRefused()
        {
            var data = new BotGuildData(database, NoDatabase.Names);
            var day = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);

            await RefusedAsync(() => data.LeaveGuild(1, DateTime.Now, 1, 1, null));
            await RefusedAsync(() => data.LeaveGuild(1, DateTime.UtcNow, 1, 0, null));
            await RefusedAsync(() => data.MarkUnavailable([1], DateTime.Now));
            await RefusedAsync(() => data.MarkUnavailable(Enumerable.Range(1, BotGuildData.BatchLimit + 1).Select(id => (ulong)id).ToList(), DateTime.UtcNow));
            await RefusedAsync(() => data.SaveMemberCount(1, 10, day.AddHours(9), day.AddHours(9)));
            await RefusedAsync(() => data.SaveMemberCount(1, 10, DateTime.SpecifyKind(day, DateTimeKind.Local), day));
            await RefusedAsync(() => data.SaveMemberCount(1, 10, day, DateTime.Now));
        }

        // The server writer drops a server that's refused as it's called, and keeps one whose write failed
        [Fact]
        public void ServersAreRefusedBeforeTheCallReturns()
        {
            var data = new BotGuildData(database, NoDatabase.Names);

            Assert.ThrowsAny<ArgumentException>(() => { _ = data.SaveGuild(Server() with { name = "" }, [], DateTime.UtcNow, 1, 1); });
            Assert.ThrowsAny<ArgumentException>(() => { _ = data.LeaveGuild(1, DateTime.Now, 1, 1, null); });
            Assert.ThrowsAny<ArgumentException>(() => { _ = data.MarkUnavailable([1], DateTime.Now); });
            Assert.Equal(0, database.Created);
        }

        // Each limit is the column's or Discord's, so values just inside them are fine, and names past theirs are clipped
        [Fact]
        public async Task ServersAtTheLimitsPass()
        {
            var server = Server() with { name = new string('n', 150), icon_hash = new string('a', 64) };
            var channels = Enumerable.Range(1, BotGuildData.ChannelLimit).Select(id => TextChannel((ulong)id) with { name = new string('c', 150) }).ToList();
            var day = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);

            // They get as far as building their SQL, which needs MinerBot's database
            var missing = new BotGuildData(database, new DatabaseNames(new Dictionary<DBConnectionName, string>()));
            await Assert.ThrowsAsync<InvalidOperationException>(() => missing.SaveGuild(server, channels, DateTime.UtcNow, 1, 1));
            await Assert.ThrowsAsync<InvalidOperationException>(() => missing.LeaveGuild(1, DateTime.UtcNow, 1, 1, new string('d', 400)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => missing.MarkUnavailable(Enumerable.Range(1, BotGuildData.BatchLimit).Select(id => (ulong)id).ToList(), DateTime.UtcNow));
            await Assert.ThrowsAsync<InvalidOperationException>(() => missing.SaveMemberCount(1, 10, day, DateTime.UtcNow));
            await Assert.ThrowsAsync<InvalidOperationException>(() => missing.ListGuildsIn());
            Assert.Equal(0, database.Created);
        }
    }
}
