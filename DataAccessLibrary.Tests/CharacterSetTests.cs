using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Blacklist;
using DataAccessLibrary.Blacklist.Models;
using DataAccessLibrary.Dashboard;
using DataAccessLibrary.Dashboard.Models;
using DataAccessLibrary.Tests.Database;
using Microsoft.Extensions.Caching.Memory;
using MinerBot.Shared.Armor;

namespace DataAccessLibrary.Tests
{
    // MySql.Data uses the server's default character set when the connection string names none, and the live server's is
    // latin1, MariaDB's own default. Reading is unaffected, because MySql.Data asks for results as they're stored, but text sent to the server
    // that latin1 can't hold, such as an emoji or a zero-width space, arrives as a question mark. So every repository the
    // dashboard uses sends utf8mb4 whatever its connection string says, which these tests check over strings that name no
    // character set, while the bot's own queries keep theirs as configured.
    [Collection("Database")]
    public class CharacterSetTests(MariaDbFixture fixture)
    {
        const string Emoji = "\U0001F600";

        // An emoji and a zero-width space, neither of which latin1 has
        const string Name = $"Test {Emoji}​ Admin";

        static readonly DateTime Noon = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

        Dictionary<DBConnectionName, string> WithoutCharacterSet() => new()
        {
            [DBConnectionName.Armor] = fixture.LoginConnectionWithoutCharacterSet(MariaDbFixture.SiteDatabase),
            [DBConnectionName.Blacklist] = fixture.LoginConnectionWithoutCharacterSet(MariaDbFixture.SiteDatabase),
            [DBConnectionName.Guild] = fixture.LoginConnectionWithoutCharacterSet(MariaDbFixture.SiteDatabase),
            [DBConnectionName.Bot] = fixture.LoginConnectionWithoutCharacterSet(MariaDbFixture.BotDatabase)
        };

        static ArmorSubmission Approved(long id, string name) => new()
        {
            ID = id,
            CreatedAt = new DateTime(2026, 1, 1),
            Status = ArmorStatus.Approved,
            Author = "Test Author",
            ArmorName = name
        };

        static AuditEntry Audit() => new()
        {
            at = Noon,
            actor_name = Name,
            actor_role = AuditRole.Admin,
            action = "armor.approve",
            outcome = AuditOutcome.Ok,
            detail = Name
        };

        // Every test here assumes it, so a change in how the test server starts fails here, with the reason
        [Fact]
        public async Task ServerDefaultsToLatin1LikeTheLiveServer()
        {
            var db = fixture.Require();

            Assert.Equal("latin1", (await db.RootQueryAsync<string>("SELECT @@global.character_set_server")).Single());
        }

        [Fact]
        public async Task ReadingWithoutACharacterSetKeepsEveryCharacter()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.ArmorTable);
            await db.AddArmorAsync(Approved(1, $"Test {Emoji} Armor"));

            using var cache = new MemoryCache(new MemoryCacheOptions());
            var armor = new ArmorData(new SQLConnectionFactory(WithoutCharacterSet()), cache);

            Assert.Equal([$"Test {Emoji} Armor"], await armor.GetAllArmorNames());
        }

        [Fact]
        public async Task TheDashboardsWritesKeepEveryCharacter()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.AuditTable, MariaDbFixture.ReviewTable, MariaDbFixture.SessionTable, MariaDbFixture.BlacklistTable);

            var connections = WithoutCharacterSet();
            var factory = new SQLConnectionFactory(connections);
            var names = new DatabaseNames(connections);

            await new AuditLogData(factory, names).AddAudit(Audit());
            await new ArmorReviewData(factory, names).AddReview(new ArmorReview
            {
                armor_id = 1,
                from_status = ArmorStatus.Pending,
                to_status = ArmorStatus.NotApproved,
                reason = Name,
                reviewed_by = 1000000000000000001,
                reviewer_name = Name,
                reviewed_at = Noon
            });
            await new DashboardSessionData(factory, names).CreateSession(new string('k', 43), new DashboardSession
            {
                user_id = 1000000000000000001,
                user_name = Name,
                created_at = Noon,
                last_seen_at = Noon,
                expires_at = Noon.AddHours(12),
                ticket = [1]
            });
            await new BlacklistData(factory).InsertPlayerBan(new BlacklistModel { steam_id = 12345, username = Name, reason = Name, notes = Name });

            Assert.Equal([Name, Name], await db.RootQueryAsync<string>($"SELECT actor_name FROM {MariaDbFixture.AuditTable} UNION ALL SELECT detail FROM {MariaDbFixture.AuditTable}"));
            Assert.Equal([Name, Name], await db.RootQueryAsync<string>($"SELECT reason FROM {MariaDbFixture.ReviewTable} UNION ALL SELECT reviewer_name FROM {MariaDbFixture.ReviewTable}"));
            Assert.Equal([Name], await db.RootQueryAsync<string>($"SELECT user_name FROM {MariaDbFixture.SessionTable}"));
            Assert.Equal([Name, Name, Name], await db.RootQueryAsync<string>(
                $"SELECT username FROM {MariaDbFixture.BlacklistTable} UNION ALL SELECT reason FROM {MariaDbFixture.BlacklistTable} UNION ALL SELECT notes FROM {MariaDbFixture.BlacklistTable}"));
        }

        // An edit of a blacklist entry, inside a transaction as the entry's page makes it, keeps them too
        [Fact]
        public async Task BlacklistEditKeepsEveryCharacter()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.BlacklistTable);
            await db.Blacklist().InsertPlayerBan(new BlacklistModel { steam_id = 12345, username = "Test Player", reason = "Testing", notes = "Made up" });

            var blacklist = new BlacklistData(new SQLConnectionFactory(WithoutCharacterSet()));
            var edited = new BlacklistModel { steam_id = 12345, username = Name, reason = Name, notes = Name };

            Assert.True(await blacklist.UpdatePlayerBan(edited));
            Assert.Equal([Name, Name, Name], await db.RootQueryAsync<string>(
                $"SELECT username FROM {MariaDbFixture.BlacklistTable} UNION ALL SELECT reason FROM {MariaDbFixture.BlacklistTable} UNION ALL SELECT notes FROM {MariaDbFixture.BlacklistTable}"));

            edited.reason = "Edited " + Emoji + (char)0x200B;
            Assert.True(await blacklist.RunInTransaction(transaction => blacklist.UpdatePlayerBan(edited, transaction)));
            Assert.Equal(edited.reason, (await blacklist.GetBannedPlayer(12345))?.reason);
        }

        // A change and its audit row travel on the website's connection
        [Fact]
        public async Task TransactionOnTheWebsitesConnectionKeepsEveryCharacter()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.ArmorTable, MariaDbFixture.AuditTable);
            await db.AddArmorAsync(new ArmorSubmission { ID = 1, CreatedAt = new DateTime(2026, 1, 1), Status = ArmorStatus.Pending, Author = "Test Author", ArmorName = "Test Armor 1" });

            var connections = WithoutCharacterSet();
            var factory = new SQLConnectionFactory(connections);
            var armor = new ArmorSubmissionData(factory);
            var audit = new AuditLogData(factory, new DatabaseNames(connections));

            await armor.RunInTransaction(async transaction =>
            {
                await armor.ChangeStatus(1, ArmorStatus.Pending, ArmorStatus.Approved, transaction);
                await audit.AddAudit(Audit(), transaction);
                return true;
            });

            Assert.Equal([Name], await db.RootQueryAsync<string>($"SELECT actor_name FROM {MariaDbFixture.AuditTable}"));
        }

        // The approval rule's check finds a clash it would miss if the name arrived with question marks in it
        [Fact]
        public async Task NameCheckFindsWhatLatin1CantHold()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.ArmorTable);
            await db.AddArmorAsync(Approved(1, "Fire \U0001F525"));
            await db.AddArmorAsync(Approved(2, "Shadow Knight"));

            var armor = new ArmorSubmissionData(new SQLConnectionFactory(WithoutCharacterSet()));

            Assert.True(await armor.IsApprovedNameTaken("Fire \U0001F525", 0));
            Assert.False(await armor.IsApprovedNameTaken("Fire \U0001F30A", 0));
            Assert.True(await armor.IsApprovedNameTaken("Shadow​ Knight", 0));
            Assert.Equal([1L], (await armor.Search("\U0001F525", null, 0, 25)).Rows.Select(row => row.ID));
        }

        // The bot's own queries use its connection string as it is, so a deployment without CharSet behaves as it always has:
        // the name arrives with a question mark in it, and the lookup finds nothing
        [Fact]
        public async Task TheBotsLookupsKeepTheirConnectionString()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.ArmorTable);
            await db.AddArmorAsync(Approved(1, "Fire \U0001F525"));

            using var cache = new MemoryCache(new MemoryCacheOptions());

            Assert.Null(await new ArmorData(new SQLConnectionFactory(WithoutCharacterSet()), cache).GetArmorStyle("Fire \U0001F525"));
            Assert.NotNull(await db.Armor(cache).GetArmorStyle("Fire \U0001F525"));
        }
    }
}
