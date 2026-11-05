using System.Security.Cryptography;
using System.Text;
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
        // The database every test that starts the app needs, with no sessions or audit rows left from another test
        public static async Task<MariaDbFixture> ReadyAsync(this MariaDbFixture fixture)
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.SessionTable, MariaDbFixture.AuditTable);
            return db;
        }

        public static Task<List<SessionRow>> SessionsAsync(this MariaDbFixture db) =>
            db.RootQueryAsync<SessionRow>($"SELECT * FROM {MariaDbFixture.SessionTable} ORDER BY created_at, user_id");

        public static async Task<SessionRow?> SessionAsync(this MariaDbFixture db, string key) =>
            (await db.SessionsAsync()).SingleOrDefault(row => row.IsFor(key));

        public static Task<List<AuditEntry>> AuditAsync(this MariaDbFixture db) =>
            db.RootQueryAsync<AuditEntry>($"SELECT * FROM {MariaDbFixture.AuditTable} ORDER BY id");
    }
}
