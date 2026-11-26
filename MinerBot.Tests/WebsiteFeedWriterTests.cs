using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using DataAccessLibrary.Feed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MinerBot_2._0.Feed;
using MinerBot_2._0.Jobs;
using MinerBot_2._0.Requests;
using MySql.Data.MySqlClient;
using SteamWebAPIAccess;
using SteamWebAPIAccess.Models;

namespace MinerBot.Tests
{
    // The feed the bot writes for the website's Lobbies and Maps pages, with FakeGetData standing in for Steam and
    // FakeFeedData for the website's tables. A good read replaces the lobbies or adds to the maps, a read where Steam gave
    // no answer changes none of them, and a database that fails, or has no tables yet, costs only the feed. The ticks are
    // called one by one here, on a clock the test moves, except where the loop itself is what's tested.
    public class WebsiteFeedWriterTests
    {
        static readonly DateTimeOffset Noon = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

        readonly FakeClock clock = new() { UtcNow = Noon };
        readonly List<string> steps = new();
        readonly FakeGetData steam;
        readonly FakeFeedData data = new();
        readonly RecordingLogger<WebsiteFeedWriter> logger = new();
        readonly WebsiteFeedWriter writer;

        public WebsiteFeedWriterTests()
        {
            steam = new FakeGetData(step =>
            {
                lock (steps)
                    steps.Add(step);
            });

            writer = Writer(clock);
        }

        WebsiteFeedWriter Writer(TimeProvider clockToUse) => new(steam, data, clockToUse, logger)
        {
            LobbyInterval = TimeSpan.FromMilliseconds(20),
            MapInterval = TimeSpan.FromMilliseconds(20)
        };

        Task LobbyTickAsync() => writer.LobbyTickAsync(TestContext.Current.CancellationToken);

        Task MapTickAsync() => writer.MapTickAsync(TestContext.Current.CancellationToken);

        int Reads(string step)
        {
            lock (steps)
                return steps.Count(name => name == step);
        }

        internal static LobbyInfo Lobby(ulong id, uint version = GameVersions.Current) => new()
        {
            LobbyId = id,
            OwnerID = 76561197960265728 + id,
            VersionNumber = version,
            HostName = $"Host {id}",
            MapName = "Map",
            Modes = "Mode",
            CurrentPlayers = 1,
            MaxPlayers = 8,
            JoinURL = "https://example.com/join"
        };

        // MySql.Data's messages can name the login and the server, as this one does
        static MySqlException Refused(int number) =>
            (MySqlException)typeof(MySqlException)
                .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string), typeof(int), typeof(Exception)])!
                .Invoke(["Access denied for user 'site-login'@'db.example.com' (using password: YES)", number, new SocketException()]);

        static async Task WaitUntilAsync(Func<bool> condition, string failure)
        {
            var watch = Stopwatch.StartNew();

            while (!condition())
            {
                Assert.True(watch.Elapsed < WaitLimit, failure);
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }

        void NoSecretWasLogged() =>
            Assert.DoesNotContain(logger.Entries, entry => entry.Exception != null || entry.Message.Contains("site-login") || entry.Message.Contains("db.example.com")
                || entry.Message.Contains("Access denied") || entry.Message.Contains("password"));

        // The lobbies

        // Each good read replaces the whole set: a lobby that's still open keeps when it was first seen, a new one is added
        // and one that closed goes
        [Fact]
        public async Task GoodReadReplacesTheLobbies()
        {
            steam.Lobbies = [Lobby(1), Lobby(2)];

            await LobbyTickAsync();

            Assert.Equal([1ul, 2], data.Lobbies.Keys.Order().ToList());
            Assert.Equal((Noon.UtcDateTime, (DateTime?)Noon.UtcDateTime, 2), data.State[FeedName.Lobbies]);

            // It reads the list ten times as often as the notification job, and says so, so Steam's reader doesn't write
            // the line of a failed read at every one of its reads
            Assert.Equal([true], steam.LobbyReads);

            var later = Noon.AddSeconds(30);
            clock.UtcNow = later;
            steam.Lobbies = [Lobby(2), Lobby(3)];
            steam.Lobbies[0].CurrentPlayers = 6;

            await LobbyTickAsync();

            Assert.Equal([2ul, 3], data.Lobbies.Keys.Order().ToList());
            Assert.Equal((Noon.UtcDateTime, later.UtcDateTime, (ushort)6), (data.Lobbies[2].FirstSeen, data.Lobbies[2].LastSeen, data.Lobbies[2].Row.players));
            Assert.Equal((later.UtcDateTime, later.UtcDateTime), (data.Lobbies[3].FirstSeen, data.Lobbies[3].LastSeen));
            Assert.Equal((later.UtcDateTime, (DateTime?)later.UtcDateTime, 2), data.State[FeedName.Lobbies]);
        }

        // Every lobby the bot's own list would show, under a branch or under Other versions, not only the current version's
        [Fact]
        public async Task LobbiesOfEveryVersionAreWritten()
        {
            steam.Lobbies = [Lobby(1), Lobby(2, version: 92), Lobby(3, version: 80), Lobby(4, version: 104), Lobby(5, version: 106), Lobby(6, version: 0)];

            await LobbyTickAsync();

            Assert.Equal(
                [(105u, "Update 41", true), (92u, "Update 40", false), (80u, "Update 35 (Xbox One Branch)", false), (104u, "", false), (106u, "", false), (0u, "2012 Alpha", false)],
                data.Lobbies.Values.OrderBy(row => row.Row.lobby_id).Select(row => (row.Row.version_number, row.Row.version_label, row.Row.is_current)));
        }

        // Steam answering that nobody hosts is an answer, which empties the table. No answer at all isn't one.
        [Fact]
        public async Task AnswerWithNoLobbiesEmptiesTheTableAndNoAnswerLeavesIt()
        {
            steam.Lobbies = [Lobby(1), Lobby(2)];
            await LobbyTickAsync();

            // No session, a request Steam didn't take or an error from Steam: the list says unavailable
            steam.LobbiesAvailable = false;
            steam.Lobbies = [];
            clock.UtcNow = Noon.AddSeconds(30);
            await LobbyTickAsync();

            Assert.Equal([1ul, 2], data.Lobbies.Keys.Order().ToList());
            Assert.All(data.Lobbies.Values, row => Assert.Equal((Noon.UtcDateTime, Noon.UtcDateTime), (row.FirstSeen, row.LastSeen)));
            Assert.Equal((Noon.UtcDateTime.AddSeconds(30), (DateTime?)Noon.UtcDateTime, 2), data.State[FeedName.Lobbies]);

            steam.LobbiesAvailable = true;
            clock.UtcNow = Noon.AddSeconds(60);
            await LobbyTickAsync();

            Assert.Empty(data.Lobbies);
            Assert.Equal((Noon.UtcDateTime.AddSeconds(60), (DateTime?)Noon.UtcDateTime.AddSeconds(60), 0), data.State[FeedName.Lobbies]);
        }

        // A read of Steam that throws, or never answers, is a read that failed: no lobby changes, and nothing is thrown
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SteamReadThatThrowsOrHangsChangesNoLobby(bool hangs)
        {
            steam.Lobbies = [Lobby(1)];
            await LobbyTickAsync();

            if (hangs)
            {
                steam.LobbyHold = new TaskCompletionSource().Task;
                writer.LobbyReadLimit = TimeSpan.FromMilliseconds(50);
            }
            else
                steam.LobbyFailure = new InvalidOperationException("Steam said something with a secret in it");

            steam.Lobbies = [];
            clock.UtcNow = Noon.AddSeconds(30);
            await LobbyTickAsync().WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.Equal([1ul], data.Lobbies.Keys);
            Assert.Equal((Noon.UtcDateTime.AddSeconds(30), (DateTime?)Noon.UtcDateTime, 1), data.State[FeedName.Lobbies]);

            var error = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Error, error.Level);
            Assert.Contains($"The website feed couldn't read the open lobbies from Steam ({(hangs ? "TimeoutException" : "InvalidOperationException")})", error.Message);
            Assert.DoesNotContain(logger.Entries, entry => entry.Exception != null || entry.Message.Contains("secret"));
        }

        // The maps

        // About 30 of Steam's latest public maps, from more pages of Steam's list than the notification job reads
        [Fact]
        public async Task MapReadAsksFor30MapsAndWritesThem()
        {
            steam.LatestItems = Enumerable.Range(1, 40).Reverse().Select(FakeGetData.CreateItem).ToList();

            await MapTickAsync();

            Assert.Equal((30, 10), steam.LatestAsked);
            Assert.Equal((WebsiteFeedWriter.MapCount, WebsiteFeedWriter.MapPages), steam.LatestAsked);
            Assert.True(WebsiteFeedWriter.MapPages > GetData.MaxQueryPages);
            Assert.Equal(Enumerable.Range(11, 30).Select(id => (ulong)id), data.Maps.Keys.Order().ToList());
            Assert.Equal((Noon.UtcDateTime, (DateTime?)Noon.UtcDateTime, 30), data.State[FeedName.Maps]);
        }

        // A map that's read again keeps when it was first seen. One that the read should have held but doesn't was hidden or
        // removed on Steam, so it goes, and the maps older than the read reaches stay.
        [Fact]
        public async Task LaterMapReadKeepsFirstSeenAndDropsAMapThatWasHidden()
        {
            // Maps 1 to 35, the newest first. The read takes 30 of them, 6 to 35.
            steam.LatestItems = Enumerable.Range(1, 35).Reverse().Select(FakeGetData.CreateItem).ToList();
            await MapTickAsync();

            // Map 20 is hidden, and map 40 is new, so the read now reaches down to map 6 still
            var later = Noon.AddMinutes(5);
            clock.UtcNow = later;
            steam.LatestItems.RemoveAll(map => map.PublishedFileId == 20);
            steam.LatestItems.Insert(0, FakeGetData.CreateItem(40));
            await MapTickAsync();

            Assert.Equal(Enumerable.Range(6, 30).Where(id => id != 20).Select(id => (ulong)id).Append(40ul), data.Maps.Keys.Order().ToList());
            Assert.Equal((Noon.UtcDateTime, later.UtcDateTime), (data.Maps[6].FirstSeen, data.Maps[6].LastSeen));
            Assert.Equal((later.UtcDateTime, later.UtcDateTime), (data.Maps[40].FirstSeen, data.Maps[40].LastSeen));
            Assert.Equal((later.UtcDateTime, (DateTime?)later.UtcDateTime, 30), data.State[FeedName.Maps]);

            // Five new maps push the read's end up to map 11. Maps 6 to 10 are older than it reaches now, and stay.
            clock.UtcNow = later.AddMinutes(5);

            foreach (var id in Enumerable.Range(41, 5))
                steam.LatestItems.Insert(0, FakeGetData.CreateItem(id));

            await MapTickAsync();

            Assert.Equal(35, data.Maps.Count);
            Assert.Equal((Noon.UtcDateTime, later.UtcDateTime), (data.Maps[6].FirstSeen, data.Maps[6].LastSeen));
            Assert.Equal((later.UtcDateTime, later.UtcDateTime.AddMinutes(5)), (data.Maps[10].LastSeen, data.Maps[11].LastSeen));
        }

        // Steam's reader gives an empty list when Steam fails, and may throw. Either way no map changes.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MapReadThatFailsChangesNoMap(bool throws)
        {
            await MapTickAsync();
            var written = data.Maps.ToDictionary(row => row.Key, row => row.Value);

            if (throws)
                steam.Failure = new HttpRequestException("Steam said something with a secret in it");
            else
                steam.LatestItems = [];

            clock.UtcNow = Noon.AddMinutes(5);
            await MapTickAsync();

            Assert.Equal(written, data.Maps);
            Assert.Equal((Noon.UtcDateTime.AddMinutes(5), (DateTime?)Noon.UtcDateTime, 5), data.State[FeedName.Maps]);
            Assert.DoesNotContain(logger.Entries, entry => entry.Exception != null || entry.Message.Contains("secret"));
            Assert.Equal(throws ? 1 : 0, logger.Entries.Count(entry => entry.Message.Contains("The website feed couldn't read the latest maps from Steam (HttpRequestException)")));
        }

        // A read whose maps all lack their times has nothing that can be written, so it counts as one that failed
        [Fact]
        public async Task MapsWithoutTheirTimesAreNotWritten()
        {
            await MapTickAsync();

            var untimed = FakeGetData.CreateItem(50);
            untimed.TimeUpdated = default;
            steam.LatestItems = [untimed];
            clock.UtcNow = Noon.AddMinutes(5);

            await MapTickAsync();

            Assert.Equal([1ul, 2, 3, 4, 5], data.Maps.Keys.Order().ToList());
            Assert.Equal((Noon.UtcDateTime.AddMinutes(5), (DateTime?)Noon.UtcDateTime, 5), data.State[FeedName.Maps]);
        }

        // The website's database

        // The bot may run before the website has made the tables, or before its login has the rights, and the database may
        // be down. None of it is thrown into the bot: each feed says so once, with the error's number and what it means, and
        // nothing of the driver's message, which can name the login and the server.
        [Theory]
        [InlineData(1146, ": the website hasn't made the feed's tables yet")]
        [InlineData(1142, ": the bot's website login has no right to write the feed's tables")]
        [InlineData(1143, ": the bot's website login has no right to write the feed's tables")]
        [InlineData(1045, ": the website's database refused the bot's login")]
        [InlineData(1044, ": the website's database refused the bot's login")]
        [InlineData(1042, "")]
        public async Task MissingTablesRightsOrDatabaseNeverThrow(int number, string reason)
        {
            steam.Lobbies = [Lobby(1)];
            data.Failure = Refused(number);

            for (var tick = 0; tick < 20; tick++)
            {
                clock.UtcNow = Noon.AddSeconds(30 * tick);
                await LobbyTickAsync();
                await MapTickAsync();
            }

            Assert.Empty(data.Lobbies);
            Assert.Empty(data.Maps);
            Assert.Empty(data.State);

            var warnings = logger.Entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message).ToList();

            Assert.Equal(
            [
                $"The website's database didn't take the open lobbies for the website's Lobbies page (MySqlException, MySQL error {number}){reason}. The page keeps what it last got, and the bot tries again at least once every 5 minutes. Nothing else the bot does depends on it.",
                $"The website's database didn't take the latest maps for the website's Maps page (MySqlException, MySQL error {number}){reason}. The page keeps what it last got, and the bot tries again at least once every 5 minutes. Nothing else the bot does depends on it."
            ], warnings);

            Assert.Equal(2, logger.Entries.Count);
            NoSecretWasLogged();
        }

        // Anything else the repository can throw: a call that got no answer within its limit, a row it refused, or a
        // connection string the driver couldn't read
        [Theory]
        [InlineData(typeof(TimeoutException))]
        [InlineData(typeof(ArgumentException))]
        [InlineData(typeof(InvalidOperationException))]
        public async Task AnyFailureOfTheWriteStaysInTheFeed(Type failure)
        {
            data.Failure = (Exception)Activator.CreateInstance(failure, "SERVER=db.example.com;UID=site-login;PASSWORD=password")!;

            await LobbyTickAsync();
            await MapTickAsync();

            Assert.Equal(2, logger.Entries.Count(entry => entry.Level == LogLevel.Warning && entry.Message.Contains($"({failure.Name}). The page keeps what it last got")));
            NoSecretWasLogged();
        }

        // 30 seconds, then twice as long each time, up to 5 minutes, and never sooner, as the lobbies' ticks come. Steam
        // isn't asked while a wait isn't over, and the whole outage gets one warning.
        [Fact]
        public async Task FailedWriteWaitsLongerEachTime()
        {
            data.Failure = Refused(1146);
            await LobbyTickAsync();

            var attempts = new List<int>();
            var last = data.Calls;

            for (var second = 30; second <= 1200; second += 30)
            {
                clock.UtcNow = Noon.AddSeconds(second);
                await LobbyTickAsync();

                if (data.Calls != last)
                {
                    attempts.Add(second);
                    last = data.Calls;
                }
            }

            Assert.Equal([30, 90, 210, 450, 750, 1050], attempts);
            Assert.Equal(7, Reads(nameof(FakeGetData.GetServerList)));
            Assert.Single(logger.Entries);
        }

        // A tick comes a moment early or late, by the timer. A try that's due within a second is made rather than left for
        // the tick after, which would double the first wait.
        [Fact]
        public async Task TryThatIsDueWithinASecondIsMade()
        {
            data.Failure = Refused(1146);
            await LobbyTickAsync();

            clock.UtcNow = Noon.AddSeconds(28);
            await LobbyTickAsync();
            Assert.Equal(1, data.Calls);

            clock.UtcNow = Noon.AddSeconds(29.5);
            await LobbyTickAsync();
            Assert.Equal(2, data.Calls);
        }

        // The maps' reads are 5 minutes apart, which is the longest wait, so every one of them tries
        [Fact]
        public async Task EveryMapReadTriesAgain()
        {
            data.Failure = Refused(1146);

            for (var read = 0; read < 12; read++)
            {
                clock.UtcNow = Noon.AddMinutes(5 * read);
                await MapTickAsync();
            }

            Assert.Equal(12, data.Calls);
            Assert.Single(logger.Entries);
        }

        // One line when the writes work again, and the next outage gets a warning of its own
        [Fact]
        public async Task RecoveryIsLoggedOnceAndTheFeedCatchesUp()
        {
            steam.Lobbies = [Lobby(1)];
            data.Failure = Refused(1146);
            await LobbyTickAsync();
            await MapTickAsync();

            // The website made its tables
            data.Failure = null;
            clock.UtcNow = Noon.AddSeconds(30);
            await LobbyTickAsync();
            clock.UtcNow = Noon.AddMinutes(5);
            await MapTickAsync();
            await LobbyTickAsync();

            Assert.Equal([1ul], data.Lobbies.Keys);
            Assert.Equal(5, data.Maps.Count);
            Assert.Equal(Noon.UtcDateTime.AddSeconds(30), data.Lobbies[1].FirstSeen);
            Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Message == "The website's database takes the open lobbies for the website's Lobbies page again");
            Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Message == "The website's database takes the latest maps for the website's Maps page again");
            Assert.Equal(4, logger.Entries.Count);

            data.Failure = new TimeoutException("The database didn't answer within 15 seconds");
            clock.UtcNow = Noon.AddMinutes(10);
            await LobbyTickAsync();

            Assert.Equal(2, logger.Entries.Count(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("the open lobbies")));
            Assert.Equal(5, logger.Entries.Count);
        }

        // Each feed has its own tables, so one that can't be written doesn't hold up the other, or make it wait
        [Fact]
        public async Task OneFeedThatFailsDoesNotStopTheOther()
        {
            steam.Lobbies = [Lobby(1)];
            data.LobbyFailure = Refused(1142);

            for (var tick = 0; tick < 3; tick++)
            {
                clock.UtcNow = Noon.AddMinutes(5 * tick);
                await LobbyTickAsync();
                await MapTickAsync();
            }

            Assert.Empty(data.Lobbies);
            Assert.Equal(5, data.Maps.Count);
            Assert.Equal((Noon.UtcDateTime.AddMinutes(10), (DateTime?)Noon.UtcDateTime.AddMinutes(10), 5), data.State[FeedName.Maps]);
            Assert.Contains("the open lobbies for the website's Lobbies page (MySqlException, MySQL error 1142)", Assert.Single(logger.Entries).Message);
        }

        // The loop

        [Fact]
        public async Task LoopWritesBothFeedsFromTheStartAndStopsWhenAsked()
        {
            steam.Lobbies = [Lobby(1)];

            await writer.StartAsync(TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => data.FeedsWritten == 2 && data.Calls >= 6, "The feed's loop never wrote both feeds, or stopped after its first writes");

            Assert.False(writer.ExecuteTask!.IsCompleted);
            await writer.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.True(writer.ExecuteTask.IsCompletedSuccessfully);
            Assert.Equal([1ul], data.Lobbies.Keys);
            Assert.Equal(5, data.Maps.Count);
            Assert.Empty(logger.Entries);
        }

        // An exception that left a loop would stop the whole bot. A clock that fails stands in for anything that could, and
        // a database that fails throughout for the tables that aren't there yet.
        [Fact]
        public async Task FailureInALoopNeverEndsIt()
        {
            var flaky = new FlakyClock { Failures = 6 };
            var looping = Writer(flaky);

            data.Failure = Refused(1146);

            await looping.StartAsync(TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => data.Calls >= 2, "The feed never tried its writes after the failures");

            // The website made its tables, and the waits are over
            data.Failure = null;
            flaky.Now = Noon.AddMinutes(10);
            await WaitUntilAsync(() => data.FeedsWritten == 2, "The feed never wrote after the database came back");

            Assert.False(looping.ExecuteTask!.IsCompleted);
            await looping.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
            Assert.True(looping.ExecuteTask.IsCompletedSuccessfully);

            // Once each, however often it repeats within a minute
            Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("The website feed's lobbies failed (InvalidOperationException)"));
            Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("The website feed's maps failed (InvalidOperationException)"));
            NoSecretWasLogged();
        }

        // A stop while a write waits on the database ends the loop without waiting for it
        [Fact]
        public async Task StopDoesNotWaitForAWriteThatHangs()
        {
            data.Hold = new TaskCompletionSource().Task;

            await writer.StartAsync(TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => data.Calls >= 2, "The feed never started its writes");

            await writer.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.True(writer.ExecuteTask!.IsCompletedSuccessfully);
            Assert.Empty(logger.Entries);
        }

        sealed class FlakyClock : TimeProvider
        {
            int failures;
            long now = Noon.UtcTicks;

            public int Failures { set => failures = value; }

            public DateTimeOffset Now { set => Volatile.Write(ref now, value.UtcTicks); }

            public override DateTimeOffset GetUtcNow() =>
                Interlocked.Decrement(ref failures) >= 0 ? throw new InvalidOperationException("The clock failed") : new DateTimeOffset(Volatile.Read(ref now), TimeSpan.Zero);
        }
    }

    // The feed beside the notification job, on the real Discord.Net pipeline: they read the same lobbies and maps from Steam,
    // and share nothing else
    [Collection("Discord")]
    public class WebsiteFeedAndNotificationTests
    {
        const int NewId = 100;
        const string SendStep = "send message";

        readonly FakeFeedData data = new();
        readonly RecordingLogger<WebsiteFeedWriter> logger = new();

        static List<string> SentTitles(FakeDiscord discord) => discord.Steps
            .Where(step => step.Name == SendStep)
            .SelectMany(step => step.EmbedTitles)
            .ToList();

        async Task FeedTicksAsync(FakeDiscord discord)
        {
            var writer = new WebsiteFeedWriter(discord.Steam, data, discord.Clock, logger);

            await writer.LobbyTickAsync(TestContext.Current.CancellationToken);
            await writer.MapTickAsync(TestContext.Current.CancellationToken);
        }

        // The feed reads a new lobby and a new map before the notification job does. It writes nothing of the job's record,
        // so the job still finds them new and posts them, once: what the feed has written isn't what the job has seen.
        [Fact]
        public async Task FeedLeavesTheQueueAndTheSeenItemsAlone()
        {
            await using var discord = await FakeDiscord.StartAsync();
            await FeedTicksAsync(discord);
            await discord.RunNotificationJobAsync();
            var record = (discord.Store.Seen, discord.Store.Queue, discord.Store.Baselines);

            discord.Steam.LatestItems.Insert(0, FakeGetData.CreateItem(NewId));
            discord.Steam.Lobbies.Add(WebsiteFeedWriterTests.Lobby(NewId));

            await FeedTicksAsync(discord);

            Assert.Contains((ulong)NewId, data.Lobbies.Keys);
            Assert.Contains((ulong)NewId, data.Maps.Keys);
            Assert.Equal(record.Seen, discord.Store.Seen);
            Assert.Equal(record.Queue, discord.Store.Queue);
            Assert.Equal(record.Baselines, discord.Store.Baselines);

            await discord.RunNotificationJobAsync();
            await FeedTicksAsync(discord);
            await discord.RunNotificationJobAsync();

            Assert.Equal(["Map 100", "__NEW__ Murder Miners Public Lobbies"], SentTitles(discord));
            Assert.Empty(logger.Entries);

            // The job's reads of the lobby list are as they were, and only the feed's say they're frequent
            Assert.Equal([true, false, true, false, true, false], discord.Steam.LobbyReads);
        }

        // A pause holds the bot's posts on Discord. The feed isn't a post, so the website's pages keep following Steam.
        [Fact]
        public async Task PausedPostsDoNotPauseTheFeed()
        {
            await using var discord = await FakeDiscord.StartAsync();
            await discord.RunNotificationJobAsync();

            discord.Services.GetRequiredService<PostsPause>().Load(true, "Maintenance", discord.Clock.UtcNow.UtcDateTime);
            discord.Steam.LatestItems.Insert(0, FakeGetData.CreateItem(NewId));
            discord.Steam.Lobbies.Add(WebsiteFeedWriterTests.Lobby(NewId));

            await discord.RunNotificationJobAsync();
            await FeedTicksAsync(discord);

            Assert.Empty(SentTitles(discord));
            Assert.Equal([(ulong)NewId], data.Lobbies.Keys);
            Assert.Contains((ulong)NewId, data.Maps.Keys);
            Assert.Equal(2, data.State.Count);
        }
    }
}
