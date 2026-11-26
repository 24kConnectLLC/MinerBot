using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using DataAccessLibrary.Posting.Models;
using DataAccessLibrary.Tests.Database;
using MySql.Data.MySqlClient;

namespace DataAccessLibrary.Tests
{
    // deploy/README.md's way back from PR 11's dashboard deploy, for the dashboard from before, which can neither show nor
    // change the switches the bot still goes by: its read of the servers with a kind switched off, and its statement that
    // turns one kind on again for one server, run as root in MinerBot's database, as `mariadb <BOT_DB> -e` runs them. The
    // test project copies the runbook into its output's deploy folder.
    [Collection("Database")]
    public class SwitchesBackOutTests(MariaDbFixture fixture)
    {
        static readonly DateTime Noon = new(2026, 11, 24, 12, 0, 0, 321, DateTimeKind.Utc);
        static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(30);
        const ulong LobbiesOff = 1000000000000000001, ArmorAndMapsOff = 1000000000000000002, AllOn = 1000000000000000003, ArmorOff = 1000000000000000004;
        const ulong OwnerId = 1000000000000000009;

        // A row of the read, by the columns it names
        sealed record SwitchedOff
        {
            public ulong guild_id { get; set; }
            public string? name { get; set; }
            public bool armor_on { get; set; }
            public bool maps_on { get; set; }
            public bool lobbies_on { get; set; }
        }

        async Task<MariaDbFixture> StartAsync()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.SwitchTable, MariaDbFixture.BotGuildTable);
            return db;
        }

        // The back-out's statements, from the paragraph that begins it to the end of its code block
        static List<string> Statements()
        {
            var runbook = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "deploy", "README.md"));
            var backOut = Regex.Match(runbook, @"^To back out once the dashboard's swap completed\b.*?^```bash\r?\n(.*?)^```", RegexOptions.Multiline | RegexOptions.Singleline);
            Assert.True(backOut.Success, "The runbook has no back-out of the dashboard's deploy with its statements");

            return [.. Regex.Matches(backOut.Groups[1].Value, @"^mariadb [^\r\n]*<BOT_DB from server\.conf> -e ""([^""]+)""\r?$", RegexOptions.Multiline).Select(match => match.Groups[1].Value)];
        }

        // In one session of root's in MinerBot's database, as `mariadb <BOT_DB>` runs it, never pooled
        static async Task<MySqlConnection> SessionAsync(MariaDbFixture db)
        {
            var session = new MySqlConnection(new MySqlConnectionStringBuilder(db.RootConnection) { Database = MariaDbFixture.BotDatabase, Pooling = false }.ConnectionString);
            await session.OpenAsync(TestContext.Current.CancellationToken);
            return session;
        }

        static async Task<List<SwitchedOff>> ReadAsync(MariaDbFixture db, string read)
        {
            await using var session = await SessionAsync(db);
            var rows = await session.QueryAsync<SwitchedOff>(new CommandDefinition(read, cancellationToken: TestContext.Current.CancellationToken)).WaitAsync(CallLimit, TestContext.Current.CancellationToken);
            return [.. rows.OrderBy(row => row.guild_id)];
        }

        static async Task TurnOnAsync(MariaDbFixture db, string turnOn, ulong guildId)
        {
            await using var session = await SessionAsync(db);
            var statement = turnOn.Replace("<server ID>", guildId.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(1, await session.ExecuteAsync(new CommandDefinition(statement, cancellationToken: TestContext.Current.CancellationToken)).WaitAsync(CallLimit, TestContext.Current.CancellationToken));
        }

        // A server in the bot's copy, as the bot writes one
        static Task AddGuildAsync(MariaDbFixture db, ulong guildId, string name) => db.RootExecuteAsync(
            $@"INSERT INTO {MariaDbFixture.BotGuildTable} (guild_id, name, icon_hash, member_count, bot_joined_at, first_seen_at, left_at, available, bot_administrator,
               bot_permissions, updated_at) VALUES (@guildId, @name, NULL, 10, NULL, @Noon, NULL, 1, 0, 0, @Noon)",
            new { guildId, name, Noon });

        // A server's switches, as the dashboard's first save writes them
        static Task AddSwitchesAsync(MariaDbFixture db, ulong guildId, bool armor, bool maps, bool lobbies) => db.RootExecuteAsync(
            $@"INSERT INTO {MariaDbFixture.SwitchTable} (guild_id, armor_on, maps_on, lobbies_on, version, changed_at, changed_by, changed_by_role)
               VALUES (@guildId, @armor, @maps, @lobbies, 1, @Noon, @OwnerId, 'owner')",
            new { guildId, armor, maps, lobbies, Noon, OwnerId });

        // With the dashboard put back, the read lists every server with a kind switched off, with its name when the bot's copy
        // has the server, and none with every kind on; the statement for armor turns on armor alone, for one server, with a
        // new version and time. A server whose only kind off was armor then drops out of the list.
        [Fact]
        public async Task BackOutListsTheServersWithAKindOffAndTurnsArmorOnForOne()
        {
            var db = await StartAsync();
            await AddGuildAsync(db, LobbiesOff, "Test server");
            await AddGuildAsync(db, ArmorAndMapsOff, "Second test server");
            await AddGuildAsync(db, AllOn, "Third test server");
            await AddSwitchesAsync(db, LobbiesOff, armor: true, maps: true, lobbies: false);
            await AddSwitchesAsync(db, ArmorAndMapsOff, armor: false, maps: false, lobbies: true);
            await AddSwitchesAsync(db, AllOn, armor: true, maps: true, lobbies: true);
            await AddSwitchesAsync(db, ArmorOff, armor: false, maps: true, lobbies: true);

            var statements = Statements();
            Assert.Equal(2, statements.Count);
            var (read, turnOn) = (statements[0], statements[1]);

            Assert.Equal([(LobbiesOff, "Test server", true, true, false), (ArmorAndMapsOff, "Second test server", false, false, true), (ArmorOff, (string?)null, false, true, true)],
                (await ReadAsync(db, read)).Select(row => (row.guild_id, row.name, row.armor_on, row.maps_on, row.lobbies_on)));

            await TurnOnAsync(db, turnOn, ArmorAndMapsOff);
            await TurnOnAsync(db, turnOn, ArmorOff);

            Assert.Equal([(LobbiesOff, true, true, false), (ArmorAndMapsOff, true, false, true)],
                (await ReadAsync(db, read)).Select(row => (row.guild_id, row.armor_on, row.maps_on, row.lobbies_on)));

            var switches = await db.RootQueryAsync<GuildSwitches>($"SELECT * FROM {MariaDbFixture.SwitchTable} ORDER BY guild_id");
            Assert.Equal([(LobbiesOff, 1u, false), (ArmorAndMapsOff, 2u, true), (AllOn, 1u, false), (ArmorOff, 2u, true)],
                switches.Select(row => (row.guild_id, row.version, row.changed_at > Noon)));
        }
    }
}
