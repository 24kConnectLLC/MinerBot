using System.Text.RegularExpressions;
using Dapper;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using DataAccessLibrary.Tests.Database;
using MinerBot.Shared.Delivery;
using MySql.Data.MySqlClient;

namespace DataAccessLibrary.Tests
{
    // deploy/README.md's restore step 7, which README.txt gives too, for a dump with migration 5: its statements, as both
    // runbooks give them, run as root in MinerBot's database, as `mariadb <BOT_DB> -e` runs them. The bot's first run after
    // it has nothing to post again: no baseline, nothing waiting, and no message being sent or in doubt, which its look in
    // the channel would find not there when the live bot sent it again after the dump, outside the minutes the look reads.
    // Each such message is left as the bot's own settle leaves one it can't find out about. The test project copies both
    // runbooks into its output's deploy folder.
    [Collection("Database")]
    public class RestoreResetTests(MariaDbFixture fixture)
    {
        static readonly DateTime Noon = new(2026, 11, 24, 12, 0, 0, 456, DateTimeKind.Utc);
        static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(30);
        const ulong RunId = 18446744073709551615;
        const ulong GuildA = 1000000000000000001, GuildB = 1000000000000000002;
        const ulong ChannelA = 2000000000000000001, ChannelB = 2000000000000000002;
        const ulong Revision = 1764000000;

        async Task<MariaDbFixture> StartAsync()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.PostingTables);
            return db;
        }

        static BaselineRow Baseline(string source) => new()
        {
            source = source,
            taken_at = Noon,
            taken_by_run = RunId,
            reason = BaselineReason.First,
            item_count = 3,
            baseline_time = source == PostSource.Maps ? Noon.AddDays(-30) : null,
            high_id = source == PostSource.Submissions ? 9 : null,
            checked_at = Noon,
            checked_by_run = RunId
        };

        static QueueItem Queued(ulong guildId, string source, ulong id, ulong revision = 0) => new()
        {
            guild_id = guildId,
            source = source,
            item_id = id,
            revision = revision,
            is_new = true,
            queued_at = Noon
        };

        static DeliveryClaim Claim(ulong key, ulong guildId, ulong channelId, string source, ulong queueId, DateTime at, IReadOnlyList<ShownUpdate>? shows = null) => new()
        {
            AttemptKey = key,
            RunId = RunId,
            RunStartedAt = Noon,
            GuildId = guildId,
            ChannelId = channelId,
            Source = source,
            Part = 1,
            AttemptedAt = at,
            QueueIds = [queueId],
            Shows = shows ?? []
        };

        // Step 7's statements, as one runbook gives them, from the line that begins the step to step 8's
        static List<string> Step7(string runbook)
        {
            var step = Regex.Match(runbook, @"^ *7\. If the dump has migration 5\b.*?^ *8\. ", RegexOptions.Multiline | RegexOptions.Singleline);
            Assert.True(step.Success, "The runbook has no step 7 for a dump with migration 5 before its step 8");

            return [.. Regex.Matches(step.Value, @"mariadb <BOT_DB from server\.conf> -e ""([^""]+)""").Select(match => match.Groups[1].Value)];
        }

        // Both runbooks give the same statements
        static List<string> ResetStatements()
        {
            var runbooks = new[] { "README.md", "README.txt" }.Select(name => Step7(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "deploy", name)))).ToList();

            Assert.NotEmpty(runbooks[0]);
            Assert.Equal(runbooks[0], runbooks[1]);
            return runbooks[0];
        }

        // In one session of root's in MinerBot's database, as `mariadb <BOT_DB>` runs them, never pooled, so no other test's
        // root connection is left in that database
        static async Task ResetAsync(MariaDbFixture db)
        {
            await using var session = new MySqlConnection(new MySqlConnectionStringBuilder(db.RootConnection) { Database = MariaDbFixture.BotDatabase, Pooling = false }.ConnectionString);

            foreach (var statement in ResetStatements())
                await session.ExecuteAsync(new CommandDefinition(statement, cancellationToken: TestContext.Current.CancellationToken)).WaitAsync(CallLimit, TestContext.Current.CancellationToken);
        }

        // What a dump taken while the bot posted holds: every source's baseline; for server A, armor 11 waiting, lobby 31
        // waiting again after its message failed, and armor 12 being sent; for server B, armor 13 that went, then armor 12
        // in doubt, and map 21's post in doubt with an update of it the post shows
        static async Task DumpAsync(NotificationData store)
        {
            await store.SaveDetectionAsync(new Detection
            {
                At = Noon,
                Baselines = [.. PostSource.All.Select(Baseline)],
                Queue = [Queued(GuildA, PostSource.Armor, 11), Queued(GuildA, PostSource.Lobbies, 31), Queued(GuildA, PostSource.Armor, 12),
                    Queued(GuildB, PostSource.Armor, 13), Queued(GuildB, PostSource.Armor, 12), Queued(GuildB, PostSource.Maps, 21, Revision)]
            });

            var open = await store.ListOpenAsync();
            ulong Row(ulong guildId, string source, ulong itemId) => open.Single(row => row.guild_id == guildId && row.source == source && row.item_id == itemId).id;

            await store.ClaimAsync(Claim(1, GuildA, ChannelA, PostSource.Lobbies, Row(GuildA, PostSource.Lobbies, 31), Noon.AddSeconds(1)));
            await store.SettleAsync(new Settlement
            {
                AttemptKey = 1,
                Outcome = DeliveryState.Failed,
                At = Noon.AddSeconds(2),
                Reason = DeliveryReasons.DiscordPermissions,
                HttpStatus = 403,
                DiscordCode = 50013,
                Counts = true,
                NextTryAt = Noon.AddMinutes(5),
                WaitingOn = 9
            });

            await store.ClaimAsync(Claim(2, GuildB, ChannelB, PostSource.Armor, Row(GuildB, PostSource.Armor, 13), Noon.AddSeconds(3)));
            await store.SettleAsync(new Settlement { AttemptKey = 2, Outcome = DeliveryState.Sent, At = Noon.AddSeconds(4), MessageId = 5000000000000000001 });

            await store.ClaimAsync(Claim(3, GuildB, ChannelB, PostSource.Armor, Row(GuildB, PostSource.Armor, 12), Noon.AddSeconds(5)));
            await store.SettleAsync(new Settlement { AttemptKey = 3, Outcome = DeliveryState.InDoubt, At = Noon.AddSeconds(20), Reason = DeliveryReasons.NoAnswer });

            await store.ClaimAsync(Claim(4, GuildB, ChannelB, PostSource.Maps, Row(GuildB, PostSource.Maps, 21), Noon.AddSeconds(21), [new ShownUpdate(21, Revision + 3600)]));
            await store.SettleAsync(new Settlement { AttemptKey = 4, Outcome = DeliveryState.InDoubt, At = Noon.AddSeconds(22), Reason = DeliveryReasons.NoAnswer, HttpStatus = 503 });

            await store.ClaimAsync(Claim(5, GuildA, ChannelA, PostSource.Armor, Row(GuildA, PostSource.Armor, 12), Noon.AddSeconds(23)));
        }

        // The queue and the messages as they're stored
        async Task<(List<QueueRow> Queue, List<DeliveryRow> Messages)> RowsAsync() => (
            await fixture.RootQueryAsync<QueueRow>($"SELECT * FROM {MariaDbFixture.QueueTable} ORDER BY id"),
            await fixture.RootQueryAsync<DeliveryRow>($"SELECT * FROM {MariaDbFixture.DeliveryTable} ORDER BY id"));

        // But for when each was settled, which the bot's settle takes from its own clock and the runbook from MariaDB's
        static List<QueueRow> Untimed(IEnumerable<QueueRow> rows) => [.. rows.Select(row => row with { settled_at = null })];
        static List<DeliveryRow> Untimed(IEnumerable<DeliveryRow> rows) => [.. rows.Select(row => row with { settled_at = null })];

        // After the reset the bot's first run finds no baseline, nothing waiting and nothing to look for: what waited is
        // given up, and each message being sent or in doubt is as the bot's own settle of one it can't find out about leaves
        // it, unconfirmed, with its rows and the update its post shows sent. Run again, it changes nothing.
        [Fact]
        public async Task ResetLeavesNothingToPostAgain()
        {
            var db = await StartAsync();
            var store = db.Notifications();

            // The bot's own settle of each open message, at a look that couldn't tell
            await DumpAsync(store);

            foreach (var open in (await store.LoadAsync(new PostingLoad())).Unsettled)
                Assert.True(await store.SettleAsync(new Settlement { AttemptKey = open.attempt_key, Outcome = DeliveryState.Unconfirmed, At = Noon.AddHours(1) }));

            var settled = await RowsAsync();

            // The same dump, restored, and reset as the runbooks say
            await db.ClearAsync(MariaDbFixture.PostingTables);
            await DumpAsync(store);
            var dumped = await RowsAsync();

            await ResetAsync(db);

            var state = await store.LoadAsync(new PostingLoad());
            Assert.Empty(state.Baselines);
            Assert.Empty(state.Open);
            Assert.Empty(state.Unsettled);

            var reset = await RowsAsync();
            var waited = dumped.Queue.Where(row => row.state == QueueState.Pending).Select(row => row.id).ToHashSet();

            Assert.Equal([(GuildA, PostSource.Armor, 11UL), (GuildA, PostSource.Lobbies, 31UL)],
                dumped.Queue.Where(row => waited.Contains(row.id)).Select(row => (row.guild_id, row.source, row.item_id)));
            Assert.All(reset.Queue.Where(row => waited.Contains(row.id)), row => Assert.Equal((QueueState.Dropped, (string?)DeliveryReasons.TooOld), (row.state, row.reason)));
            Assert.Equal(Untimed(settled.Queue.Where(row => !waited.Contains(row.id))), Untimed(reset.Queue.Where(row => !waited.Contains(row.id))));
            Assert.Equal(Untimed(settled.Messages), Untimed(reset.Messages));

            Assert.Equal([(GuildB, PostSource.Maps, 21UL, Revision + 3600, QueueState.Sent)],
                reset.Queue.Where(row => !row.is_new).Select(row => (row.guild_id, row.source, row.item_id, row.revision, row.state)));
            Assert.Equal([(1UL, DeliveryState.Failed), (2UL, DeliveryState.Sent), (3UL, DeliveryState.Unconfirmed), (4UL, DeliveryState.Unconfirmed), (5UL, DeliveryState.Unconfirmed)],
                reset.Messages.Select(row => (row.attempt_key, row.state)));
            Assert.All(reset.Messages, row => Assert.NotNull(row.settled_at));
            Assert.All(reset.Queue, row => Assert.NotNull(row.settled_at));

            await ResetAsync(db);

            var again = await RowsAsync();
            Assert.Equal(reset.Queue, again.Queue);
            Assert.Equal(reset.Messages, again.Messages);
        }
    }
}
