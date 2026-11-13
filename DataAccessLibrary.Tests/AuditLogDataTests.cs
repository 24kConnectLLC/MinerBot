using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Tests.Database;

namespace DataAccessLibrary.Tests
{
    [Collection("Database")]
    public class AuditLogDataTests(MariaDbFixture fixture)
    {
        static readonly DateTime Noon = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

        async Task<MariaDbFixture> StartAsync()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.AuditTable);
            return db;
        }

        static AuditEntry Entry(DateTime at, string action = "armor.approve", ulong? actor = 1000000000000000001, ulong? guild = null,
            string? targetType = "armor", string? targetId = "12") => new()
        {
            at = at,
            actor_id = actor,
            actor_name = "Test Admin",
            actor_role = AuditRole.Admin,
            action = action,
            outcome = AuditOutcome.Ok,
            guild_id = guild,
            target_type = targetType,
            target_id = targetId
        };

        static IEnumerable<string> Actions(Page<AuditEntry> page) => page.Rows.Select(entry => entry.action);

        // BIGINT UNSIGNED and ulong hold the biggest Discord ID, and DATETIME(3) the milliseconds
        [Fact]
        public async Task EveryFieldRoundTrips()
        {
            var db = await StartAsync();
            var audit = db.Audit();
            var stored = new AuditEntry
            {
                at = Noon.AddMilliseconds(123),
                actor_id = ulong.MaxValue,
                actor_name = "Test Owner",
                actor_role = AuditRole.Owner,
                action = "guild.settings.save",
                outcome = AuditOutcome.Unknown,
                guild_id = ulong.MaxValue,
                target_type = "guild",
                target_id = "18446744073709551615",
                before_json = """{"armor_channel_id":"1"}""",
                after_json = """{"armor_channel_id":"2"}""",
                detail = "The save timed out",
                ip = "2001:db8::1",
                request_id = "0HN7TESTREQUEST:00000001"
            };

            await audit.AddAudit(stored);

            var page = await audit.ListAudit(new AuditQuery { IncludeIp = true });
            var entry = Assert.Single(page.Rows);

            Assert.True(entry.id > 0);
            stored.id = entry.id;
            entry.at = DateTime.SpecifyKind(entry.at, DateTimeKind.Utc);
            Assert.Equivalent(stored, entry, strict: true);
            Assert.Equal(1, page.Total);
        }

        // A row for the bot itself has no actor or target
        [Fact]
        public async Task NullsAreKept()
        {
            var db = await StartAsync();
            var audit = db.Audit();

            await audit.AddAudit(new AuditEntry { at = Noon, actor_role = AuditRole.Bot, action = "bot.restart", outcome = AuditOutcome.Failed });

            var entry = Assert.Single((await audit.ListAudit(new AuditQuery())).Rows);
            Assert.Null(entry.actor_id);
            Assert.Null(entry.actor_name);
            Assert.Null(entry.guild_id);
            Assert.Null(entry.target_type);
            Assert.Null(entry.before_json);
        }

        [Fact]
        public async Task EachFilterWorksOnItsOwn()
        {
            var db = await StartAsync();
            var audit = db.Audit();

            await audit.AddAudit(Entry(Noon, "armor.approve", actor: 1, targetId: "12"));
            await audit.AddAudit(Entry(Noon.AddMinutes(1), "armor.reject", actor: 2, targetId: "13"));
            await audit.AddAudit(Entry(Noon.AddMinutes(2), "guild.settings.save", actor: 1, guild: 1000000000000000005, targetType: "guild", targetId: "1000000000000000005"));
            await audit.AddAudit(Entry(Noon.AddMinutes(3), "blacklist.add", actor: 3, targetType: "player", targetId: "12345"));

            Assert.Equal(["guild.settings.save", "armor.approve"], Actions(await audit.ListAudit(new AuditQuery { ActorId = 1 })));
            Assert.Equal(["armor.reject"], Actions(await audit.ListAudit(new AuditQuery { Action = "armor.reject" })));
            Assert.Equal(["guild.settings.save"], Actions(await audit.ListAudit(new AuditQuery { GuildId = 1000000000000000005 })));
            Assert.Equal(["armor.reject", "armor.approve"], Actions(await audit.ListAudit(new AuditQuery { TargetType = "armor" })));
            Assert.Equal(["armor.approve"], Actions(await audit.ListAudit(new AuditQuery { TargetType = "armor", TargetId = "12" })));
            Assert.Empty((await audit.ListAudit(new AuditQuery { TargetType = "guild", TargetId = "12" })).Rows);

            var both = await audit.ListAudit(new AuditQuery { ActorId = 1, Action = "armor.approve" });
            Assert.Equal(["armor.approve"], Actions(both));
            Assert.Equal(1, both.Total);
        }

        // From is included and To isn't, so days next to each other don't share a row
        [Fact]
        public async Task DateRangeIncludesItsStartOnly()
        {
            var db = await StartAsync();
            var audit = db.Audit();

            await audit.AddAudit(Entry(Noon.AddMilliseconds(-1), "test.before"));
            await audit.AddAudit(Entry(Noon, "test.start"));
            await audit.AddAudit(Entry(Noon.AddHours(1).AddMilliseconds(-1), "test.inside"));
            await audit.AddAudit(Entry(Noon.AddHours(1), "test.end"));

            var range = await audit.ListAudit(new AuditQuery { From = Noon, To = Noon.AddHours(1) });

            Assert.Equal(["test.inside", "test.start"], Actions(range));
            Assert.Equal(2, range.Total);
            Assert.Equal(["test.end", "test.inside", "test.start"], Actions(await audit.ListAudit(new AuditQuery { From = Noon })));
            Assert.Equal(["test.inside", "test.start", "test.before"], Actions(await audit.ListAudit(new AuditQuery { To = Noon.AddHours(1) })));
        }

        [Fact]
        public async Task NewestFirstInPagesWithTheTotal()
        {
            var db = await StartAsync();
            var audit = db.Audit();

            // Two at the same moment come in the order they were written, latest first
            await audit.AddAudit(Entry(Noon, "test.first"));
            await audit.AddAudit(Entry(Noon, "test.second"));
            await audit.AddAudit(Entry(Noon.AddSeconds(1), "test.third"));
            await audit.AddAudit(Entry(Noon.AddSeconds(2), "test.fourth"));
            await audit.AddAudit(Entry(Noon.AddSeconds(3), "test.fifth"));

            var first = await audit.ListAudit(new AuditQuery { PageSize = 2 });
            var second = await audit.ListAudit(new AuditQuery { Page = 1, PageSize = 2 });
            var last = await audit.ListAudit(new AuditQuery { Page = 2, PageSize = 2 });

            Assert.Equal(["test.fifth", "test.fourth"], Actions(first));
            Assert.Equal(["test.third", "test.second"], Actions(second));
            Assert.Equal(["test.first"], Actions(last));
            Assert.All(new[] { first, second, last }, page => Assert.Equal(5, page.Total));
        }

        // A refused sign-in by someone on none of the dashboard's lists is kept with the visitor role. Fails if Visitor is
        // left out of IsKnown, which would refuse the row and with it the refusal's record.
        [Fact]
        public async Task VisitorRefusalIsKept()
        {
            var db = await StartAsync();
            var audit = db.Audit();

            await audit.AddAudit(new AuditEntry
            {
                at = Noon,
                actor_id = 1000000000000000099,
                actor_name = "Test Visitor",
                actor_role = AuditRole.Visitor,
                action = "auth.refused",
                outcome = AuditOutcome.Refused,
                target_type = "session",
                detail = "Not on the dashboard's lists"
            });

            var entry = Assert.Single((await audit.ListAudit(new AuditQuery { Action = "auth.refused" })).Rows);
            Assert.Equal(AuditRole.Visitor, entry.actor_role);
            Assert.Equal(AuditOutcome.Refused, entry.outcome);
            Assert.Equal(1000000000000000099UL, entry.actor_id);
            Assert.Contains(AuditRole.Visitor, AuditRole.All);
        }

        // Text that's only for reading is cut to fit rather than failing the change it travels with
        [Fact]
        public async Task LongTextIsClipped()
        {
            var db = await StartAsync();
            var audit = db.Audit();
            var entry = Entry(Noon);
            entry.detail = string.Concat(Enumerable.Range(0, 100).Select(i => $"{i:0000000000}"));
            entry.actor_name = new string('n', 150);
            entry.request_id = new string('r', 70);

            await audit.AddAudit(entry);

            var stored = Assert.Single((await audit.ListAudit(new AuditQuery())).Rows);
            Assert.Equal(entry.detail[..300], stored.detail);
            Assert.Equal(new string('n', 100), stored.actor_name);
            Assert.Equal(new string('r', 64), stored.request_id);
        }

        // The visitor's address comes back only for a caller that asks, which only the audit page does, for the Owner
        [Fact]
        public async Task AddressOnlyWhenAskedFor()
        {
            var db = await StartAsync();
            var audit = db.Audit();
            var entry = Entry(Noon);
            entry.ip = "203.0.113.9";
            await audit.AddAudit(entry);

            Assert.Null(Assert.Single((await audit.ListAudit(new AuditQuery())).Rows).ip);
            Assert.Null(Assert.Single((await audit.ListAudit(new AuditQuery { TargetType = "armor", TargetId = "12" })).Rows).ip);
            Assert.Equal("203.0.113.9", Assert.Single((await audit.ListAudit(new AuditQuery { IncludeIp = true })).Rows).ip);
            Assert.Null(Assert.Single(await audit.LatestChanges("armor", ["12"])).ip);
        }

        // The latest change that went through for each target, in ListAudit's order: the time, then the order the rows were
        // written. A refusal, a failure and a change that may not have landed don't count, and neither do other targets.
        [Fact]
        public async Task LatestChangeOfEachTarget()
        {
            var db = await StartAsync();
            var audit = db.Audit();

            AuditEntry Row(string target, string action, DateTime at, string outcome = AuditOutcome.Ok, string type = "blacklist")
            {
                var entry = Entry(at, action, targetType: type, targetId: target);
                entry.outcome = outcome;
                return entry;
            }

            // A change whose transaction ran again after a deadlock keeps the time it was given before its first run, so a
            // later ID can have an earlier time
            await audit.AddAudit(Row("1", "blacklist.edit", Noon.AddMinutes(5)));
            await audit.AddAudit(Row("1", "blacklist.add", Noon));
            await audit.AddAudit(Row("1", "blacklist.remove", Noon.AddMinutes(6), AuditOutcome.Refused));

            // Two at the same moment: the one written last
            await audit.AddAudit(Row("2", "blacklist.add", Noon));
            await audit.AddAudit(Row("2", "blacklist.edit", Noon));

            // Only refused, unknown and failed rows, and a row about armor with the same ID
            await audit.AddAudit(Row("3", "blacklist.add", Noon, AuditOutcome.Refused));
            await audit.AddAudit(Row("3", "blacklist.add", Noon, AuditOutcome.Unknown));
            await audit.AddAudit(Row("3", "blacklist.add", Noon, AuditOutcome.Failed));
            await audit.AddAudit(Row("3", "armor.approve", Noon.AddHours(1), type: "armor"));

            var latest = (await audit.LatestChanges("blacklist", ["1", "2", "3", "4"])).ToDictionary(entry => entry.target_id!);

            Assert.Equal(["1", "2"], latest.Keys.Order(StringComparer.Ordinal));
            Assert.Equal("blacklist.edit", latest["1"].action);
            Assert.Equal(Noon.AddMinutes(5), DateTime.SpecifyKind(latest["1"].at, DateTimeKind.Utc));
            Assert.Equal("blacklist.edit", latest["2"].action);
            Assert.Equal("Test Admin", latest["2"].actor_name);
            Assert.Equal(AuditRole.Admin, latest["2"].actor_role);
            Assert.All(latest.Values, entry => Assert.Equal(AuditOutcome.Ok, entry.outcome));

            Assert.Equal("armor.approve", Assert.Single(await audit.LatestChanges("armor", ["3"])).action);
            Assert.Empty(await audit.LatestChanges("blacklist", ["5"]));
        }

        // A page with no rows asks for nothing, so no connection is made
        [Fact]
        public async Task LatestChangesOfNoTargetsAsksNothing()
        {
            var database = new NoDatabase();

            Assert.Empty(await new AuditLogData(database, NoDatabase.Names).LatestChanges("blacklist", []));
            Assert.Equal(0, database.Created);
        }

        // TEXT holds at least 16,000 characters of any kind, which is the limit the code checks
        [Fact]
        public async Task JsonAtItsLimitFits()
        {
            var db = await StartAsync();
            var audit = db.Audit();
            var entry = Entry(Noon);
            entry.after_json = string.Concat(Enumerable.Repeat("\U0001F600", 16000));

            await audit.AddAudit(entry);

            Assert.Equal(entry.after_json, Assert.Single((await audit.ListAudit(new AuditQuery())).Rows).after_json);
        }
    }
}
