using DataAccessLibrary.Posting;
using Microsoft.Extensions.DependencyInjection;
using MinerBot_2._0.Posting;
using MinerBot_2._0.Requests;
using MinerBot_2._0.Status;
using static MinerBot.Tests.Posts;

namespace MinerBot.Tests
{
    // What the notification run takes as already posted, so that no start floods a server: each source's first good read,
    // and a new one after an older bot, which keeps no record, posted since this bot last checked
    [Collection("Discord")]
    public class PostingBaselineTests
    {
        static List<(string Kind, string? Detail)> Events(FakeDiscord discord, string kind) =>
            discord.Recorder.PeekEvents(1000).Where(row => row.kind == kind).Select(row => (row.kind, row.detail)).ToList();

        static List<string?> Baselines(FakeDiscord discord) => Events(discord, BotEvents.PostsBaseline).Select(row => row.Detail).ToList();

        // Every item of the first good read is taken as already posted, per source, and nothing is sent
        [Fact]
        public async Task FirstRunTakesABaselineAndPostsNothing()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.Lobbies.Add(Lobby(1));

            Assert.Equal("took the first baseline of armor, maps and lobbies (2, 5 and 1 items) and posted nothing", await discord.RunNotificationJobAsync());

            Assert.DoesNotContain(SendStep, discord.StepNames);
            Assert.Empty(discord.Store.Queue);
            Assert.Equal([(PostSource.Armor, 1ul), (PostSource.Armor, 2), (PostSource.Lobbies, 1), (PostSource.Maps, 1), (PostSource.Maps, 2), (PostSource.Maps, 3),
                (PostSource.Maps, 4), (PostSource.Maps, 5)], discord.Store.Seen.Select(row => (row.source, row.item_id)));
            Assert.All(discord.Store.Seen, row => Assert.Equal(SeenState.Baseline, row.state));
            Assert.Equal(["armor: 2 items taken as already posted, the first baseline", "maps: 5 items taken as already posted, the first baseline",
                "lobbies: 1 item taken as already posted, the first baseline"], Baselines(discord));
            Assert.All(discord.Store.Baselines, row => Assert.Equal(BaselineReason.First, row.reason));
        }

        // Steam down at the first run doesn't stop armor's baseline, and each of the others takes its own at its first good
        // read, while armor found new meanwhile is posted
        [Fact]
        public async Task EachSourceTakesItsBaselineAtItsFirstGoodRead()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.Lobbies.Add(Lobby(1));
            discord.Steam.LobbiesAvailable = false;
            var maps = discord.Steam.LatestItems;
            discord.Steam.LatestItems = new();

            Assert.Equal("took the first baseline of armor (2 items) and posted nothing", await discord.RunNotificationJobAsync());
            Assert.Equal([PostSource.Armor], discord.Store.Baselines.Select(row => row.source));

            discord.Steam.LobbiesAvailable = true;
            discord.Steam.LatestItems = maps;
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));

            Assert.Equal("took the first baseline of maps and lobbies (5 and 1 items); 1 armor: posted in 1 of 1 server", await discord.RunNotificationJobAsync());
            Assert.Equal([ArmorTitle], SentTitles(discord));
            Assert.Equal(3, Baselines(discord).Count);
        }

        // Steam's answer with no lobbies is a good read, which takes a baseline of none, so the next lobby is posted
        [Fact]
        public async Task EmptyLobbyAnswerIsAGoodFirstRead()
        {
            await using var discord = await StartWithBaselineAsync();

            Assert.Equal(0u, discord.Store.Baselines.Single(row => row.source == PostSource.Lobbies).item_count);

            discord.Steam.Lobbies.Add(Lobby(NewId));
            await discord.RunNotificationJobAsync();

            Assert.Equal([LobbyTitle], SentTitles(discord));
        }

        // The maps' baseline reads twenty deep and records every map in it; the runs after it read five
        [Fact]
        public async Task MapBaselineReadsTwentyDeep()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.LatestItems = Enumerable.Range(1, 25).Reverse().Select(FakeGetData.CreateItem).ToList();

            await discord.RunNotificationJobAsync();

            Assert.Equal((PostingLimits.DeepMaps, SteamWebAPIAccess.GetData.MaxQueryPages), discord.Steam.LatestAsked);
            Assert.Equal(Enumerable.Range(6, 20).Select(id => (ulong)id), discord.Store.Seen.Where(row => row.source == PostSource.Maps).Select(row => row.item_id));
            Assert.Equal(20u, discord.Store.Baselines.Single(row => row.source == PostSource.Maps).item_count);

            await discord.RunNotificationJobAsync();

            Assert.Equal((PostingLimits.LatestMaps, SteamWebAPIAccess.GetData.MaxQueryPages), discord.Steam.LatestAsked);
        }

        // Steam now and then ranks an old map far above its place. At the first read that one drags the baseline's time back
        // by weeks, so a map of the day under it would look new to a later deep read, though it was there at the first. The
        // baseline records every map of its twenty, so none of them is posted after a gap.
        [Fact]
        public async Task OutOfPlaceOldMapAtTheBaselineDoesNotRepostTheDaysMaps()
        {
            await using var discord = await FakeDiscord.StartAsync();
            var day = Enumerable.Range(NewId, 19).Reverse().Select(NewMap).ToList();
            var old = FakeGetData.CreateItem(1);
            day.Insert(1, old);
            discord.Steam.LatestItems = day;

            await discord.RunNotificationJobAsync();

            Assert.Equal(old.TimeUpdated, discord.Store.Baselines.Single(row => row.source == PostSource.Maps).baseline_time);

            // A gap, so the next read is twenty deep again
            discord.MoveClock(TimeSpan.FromMinutes(30));
            await discord.RunNotificationJobAsync();

            Assert.Equal(PostingLimits.DeepMaps, discord.Steam.LatestAsked!.Value.Maps);
            Assert.Empty(SentTitles(discord));
            Assert.Empty(discord.Store.Queue);
        }

        // ./rollback.sh bot puts back a bot that keeps no record. What it posted can't be told apart, so a forward deploy's
        // first run that finds its runs takes a new baseline of every source from its read, and what this bot had queued
        // before the rollback stays and is posted: the older bot's own first read took it as already posted.
        [Fact]
        public async Task OlderBotsRunsMeanANewBaselineAndTheQueueStays()
        {
            await using var before = await StartWithBaselineAsync();

            // An armor found and queued, waiting for its server, when the rollback came
            before.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            await before.MakeGuildUnavailableAsync();
            await before.RunNotificationJobAsync();

            var since = before.Store.Baselines.Max(row => row.checked_at);
            before.Store.AddJobRun(RequestActions.NotificationJob, since.AddMinutes(30), "ok", null);

            // What the older bot found and may have posted meanwhile
            before.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId + 1));
            before.Steam.LatestItems.Insert(0, NewMap(NewId));
            before.Steam.Lobbies.Add(Lobby(NewId));

            await using var discord = await before.RestartAsync(TimeSpan.FromHours(2));

            Assert.Equal($"took a new baseline of armor, maps and lobbies (4, 6 and 1 items), because an older bot posted since {since:yyyy-MM-dd HH:mm} UTC; 1 armor: posted in 1 of 1 server",
                await discord.RunNotificationJobAsync());

            var sent = Assert.Single(Sends(discord));
            Assert.Contains("Armor 100", sent.EmbedDescriptions.Single());
            Assert.DoesNotContain("Armor 101", sent.EmbedDescriptions.Single());
            Assert.Equal([$"armor: 4 items taken as already posted, because an older bot posted since {since:yyyy-MM-dd HH:mm} UTC",
                $"maps: 6 items taken as already posted, because an older bot posted since {since:yyyy-MM-dd HH:mm} UTC",
                $"lobbies: 1 item taken as already posted, because an older bot posted since {since:yyyy-MM-dd HH:mm} UTC"], Baselines(discord));
            Assert.All(discord.Store.Baselines, row => Assert.Equal(BaselineReason.OlderBot, row.reason));

            // Once a process: the next run takes no baseline and posts nothing
            Assert.Equal("nothing new to post", await discord.RunNotificationJobAsync());
            Assert.Single(Sends(discord));
            Assert.Equal(3, Baselines(discord).Count);
        }

        // Each source is checked against its own last check: one whose new baseline waited for a good read, because Steam was
        // down at the first start after the rollback, still takes it after a second restart, though armor's check is newer
        // than the older bot's runs by then
        [Fact]
        public async Task SourceWhoseNewBaselineWaitedForItsReadTakesItAfterARestart()
        {
            await using var before = await StartWithBaselineAsync();
            before.Store.AddJobRun(RequestActions.NotificationJob, before.Store.Baselines.Max(row => row.checked_at).AddMinutes(25), "ok", null);

            // What the older bot found, and may have posted
            before.Steam.LatestItems.Insert(0, NewMap(NewId));
            before.Steam.Lobbies.Add(Lobby(NewId));

            await using var steamDown = await before.RestartAsync(TimeSpan.FromHours(1));
            steamDown.Steam.Failure = new TimeoutException("Steam timed out");
            steamDown.Steam.LobbiesAvailable = false;

            Assert.StartsWith("took a new baseline of armor (2 items)", await steamDown.RunNotificationJobAsync());

            await using var discord = await steamDown.RestartAsync(TimeSpan.FromMinutes(5));
            discord.Steam.LobbiesAvailable = true;

            Assert.StartsWith("took a new baseline of maps and lobbies (6 and 1 items)", await discord.RunNotificationJobAsync());
            Assert.DoesNotContain(SendStep, discord.StepNames);
            Assert.All(discord.Store.Baselines, row => Assert.Equal(BaselineReason.OlderBot, row.reason));
        }

        // An armor approved again while the older bot ran may be one it posted, so the new baseline takes it as posted, and
        // what was withdrawn of it isn't reopened
        [Fact]
        public async Task ArmorApprovedAgainUnderAnOlderBotIsNotReopened()
        {
            await using var before = await StartWithBaselineAsync();
            var armor = FakeArmorData.CreateArmor(NewId);
            before.Armor.AllArmor.Add(armor);
            await before.MakeGuildUnavailableAsync();
            await before.RunNotificationJobAsync();

            before.Armor.AllArmor.Remove(armor);
            await before.MakeGuildAvailableAsync();
            await before.RunNotificationJobAsync();

            before.Store.AddJobRun(RequestActions.NotificationJob, before.Store.Baselines.Max(row => row.checked_at).AddMinutes(5), "ok", null);
            before.Armor.AllArmor.Add(armor);

            await using var discord = await before.RestartAsync(TimeSpan.FromHours(1));
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            Assert.DoesNotContain(SendStep, discord.StepNames);
            Assert.Equal(SeenState.Baseline, Seen(discord, PostSource.Armor, NewId)!.state);
            Assert.Equal(QueueState.Dropped, Assert.Single(discord.Store.Queue).state);
        }

        // A PR 9 bot's run that lost maps that waited has that as its detail, asked for or not, and counts as a run too
        [Theory]
        [InlineData("1 map that waited wasn't in Steam's latest list any more, so it wasn't posted: it was hidden meanwhile, or more maps were updated while posts waited than the read reaches")]
        [InlineData("asked for on request 12: 3 maps that waited weren't in Steam's latest list any more, so they weren't posted: it was hidden meanwhile")]
        [InlineData("asked for on request 12")]
        public async Task OlderBotsLostMapsDetailCountsAsARun(string detail)
        {
            await using var before = await StartWithBaselineAsync();
            before.Store.AddJobRun(RequestActions.NotificationJob, before.Store.Baselines.Max(row => row.checked_at).AddMinutes(5), "ok", detail);
            before.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));

            await using var discord = await before.RestartAsync(TimeSpan.FromHours(1));

            Assert.StartsWith("took a new baseline of armor, maps and lobbies", await discord.RunNotificationJobAsync());
            Assert.DoesNotContain(SendStep, discord.StepNames);
        }

        // Every run of this bot says what it did, asked for or not, so the next start never takes them for an older bot's
        [Fact]
        public async Task RunsOfThisBotNeverLookOlder()
        {
            await using var before = await FakeDiscord.StartAsync();
            var details = new List<string>
            {
                await before.RunNotificationJobAsync(),
                await before.RunNotificationJobAsync()
            };

            AddNewItems(before);
            details.Add(await before.RunNotificationJobAsync());

            before.Services.GetRequiredService<PostsPause>().Load(true, "Maintenance", before.Clock.UtcNow.UtcDateTime);
            details.Add(await before.RunNotificationJobAsync());

            before.Services.GetRequiredService<PostsPause>().Load(false, null, before.Clock.UtcNow.UtcDateTime);
            before.FailStep = SendStep;
            before.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId + 1));
            details.Add(await before.RunNotificationJobAsync());
            before.FailStep = null;

            Assert.All(details, detail => Assert.False(string.IsNullOrEmpty(detail)));

            foreach (var detail in details)
            {
                before.Store.AddJobRun(RequestActions.NotificationJob, before.Clock.UtcNow.UtcDateTime, "ok", detail);
                before.Store.AddJobRun(RequestActions.NotificationJob, before.Clock.UtcNow.UtcDateTime, "ok", $"asked for on request 7: {detail}");
            }

            Assert.Equal(0, await before.Store.CountOlderBotRunsAsync(RequestActions.NotificationJob, DateTime.UnixEpoch));

            await using var discord = await before.RestartAsync(TimeSpan.FromMinutes(1));
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId + 2));

            // The armor refused before the restart, and the new one
            Assert.Equal("2 armor: posted in 1 of 1 server", await discord.RunNotificationJobAsync());
            Assert.Empty(Baselines(discord));
        }
    }
}
