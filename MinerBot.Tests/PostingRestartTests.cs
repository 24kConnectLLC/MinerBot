using DataAccessLibrary.Posting;
using Microsoft.Extensions.DependencyInjection;
using MinerBot.Shared.Delivery;
using MinerBot_2._0.Posting;
using MinerBot_2._0.Requests;
using static MinerBot.Tests.Posts;

namespace MinerBot.Tests
{
    // A restart, a deploy or a crash between two runs: a new process over the same MinerBot's database, which keeps what was
    // found and what each server is owed, so nothing is lost and nothing is posted twice
    [Collection("Discord")]
    public class PostingRestartTests
    {
        // Approved while the bot was down: found new at the first run after the start, as it would have been at any run, and
        // posted once
        [Fact]
        public async Task ArmorApprovedWhileTheBotIsDownIsPostedAfterTheStart()
        {
            await using var before = await StartWithBaselineAsync();

            before.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));

            await using var discord = await before.RestartAsync(TimeSpan.FromMinutes(10));
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle], SentTitles(discord));
            Assert.Contains("Armor 100", Sends(discord)[0].EmbedDescriptions.Single());
        }

        // Found and queued by the last run before the stop, which couldn't send it yet: the first run after the start posts it,
        // once
        [Fact]
        public async Task ArmorApprovedJustBeforeAStopIsPostedOnceAfterIt()
        {
            await using var before = await StartWithBaselineAsync();
            before.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            await before.MakeGuildUnavailableAsync();
            await before.RunNotificationJobAsync();

            Assert.Empty(SentTitles(before));

            await using var discord = await before.RestartAsync(TimeSpan.FromMinutes(1));
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle], SentTitles(discord));
            Assert.Equal(QueueState.Sent, Assert.Single(discord.Store.Queue).state);
        }

        // A pause from the very start: its runs take the baselines and queue what they find, so the first run after a restart
        // and the resume posts it, though that run would otherwise take the first baseline itself
        [Fact]
        public async Task WhatAPauseFoundIsPostedAfterARestartAndResume()
        {
            await using var before = await FakeDiscord.StartAsync();
            Pause(before, true);
            await before.RunNotificationJobAsync();

            before.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            await before.RunNotificationJobAsync();

            await using var discord = await before.RestartAsync(TimeSpan.FromMinutes(1));
            Pause(discord, true);
            await discord.RunNotificationJobAsync();

            Pause(discord, false);
            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle], SentTitles(discord));
            Assert.Contains("Armor 100", Sends(discord)[0].EmbedDescriptions.Single());
        }

        static void Pause(FakeDiscord discord, bool paused) =>
            discord.Services.GetRequiredService<PostsPause>().Load(paused, paused ? "Maintenance" : null, discord.Clock.UtcNow.UtcDateTime);

        // The map's last queueing is in MinerBot's database, so an update within a day of its post is held back after a
        // restart too, and posted once the day is over
        [Fact]
        public async Task MapCooldownSurvivesARestart()
        {
            await using var before = await StartWithBaselineAsync();
            var map = FakeGetData.CreateItem(NewId);
            before.Steam.LatestItems.Insert(0, map);
            await before.RunNotificationJobAsync();

            UpdateMap(before, map, day: NewId + 1);

            await using var discord = await before.RestartAsync(TimeSpan.FromHours(1));
            await discord.RunNotificationJobAsync();

            Assert.Empty(SentTitles(discord));

            discord.MoveClock(PostingLimits.Cooldown);
            await discord.RunNotificationJobAsync();

            Assert.Equal([UpdatedMapHeading], MapHeadings(discord));
        }

        // Maps added and updated while the bot was down, more than the five a run reads: the first run after the start reads
        // twenty, after the gap, and posts every one
        [Fact]
        public async Task MapsUpdatedDuringADowntimeAreReadDeepAndPosted()
        {
            await using var before = await StartWithBaselineAsync();

            for (var id = NewId; id < NewId + 8; id++)
                before.Steam.LatestItems.Insert(0, NewMap(id));

            await using var discord = await before.RestartAsync(TimeSpan.FromMinutes(20));
            await discord.RunNotificationJobAsync();

            Assert.Equal(PostingLimits.DeepMaps, discord.Steam.LatestAsked!.Value.Maps);
            Assert.Equal(Enumerable.Range(NewId, 8).Reverse().Select(id => $"Map {id}"), SentTitles(discord));

            await discord.RunNotificationJobAsync();

            Assert.Equal(PostingLimits.LatestMaps, discord.Steam.LatestAsked!.Value.Maps);
            Assert.Equal(8, SentTitles(discord).Count);
        }

        // After a long downtime the deep read reaches maps updated days before the newest: those aren't news any more, so
        // they're recorded and not posted
        [Fact]
        public async Task MapsOlderThanADayBeforeTheNewestAreNotPosted()
        {
            await using var before = await StartWithBaselineAsync();

            // A day and a half before the newest, and so old news, and one an hour before it, which is still news
            var newest = NewMap(NewId + 3);
            var older = NewMap(NewId + 1);
            older.TimeCreated = older.TimeUpdated = newest.TimeUpdated.AddHours(-36);
            var recent = NewMap(NewId + 2);
            recent.TimeCreated = recent.TimeUpdated = newest.TimeUpdated.AddHours(-1);

            before.Steam.LatestItems.InsertRange(0, [newest, recent, older]);

            await using var discord = await before.RestartAsync(TimeSpan.FromDays(3));
            await discord.RunNotificationJobAsync();

            Assert.Equal(["Map 103", "Map 102"], SentTitles(discord));
            Assert.Equal(SeenState.Baseline, Seen(discord, PostSource.Maps, NewId + 1)!.state);
            Assert.DoesNotContain(discord.Store.Queue, row => row.item_id == NewId + 1);
        }

        // Lobbies are kept too: one posted before the restart and still open after it isn't new to the next process
        [Fact]
        public async Task LobbyPostedBeforeARestartIsNotPostedAgain()
        {
            await using var before = await StartWithBaselineAsync();
            before.Steam.Lobbies.Add(Lobby(NewId));
            await before.RunNotificationJobAsync();

            Assert.Equal([LobbyTitle], SentTitles(before));

            await using var discord = await before.RestartAsync(TimeSpan.FromMinutes(1));
            discord.Steam.Lobbies.Add(Lobby(NewId + 1));
            await discord.RunNotificationJobAsync();

            var announced = Assert.Single(Sends(discord)).EmbedDescriptions.Single();
            Assert.Contains($"Host {NewId + 1}", announced);
            Assert.DoesNotContain($"Host {NewId}`", announced);
        }

        // Posted, taken down, and approved again after a restart: a server that got it never gets it again
        [Fact]
        public async Task PostedArmorApprovedAgainIsNeverPostedAgain()
        {
            await using var before = await StartWithBaselineAsync();
            var armor = FakeArmorData.CreateArmor(NewId);
            before.Armor.AllArmor.Add(armor);
            await before.RunNotificationJobAsync();

            before.Armor.AllArmor.Remove(armor);
            await before.RunNotificationJobAsync();

            await using var discord = await before.RestartAsync(TimeSpan.FromMinutes(1));
            await discord.RunNotificationJobAsync();

            discord.Armor.AllArmor.Add(armor);
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle], SentTitles(before));
            Assert.Empty(SentTitles(discord));
            Assert.Equal(QueueState.Sent, Assert.Single(discord.Store.Queue).state);
        }

        // Taken down before it reached a server, which drops it there as not approved, and approved again after a restart:
        // it goes to that server then
        [Fact]
        public async Task ArmorWithdrawnBeforeItsPostIsPostedWhenApprovedAgain()
        {
            await using var before = await StartWithBaselineAsync();
            var armor = FakeArmorData.CreateArmor(NewId);
            before.Armor.AllArmor.Add(armor);
            await before.MakeGuildUnavailableAsync();
            await before.RunNotificationJobAsync();

            before.Armor.AllArmor.Remove(armor);
            await before.MakeGuildAvailableAsync();
            await before.RunNotificationJobAsync();

            Assert.Equal((QueueState.Dropped, DeliveryReasons.NotApproved), before.Store.Queue.Select(row => (row.state, row.reason)).Single());

            await using var discord = await before.RestartAsync(TimeSpan.FromMinutes(1));
            discord.Armor.AllArmor.Add(armor);
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            Assert.Empty(SentTitles(before));
            Assert.Equal([ArmorTitle], SentTitles(discord));
            Assert.Equal(QueueState.Sent, Assert.Single(discord.Store.Queue).state);
        }
    }
}
