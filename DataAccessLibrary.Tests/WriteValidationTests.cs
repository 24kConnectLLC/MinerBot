using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Blacklist;
using DataAccessLibrary.Blacklist.Models;
using DataAccessLibrary.Dashboard;
using DataAccessLibrary.Dashboard.Models;
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
            await RefusedAsync(() => sessions.DeleteExpiredSessions(DateTime.Now));
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
    }
}
