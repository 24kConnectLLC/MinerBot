using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Posting;
using Microsoft.Extensions.DependencyInjection;
using MinerBot.Shared.Delivery;
using MinerBot_2._0.Models;
using MinerBot_2._0.Posting;
using MinerBot_2._0.Requests;
using static MinerBot.Tests.Posts;

namespace MinerBot.Tests
{
    // The dashboard's switches for armor, map and lobby posts, read at every run with no cache: a server switched off for a
    // kind is owed none of it, no row means every kind is on, and an empty channel is off whatever the switch says
    [Collection("Discord")]
    public class SwitchTests
    {
        // A server whose kind is off when an item is found gets no row for it, so nothing is recorded or tried for it
        [Fact]
        public async Task SwitchedOffKindGetsNoPostsAndNoQueueRows()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Store.SetSwitches(Switches(FakeDiscord.GuildId, armor: false));
            AddNewItems(discord);

            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal([NewMapTitle, LobbyTitle], SentTitles(discord));
            Assert.Empty(Queue(discord, FakeDiscord.GuildId, PostSource.Armor));
            Assert.Equal(SeenState.Queued, Seen(discord, PostSource.Armor, NewId)!.state);
            Assert.DoesNotContain("armor notification", discord.ConsoleText);
        }

        [Fact]
        public async Task NoSwitchRowMeansEverythingOn()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Store.SetSwitches(Switches(FakeDiscord.JoinedGuildId, armor: false, maps: false, lobbies: false));
            AddNewItems(discord);

            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle, NewMapTitle, LobbyTitle], SentTitles(discord));
        }

        // A channel of 0 is the server's own off, which the switch can't turn on
        [Fact]
        public async Task EmptyChannelIsOffWhateverTheSwitch()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Guilds.AllGuilds[0].armor_channel_id = Guild.ChannelOff;
            discord.Store.SetSwitches(Switches(FakeDiscord.GuildId));
            AddNewItems(discord);

            await discord.RunNotificationJobAsync();

            Assert.Equal([NewMapTitle, LobbyTitle], SentTitles(discord));
            Assert.Empty(Queue(discord, FakeDiscord.GuildId, PostSource.Armor));
        }

        // Turned on again, a kind gets only what's found from then: what was found while it was off was never owed it
        [Fact]
        public async Task SwitchedOnLaterGetsOnlyWhatIsFoundFromThen()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Store.SetSwitches(Switches(FakeDiscord.GuildId, armor: false));
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            await discord.RunNotificationJobAsync();

            discord.Store.SetSwitches(Switches(FakeDiscord.GuildId) with { version = 2 });
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId + 1));
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            var sent = Assert.Single(Sends(discord)).EmbedDescriptions.Single();
            Assert.Contains("Armor 101", sent);
            Assert.DoesNotContain("Armor 100", sent);
        }

        // Switched off while items wait for the server: at the next run they're dropped there, with the reason, and never sent
        [Fact]
        public async Task SwitchedOffWhilePendingDropsWhatWaits()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            await discord.MakeGuildUnavailableAsync();
            await discord.RunNotificationJobAsync();

            discord.Store.SetSwitches(Switches(FakeDiscord.GuildId, armor: false));
            await discord.MakeGuildAvailableAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal([NewMapTitle, LobbyTitle], SentTitles(discord));
            Assert.Equal((QueueState.Dropped, DeliveryReasons.SwitchedOff), Queue(discord, FakeDiscord.GuildId, PostSource.Armor).Select(row => (row.state, row.reason)).Single());
        }

        // Switched off, or the server's channel set to 0, while a post of the kind there isn't settled: what waits behind that
        // post is dropped at the next run all the same, with the reason, and the post itself is left to its look. So when the
        // kind is on again before the look ends, nothing found before the off goes there. Fails if the post that isn't settled
        // holds back the off, so what waited is posted once the kind is on again.
        [Theory]
        [InlineData(DeliveryReasons.SwitchedOff)]
        [InlineData(DeliveryReasons.ChannelOff)]
        public async Task SwitchedOffBehindAPostInDoubtDropsWhatWaits(string reason)
        {
            await using var discord = await StartWithBaselineAsync();

            // A channel where the bot may post but can't read the history, so the look at the post Discord didn't answer can't
            // tell for three runs
            discord.Guilds.AllGuilds = [FakeGuildData.CreateGuild(FakeDiscord.AnnouncementChannelId)];
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.Unanswered[FakeDiscord.AnnouncementChannelId] = true;
            await discord.RunNotificationJobAsync();
            discord.Unanswered.Clear();

            // Found while the server is owed armor, so it's queued there, and waits behind the post in doubt
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId + 1));
            await discord.RunNotificationJobAsync();

            Assert.Equal(QueueState.Pending, Queue(discord, FakeDiscord.GuildId, PostSource.Armor).Single(row => row.item_id == NewId + 1).state);

            // Off for one run, then on again before the third look settles the post in doubt
            ArmorOn(discord, reason, on: false);
            var off = await discord.RunNotificationJobAsync();
            ArmorOn(discord, reason, on: true);
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal([(NewId, QueueState.Sent, (string?)DeliveryReasons.Unconfirmed), (NewId + 1, QueueState.Dropped, reason)],
                Queue(discord, FakeDiscord.GuildId, PostSource.Armor).Select(row => (row.item_id, row.state, row.reason)));
            Assert.Contains("Armor 100", Assert.Single(Sends(discord)).EmbedDescriptions.Single());
            Assert.Equal(PostingTexts.NothingNew, off);

            static void ArmorOn(FakeDiscord discord, string reason, bool on)
            {
                if (reason == DeliveryReasons.SwitchedOff)
                    discord.Store.SetSwitches(Switches(FakeDiscord.GuildId, armor: on) with { version = on ? 2u : 1u });
                else
                    discord.Guilds.AllGuilds[0].armor_channel_id = on ? FakeDiscord.AnnouncementChannelId : Guild.ChannelOff;
            }
        }

        // Switched off and on again while posts are paused: a paused run drops what waits for a server that's off as any run
        // does, so the resume posts nothing found before the off. Fails if a paused run leaves it, so it goes at the resume.
        [Fact]
        public async Task SwitchedOffAndOnDuringAPauseDropsWhatWaitedFromBefore()
        {
            await using var discord = await StartWithBaselineAsync();
            var pause = discord.Services.GetRequiredService<PostsPause>();
            pause.Load(true, "Maintenance", discord.Clock.UtcNow.UtcDateTime);
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            await discord.RunNotificationJobAsync();

            Assert.Equal(QueueState.Pending, Assert.Single(Queue(discord, FakeDiscord.GuildId, PostSource.Armor)).state);

            discord.Store.SetSwitches(Switches(FakeDiscord.GuildId, armor: false));
            var off = await discord.RunNotificationJobAsync();
            discord.Store.SetSwitches(Switches(FakeDiscord.GuildId) with { version = 2 });
            pause.Load(false, null, discord.Clock.UtcNow.UtcDateTime);
            await discord.RunNotificationJobAsync();

            Assert.Empty(Sends(discord));
            Assert.Equal((QueueState.Dropped, DeliveryReasons.SwitchedOff), Queue(discord, FakeDiscord.GuildId, PostSource.Armor).Select(row => (row.state, row.reason)).Single());
            Assert.Equal("posts are paused, and nothing waits", off);
        }

        // Switched off and on again before the bot has read whether posts are paused, as while MinerBot's database doesn't
        // answer that read at a start: a run that doesn't know yet drops what waits for a server that's off, as a paused run
        // does, so the first run after the read posts nothing found before the off. Fails if only a run that knows posts are
        // paused drops it.
        [Fact]
        public async Task SwitchedOffAndOnBeforeThePauseIsReadDropsWhatWaitedFromBefore()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Settings["ConnectionStrings:BotConnection"] = RequestBot.Connection;
            await discord.RunNotificationJobAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            await discord.RunNotificationJobAsync();

            Assert.Equal(QueueState.Pending, Assert.Single(Queue(discord, FakeDiscord.GuildId, PostSource.Armor)).state);

            discord.Store.SetSwitches(Switches(FakeDiscord.GuildId, armor: false));
            var off = await discord.RunNotificationJobAsync();
            discord.Store.SetSwitches(Switches(FakeDiscord.GuildId) with { version = 2 });
            discord.Services.GetRequiredService<PostsPause>().Load(false, null, discord.Clock.UtcNow.UtcDateTime);
            await discord.RunNotificationJobAsync();

            Assert.Empty(Sends(discord));
            Assert.Equal((QueueState.Dropped, DeliveryReasons.SwitchedOff), Queue(discord, FakeDiscord.GuildId, PostSource.Armor).Select(row => (row.state, row.reason)).Single());
            Assert.Equal(PostingTexts.PauseUnknown, off);
        }

        // A drop MinerBot's database didn't take during a pause is logged, and what it was to drop waits, still counted, with
        // the drops after it, for the next run, which drops them all: the run doesn't fail over it. Fails if the failure fails
        // the run, or if the run goes on to the drops after it.
        [Fact]
        public async Task DropTheDatabaseDidNotTakeDuringAPauseWaitsForTheNextRun()
        {
            var store = new FailingNotificationData(new());
            await using var discord = await FakeDiscord.StartAsync(store: store);
            await discord.RunNotificationJobAsync();
            discord.Services.GetRequiredService<PostsPause>().Load(true, "Maintenance", discord.Clock.UtcNow.UtcDateTime);
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.Steam.LatestItems.Insert(0, FakeGetData.CreateItem(NewId));
            await discord.RunNotificationJobAsync();

            discord.Store.SetSwitches(Switches(FakeDiscord.GuildId, armor: false, maps: false));
            store.FailNext(nameof(FailingNotificationData.DropAsync), new TimeoutException("The database didn't answer within 15 seconds"));

            Assert.Equal("posts are paused: 1 armor and 1 map wait for 1 server", await discord.RunNotificationJobAsync());
            Assert.Contains($"MinerBot's database didn't take what's dropped of guild {FakeDiscord.GuildId}'s armor posts (TimeoutException)", discord.ConsoleText);
            Assert.Equal([(PostSource.Armor, QueueState.Pending), (PostSource.Maps, QueueState.Pending)], discord.Store.Queue.Select(row => (row.source, row.state)));

            Assert.Equal("posts are paused, and nothing waits", await discord.RunNotificationJobAsync());
            Assert.All(discord.Store.Queue, row => Assert.Equal((QueueState.Dropped, DeliveryReasons.SwitchedOff), (row.state, row.reason)));
        }

        // The switches come with the rest of the record, and a read that fails holds every post of the run: no row means on,
        // and a read that failed must never read as on
        [Fact]
        public async Task UnreadableSwitchesHoldEveryPost()
        {
            var store = new FailingNotificationData(new());
            await using var discord = await FakeDiscord.StartAsync(store: store);
            await discord.RunNotificationJobAsync();

            // An armor that waits for the server, which is then switched off for armor
            AddNewItems(discord);
            await discord.MakeGuildUnavailableAsync();
            await discord.RunNotificationJobAsync();
            discord.Store.SetSwitches(Switches(FakeDiscord.GuildId, armor: false, maps: false, lobbies: false));
            await discord.MakeGuildAvailableAsync();

            store.FailAlways(nameof(FailingNotificationData.LoadAsync), new TimeoutException("The database didn't answer within 15 seconds"));

            var failed = await Assert.ThrowsAsync<JobFailedException>(discord.RunNotificationJobAsync);

            Assert.Equal("posts wait: MinerBot's database didn't answer (TimeoutException)", failed.Message);
            Assert.DoesNotContain(SendStep, discord.StepNames);
            Assert.All(discord.Store.Queue, row => Assert.Equal(QueueState.Pending, row.state));

            store.Heal();
            await discord.RunNotificationJobAsync();

            Assert.DoesNotContain(SendStep, discord.StepNames);
            Assert.All(discord.Store.Queue, row => Assert.Equal(DeliveryReasons.SwitchedOff, row.reason));
        }

        // Announcements are a server admin's own, with /admin announce: no switch stops them, and the job never posts any
        [Fact]
        public async Task AnnouncementsHaveNoSwitch()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Guilds.Guild = new Guild { guild_id = FakeDiscord.GuildId, announcements_channel_id = FakeDiscord.AnnouncementChannelId };
            discord.Guilds.AllGuilds[0].announcements_channel_id = FakeDiscord.AnnouncementChannelId;
            discord.Store.SetSwitches(Switches(FakeDiscord.GuildId, armor: false, maps: false, lobbies: false));
            AddNewItems(discord);

            await discord.RunNotificationJobAsync();
            await discord.SendSlashCommandAsync("admin", "announce", """{"name":"message","type":3,"value":"Hello"}""");

            Assert.Equal("Hello", discord.Single(SendStep).Content);
            Assert.Equal(FakeDiscord.AnnouncementChannelId, discord.Single(SendStep).SentTo);
            Assert.Empty(discord.Store.Queue);
        }
    }
}
