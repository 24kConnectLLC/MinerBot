using System.Security.Cryptography;
using System.Text;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Tests.Database;

namespace MinerBot.Dashboard.Tests.Support
{
    // A row of dashboard_sessions as the table has it. The names match the columns, as in DashboardSession.
    internal sealed class SessionRow
    {
        public byte[] id_hash { get; set; } = [];
        public ulong user_id { get; set; }
        public string user_name { get; set; } = "";
        public DateTime created_at { get; set; }
        public DateTime last_seen_at { get; set; }
        public DateTime expires_at { get; set; }
        public string? ip { get; set; }
        public string? user_agent { get; set; }
        public byte[] ticket { get; set; } = [];

        public bool IsFor(string key) => id_hash.AsSpan().SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    internal static class Rows
    {
        // The database every test that starts the app needs, with no sessions, audit rows, armor, reviews, blacklist entries,
        // requests to the bot or bot settings left from another test. Every sign-in writes a request since the sign-in
        // alerts, and every approval may, and the limits the dashboard checks before it sends one count them, so a test that
        // ran before this one, in whatever order xunit picks, can't change what this one counts.
        public static async Task<MariaDbFixture> ReadyAsync(this MariaDbFixture fixture)
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.SessionTable, MariaDbFixture.AuditTable, MariaDbFixture.ArmorTable, MariaDbFixture.ReviewTable,
                MariaDbFixture.BlacklistTable, MariaDbFixture.RequestTable, MariaDbFixture.SettingsTable);
            return db;
        }

        // MinerBot's status and history, its copy of its servers, and the website's channel settings, which the status pages
        // read, as the bot's own tests list them. ReadyAsync clears the bot's requests and settings.
        public static readonly string[] BotTables =
        [
            MariaDbFixture.StatusTable, MariaDbFixture.EventTable, MariaDbFixture.JobRunTable, MariaDbFixture.JobStatusTable, MariaDbFixture.SampleTable,
            MariaDbFixture.DailyTable, MariaDbFixture.BotGuildTable, MariaDbFixture.BotChannelTable, MariaDbFixture.GuildEventTable,
            MariaDbFixture.GuildDailyTable, MariaDbFixture.GuildTable
        ];

        // As ReadyAsync, and with none of the bot's other rows or the website's settings either, for a test that checks what
        // the status pages show: another test class may have left any of them
        public static async Task<MariaDbFixture> ReadyBotAsync(this MariaDbFixture fixture)
        {
            var db = await fixture.ReadyAsync();
            await db.ClearAsync(BotTables);
            return db;
        }

        public static Task<List<ArmorReview>> ReviewsAsync(this MariaDbFixture db) =>
            db.RootQueryAsync<ArmorReview>($"SELECT * FROM {MariaDbFixture.ReviewTable} ORDER BY id");

        public static async Task<string?> StatusAsync(this MariaDbFixture db, long id) =>
            (await db.RootQueryAsync<string?>($"SELECT Status FROM {MariaDbFixture.ArmorTable} WHERE ID = @id", new { id })).Single();

        public static Task<List<SessionRow>> SessionsAsync(this MariaDbFixture db) =>
            db.RootQueryAsync<SessionRow>($"SELECT * FROM {MariaDbFixture.SessionTable} ORDER BY created_at, user_id");

        public static async Task<SessionRow?> SessionAsync(this MariaDbFixture db, string key) =>
            (await db.SessionsAsync()).SingleOrDefault(row => row.IsFor(key));

        public static Task<List<AuditEntry>> AuditAsync(this MariaDbFixture db) =>
            db.RootQueryAsync<AuditEntry>($"SELECT * FROM {MariaDbFixture.AuditTable} ORDER BY id");
    }
}
