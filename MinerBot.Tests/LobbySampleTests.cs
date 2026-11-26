using System.Reflection;
using DataAccessLibrary.Status.Models;
using Microsoft.Extensions.DependencyInjection;
using MinerBot_2._0.Feed;
using MinerBot_2._0.Requests;
using MinerBot_2._0.Status;
using SteamWebAPIAccess;
using SteamWebAPIAccess.Models;

namespace MinerBot.Tests
{
    // The open lobbies at each notification run's read, as counts for the analytics: one sample a run, taken from the run's
    // own read before anything can end it, never from the website feed's, and kept in memory until StatusWriter writes it
    // beside the uptime samples. Counts only, never a lobby, a host, a map or a link, and none at all when Steam gave no answer.
    [Collection("Discord")]
    public class LobbySampleTests
    {
        // Made up, and never connected to: FakeStatusData stands in for the database
        const string Connection = "Server=127.0.0.1;Database=bot_db;Uid=minerbot-bot;Pwd=password";

        static LobbyInfo Lobby(ulong id, int players, int slots, uint version = GameVersions.Current, LobbyHostKind host = LobbyHostKind.Player)
        {
            var lobby = Posts.Lobby(id, version);
            lobby.CurrentPlayers = players;
            lobby.MaxPlayers = slots;
            lobby.HostKind = host;
            return lobby;
        }

        static ushort?[] Counts(LobbySample sample) =>
            [sample.lobbies, sample.players, sample.slots, sample.current_lobbies, sample.current_players, sample.full_lobbies, sample.gamenight_lobbies, sample.official_lobbies];

        static DateTime Minute(FakeDiscord discord)
        {
            var now = discord.Clock.UtcNow.UtcDateTime;
            return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        }

        [Fact]
        public async Task EachRunRecordsOneSampleWithCountsOnly()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.Lobbies.AddRange(
            [
                Lobby(1, players: 3, slots: 8),
                Lobby(2, players: 8, slots: 8),
                Lobby(3, players: 2, slots: 10, host: LobbyHostKind.Gamenight),
                Lobby(4, players: 5, slots: 16, version: 100, host: LobbyHostKind.Official),

                // A lobby whose slots Steam didn't give is never full
                Lobby(5, players: 0, slots: 0, version: 104)
            ]);

            await discord.RunNotificationJobAsync();
            var first = Minute(discord);
            discord.Steam.Lobbies.RemoveAt(0);
            await discord.RunNotificationJobAsync();

            var samples = discord.Recorder.PeekLobbySamples(10);

            Assert.Equal([first, first.AddMinutes(5)], samples.Select(sample => sample.minute));
            Assert.All(samples, sample => Assert.Equal(DateTimeKind.Utc, sample.minute.Kind));
            Assert.All(samples, sample => Assert.True(sample.steam_answered));

            // Every lobby of every version, its players and slots; the current version's lobbies and players; the full ones,
            // and the game's own Gamenight and Official hosts'
            Assert.Equal<ushort?>([5, 18, 42, 3, 13, 1, 1, 1], Counts(samples[0]));
            Assert.Equal<ushort?>([4, 15, 34, 2, 10, 1, 1, 1], Counts(samples[1]));
        }

        // A chart must never read an outage as an empty game: with no answer from Steam, or a read that failed, every count is
        // null. An answer with no lobby in it is an empty game.
        [Fact]
        public async Task SteamOutageIsASampleWithNoCounts()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.Lobbies.Add(Lobby(1, players: 3, slots: 8));

            discord.Steam.LobbiesAvailable = false;
            await discord.RunNotificationJobAsync();

            discord.Steam.LobbiesAvailable = true;
            discord.Steam.LobbyFailure = new HttpRequestException("Steam didn't answer");
            await discord.RunNotificationJobAsync();

            discord.Steam.LobbyFailure = null;
            discord.Steam.Lobbies.Clear();
            await discord.RunNotificationJobAsync();

            var samples = discord.Recorder.PeekLobbySamples(10);

            Assert.Equal([false, false, true], samples.Select(sample => sample.steam_answered));
            Assert.All(samples.Take(2), sample => Assert.All(Counts(sample), count => Assert.Null(count)));
            Assert.All(Counts(samples[2]), count => Assert.Equal((ushort?)0, count));
        }

        // Counts, a bool and the minute: nothing a person chose, such as a host's or a map's name, and no lobby's or host's ID
        [Fact]
        public void SampleHoldsNoNameOrId()
        {
            var properties = typeof(LobbySample).GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.Equal(["current_lobbies", "current_players", "full_lobbies", "gamenight_lobbies", "lobbies", "minute", "official_lobbies", "players", "slots", "steam_answered"],
                properties.Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.Equal(typeof(DateTime), Assert.Single(properties, property => property.Name == "minute").PropertyType);
            Assert.Equal(typeof(bool), Assert.Single(properties, property => property.Name == "steam_answered").PropertyType);
            Assert.All(properties.Where(property => property.Name is not ("minute" or "steam_answered")), property => Assert.Equal(typeof(ushort?), property.PropertyType));
            Assert.DoesNotContain(typeof(LobbySample).GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
                field => !field.Name.Contains("k__BackingField", StringComparison.Ordinal));
        }

        // The table's columns hold up to 65,535: a count above it is kept at that, never wrapped round, and a lobby's players
        // below 0 count as none
        [Fact]
        public void CountsAreKeptWithinWhatTheTableHolds()
        {
            var sample = LobbySamples.Of(new DateTime(2026, 11, 24, 15, 4, 59, 999, DateTimeKind.Utc),
                [Lobby(1, players: 40000, slots: 40000), Lobby(2, players: 40000, slots: 40000), Lobby(3, players: -5, slots: 8)]);

            Assert.Equal(new DateTime(2026, 11, 24, 15, 4, 0, DateTimeKind.Utc), sample.minute);
            Assert.Equal<ushort?>([3, 65535, 65535, 3, 65535, 2, 0, 0], Counts(sample));
        }

        // StatusWriter writes them beside the uptime samples, and while MinerBot's database is down they wait in memory, so a
        // database that's down never holds up the run, and costs no sample within the recorder's 6 hours
        [Fact]
        public async Task SamplesWaitInMemoryWhileTheDatabaseIsDown()
        {
            await using var discord = await FakeDiscord.StartAsync();
            var data = new FakeStatusData { Failure = new TimeoutException("The database didn't answer within 15 seconds") };
            var writer = new StatusWriter(discord.Recorder, data, BotRecorderTests.Settings(botConnection: Connection), discord.Clock, new RecordingLogger<StatusWriter>());

            await discord.RunNotificationJobAsync();
            await writer.TickAsync(TestContext.Current.CancellationToken);
            discord.Steam.Lobbies.Add(Lobby(1, players: 3, slots: 8));
            await discord.RunNotificationJobAsync();

            Assert.Equal(2, discord.Recorder.PeekLobbySamples(10).Count);
            Assert.Empty(data.LobbySamples);

            // Back, but for the lobby samples' own write, which fails after the rest of the tick went through
            data.Failure = null;
            data.LobbyFailure = new TimeoutException("The database didn't answer within 15 seconds");
            await writer.TickAsync(TestContext.Current.CancellationToken);

            Assert.NotEmpty(data.Statuses);
            Assert.Empty(data.LobbySamples);
            Assert.Equal(2, discord.Recorder.PeekLobbySamples(10).Count);

            data.LobbyFailure = null;
            discord.MoveClock(TimeSpan.FromMinutes(1));
            await writer.TickAsync(TestContext.Current.CancellationToken);

            Assert.Equal<ushort?>([0, 1], data.LobbySamples.Select(sample => sample.lobbies));
            Assert.Empty(discord.Recorder.PeekLobbySamples(10));
        }

        // A run asked for from the dashboard can come in the same minute as a scheduled one: both samples are keyed by the
        // minute's start, in the order taken, and MinerBot's database keeps the first of a minute
        // (BotStatusDataTests.LobbySamplesKeepTheFirstOfAMinute), so 288 samples a day at most
        [Fact]
        public async Task SecondRunInAMinuteKeepsTheFirstSample()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Clock.UtcNow = new DateTimeOffset(2026, 11, 24, 15, 4, 50, TimeSpan.Zero);

            await discord.RunNotificationJobAsync();
            discord.Steam.Lobbies.Add(Lobby(1, players: 3, slots: 8));
            await discord.RunNotificationJobAsync(TimeSpan.FromSeconds(5));

            var minute = new DateTime(2026, 11, 24, 15, 9, 0, DateTimeKind.Utc);
            var data = new FakeStatusData();
            var writer = new StatusWriter(discord.Recorder, data, BotRecorderTests.Settings(botConnection: Connection), discord.Clock, new RecordingLogger<StatusWriter>());
            await writer.TickAsync(TestContext.Current.CancellationToken);

            Assert.Equal<(DateTime, ushort?)>([(minute, 0), (minute, 1)], data.LobbySamples.Select(sample => (sample.minute, sample.lobbies)));
        }

        // The sample comes before the pause and Discord's check, so a paused run, or one before Discord is ready, samples too
        [Fact]
        public async Task PausedRunsStillSample()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Services.GetRequiredService<PostsPause>().Load(true, "Maintenance", discord.Clock.UtcNow.UtcDateTime);
            discord.Steam.Lobbies.Add(Lobby(1, players: 3, slots: 8));

            Assert.StartsWith("posts are paused", await discord.RunNotificationJobAsync());
            Assert.Equal((ushort?)1, Assert.Single(discord.Recorder.PeekLobbySamples(10)).lobbies);
            Assert.Empty(Posts.Sends(discord));

            await using var waiting = await FakeDiscord.StartAsync(holdReady: true);
            waiting.Steam.Lobbies.Add(Lobby(1, players: 3, slots: 8));

            Assert.Equal("Discord hasn't sent the bot's servers yet, so this run found and posted nothing", await waiting.RunNotificationJobAsync());
            Assert.Equal((ushort?)1, Assert.Single(waiting.Recorder.PeekLobbySamples(10)).lobbies);

            await waiting.ReleaseReadyAsync();
        }

        // The website feed reads the lobby list every 30 seconds, and keeps its own: it takes no sample, so the samples stay one
        // a notification run, whatever the feed does
        [Fact]
        public async Task FeedReadsTakeNoSample()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.Lobbies.Add(Lobby(1, players: 3, slots: 8));
            var feed = ActivatorUtilities.CreateInstance<WebsiteFeedWriter>(discord.Services, new FakeFeedData());

            for (var tick = 0; tick < 3; tick++)
            {
                await feed.LobbyTickAsync(TestContext.Current.CancellationToken);
                await feed.MapTickAsync(TestContext.Current.CancellationToken);
            }

            Assert.Equal(3, discord.Steam.LobbyReads.Count(frequent => frequent));
            Assert.Empty(discord.Recorder.PeekLobbySamples(10));

            await discord.RunNotificationJobAsync();

            Assert.Equal((ushort?)1, Assert.Single(discord.Recorder.PeekLobbySamples(10)).lobbies);
        }
    }
}
