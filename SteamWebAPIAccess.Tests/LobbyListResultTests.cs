using System.Net;
using System.Reflection;
using SteamKit2;
using SteamKit2.Internal;
using SteamWebAPIAccess.Models;
using SteamWebAPIAccess.Murder_Miners_Networking;

namespace SteamWebAPIAccess.Tests
{
    // Runs the real GetServerList on SteamKit's real job handling, without a connection to Steam.
    // SteamKit keeps the logon state, the jobs and the lobby types to itself, so these tests reach them with reflection.
    public class LobbyListResultTests
    {
        const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

        readonly SteamAPIAccess steam;
        readonly GetData data;
        readonly List<string> warnings = new();
        readonly List<string> alerts = new();

        public LobbyListResultTests()
        {
            // SteamAPIAccess reads this without checking it
            Environment.SetEnvironmentVariable("STEAMAPP_ID", "1");

            steam = new SteamAPIAccess();
            steam.CreateClient();
            // These tests make no web requests
            data = new GetData(steam, new HttpClient(), new WebRetryHandler());

            // Other tests swap the console while these run, so the warnings are read here
            data.UnseenVersions.Log = warning =>
            {
                lock (warnings)
                    warnings.Add(warning);
            };

            steam.AlertOwner = message =>
            {
                lock (alerts)
                    alerts.Add(message);

                return Task.CompletedTask;
            };
        }

        [Fact]
        public async Task NoSessionIsUnavailable()
        {
            var result = await data.GetServerList();

            Assert.False(result.Available);
            Assert.Empty(result.Lobbies);
        }

        [Fact]
        public async Task RequestThatCanNotBeSentYetIsUnavailable()
        {
            // SteamKit gives back no job until it has a cell ID
            LogOn(cellId: null);

            var result = await data.GetServerList();

            Assert.False(result.Available);
        }

        [Theory]
        [InlineData(EResult.Fail)]
        [InlineData(EResult.Timeout)]
        [InlineData(EResult.ServiceUnavailable)]
        [InlineData(EResult.AccessDenied)]
        public async Task ErrorResultIsUnavailable(EResult error)
        {
            var result = await AnswerWith(error, Lobby(1));

            Assert.False(result.Available);
            Assert.Empty(result.Lobbies);
        }

        [Theory]
        [InlineData(EResult.OK)]
        [InlineData(EResult.NoMatch)]
        public async Task AnswerWithNoLobbiesIsAvailable(EResult answer)
        {
            var result = await AnswerWith(answer);

            Assert.True(result.Available);
            Assert.Empty(result.Lobbies);
        }

        [Fact]
        public async Task LobbiesOfAGoodAnswerAreRead()
        {
            var result = await AnswerWith(EResult.OK, Lobby(1), Lobby(2));

            Assert.True(result.Available);
            Assert.Equal([1UL, 2UL], result.Lobbies.Select(lobby => lobby.LobbyId));
            Assert.Equal("Host", result.Lobbies[0].HostName);
        }

        // The lobby list names the game's own hosts in place of the host, so the kind is carried apart from the name, and
        // a player who is named Gamenight isn't taken for one. A lobby that says both counts as a Gamenight.
        [Fact]
        public async Task HostKindIsReadApartFromTheHostsName()
        {
            var result = await AnswerWith(EResult.OK,
                Lobby(1),
                Lobby(2, host: "Gamenight"),
                Lobby(3, properties: [(PropertyUsage.Gamenight, 1)]),
                Lobby(4, properties: [(PropertyUsage.Official, 1)]),
                Lobby(5, properties: [(PropertyUsage.Gamenight, 1), (PropertyUsage.Official, 1)]),
                Lobby(6, properties: [(PropertyUsage.Gamenight, 0), (PropertyUsage.Official, 0)]));

            Assert.Equal(
                [("Host", LobbyHostKind.Player), ("Gamenight", LobbyHostKind.Player), ("Gamenight", LobbyHostKind.Gamenight), ("Official", LobbyHostKind.Official),
                    ("Gamenight", LobbyHostKind.Gamenight), ("Host", LobbyHostKind.Player)],
                result.Lobbies.Select(lobby => (lobby.HostName, lobby.HostKind)));
        }

        // The lines of a failed read

        // The notification job and the slash command read the list every 5 minutes or when asked, and each of their reads
        // that fails, or skips a lobby it can't read, says so, as before
        [Fact]
        public async Task EveryFailedReadIsLoggedForACallerThatReadsNowAndThen()
        {
            var lines = LobbyLines();

            for (var read = 0; read < 3; read++)
            {
                await AnswerWith(EResult.Fail);
                await AnswerWith(EResult.OK, Unreadable(1), Lobby(2));
            }

            Assert.Equal(Enumerable.Repeat(new[] { "Steam didn't return the lobby list: Fail", "Skipped 1 lobbies with unreadable metadata" }, 3).SelectMany(pair => pair), lines);
        }

        // The website feed reads the list every 30 seconds. Through a Steam outage, or with one lobby it can't read, its
        // reads would write the same line 2,880 times a day, so a line of one kind is written once, then again after 5
        // minutes. The other callers' lines aren't held back by it, and don't hold its lines back.
        [Fact]
        public async Task FrequentReaderLogsARepeatedLineOnceEvery5Minutes()
        {
            var lines = LobbyLines();
            var time = new ManualTime();
            data.Clock = time;

            for (var read = 0; read < 9; read++)
            {
                await AnswerWith(EResult.Fail, frequent: true);
                await AnswerWith(EResult.OK, frequent: true, Unreadable(1), Lobby(2));
                time.Advance(TimeSpan.FromSeconds(30));
            }

            Assert.Equal(["Steam didn't return the lobby list: Fail", "Skipped 1 lobbies with unreadable metadata"], lines);

            // The notification job's read, in the same outage
            await AnswerWith(EResult.Timeout);
            await AnswerWith(EResult.Timeout, frequent: true);

            Assert.Equal("Steam didn't return the lobby list: Timeout", Assert.Single(lines.Skip(2)));

            // 5 minutes after the feed's first line
            time.Advance(TimeSpan.FromSeconds(30));
            await AnswerWith(EResult.Timeout, frequent: true);
            await AnswerWith(EResult.Timeout, frequent: true);

            Assert.Equal(["Steam didn't return the lobby list: Timeout"], lines.Skip(3));

            // The lobbies of a frequent read are the same as any other's
            var result = await AnswerWith(EResult.OK, frequent: true, Lobby(1), Lobby(2));

            Assert.True(result.Available);
            Assert.Equal([1UL, 2UL], result.Lobbies.Select(lobby => lobby.LobbyId));
        }

        // A job that Steam fails or cancels says so in the same way
        [Fact]
        public async Task CanceledJobOfAFrequentReaderIsLoggedOnce()
        {
            var lines = LobbyLines();
            data.Clock = new ManualTime();

            for (var read = 0; read < 3; read++)
            {
                LogOn();

                var pending = data.GetServerList(frequent: true);
                Invoke("CancelPendingJobs");

                Assert.False((await pending.WaitAsync(WaitLimit, TestContext.Current.CancellationToken)).Available);
            }

            Assert.StartsWith("Error getting the lobby list: ", Assert.Single(lines));
        }

        // SteamKit fails a job straight away when the connection is gone
        [Fact]
        public async Task LostConnectionIsUnavailable()
        {
            LogOn(connected: false);

            var result = await data.GetServerList().WaitAsync(WaitLimit);

            Assert.False(result.Available);
        }

        // What happens when the job times out or the connection drops
        [Fact]
        public async Task CanceledJobIsUnavailable()
        {
            LogOn();

            var pending = data.GetServerList();
            Invoke("CancelPendingJobs");

            var result = await pending.WaitAsync(WaitLimit);

            Assert.False(result.Available);
        }

        // What happens when Steam reports the job as failed
        [Fact]
        public async Task FailedJobIsUnavailable()
        {
            LogOn();

            var jobId = NextJobId();
            var pending = data.GetServerList();
            Invoke("FailJob", jobId);

            var result = await pending.WaitAsync(WaitLimit);

            Assert.False(result.Available);
        }

        // Versions that aren't in the version table

        [Fact]
        public async Task LobbyOfAnUnseenVersionIsListed()
        {
            var result = await AnswerWith(EResult.OK, Lobby(1, version: 106));

            Assert.Equal(106u, Assert.Single(result.Lobbies).VersionNumber);
        }

        [Fact]
        public async Task UnseenVersionIsWarnedAboutOnce()
        {
            await AnswerWith(EResult.OK, Lobby(1, version: 106), Lobby(2, version: 106));
            await AnswerWith(EResult.OK, Lobby(3, version: 106));

            Assert.Equal(["Lobby version 106 isn't in the version table, so its lobbies show Modes Unknown under Other versions"], warnings);
        }

        [Theory]
        [InlineData(105)]
        [InlineData(104)]
        [InlineData(200)]
        [InlineData(93)]
        [InlineData(80)]
        [InlineData(39)]
        [InlineData(0)]
        public async Task KnownVersionIsNotReported(uint version)
        {
            var result = await AnswerWith(EResult.OK, Lobby(1, version));

            Assert.Single(result.Lobbies);
            Assert.Empty(warnings);
            Assert.Empty(alerts);
        }

        // Hosts write the version, so made-up ones can't fill the log or the memory
        [Fact]
        public async Task OnlySoManyUnseenVersionsAreWarnedAbout()
        {
            var versions = Enumerable.Range(1000, UnseenVersions.MaxWarnings + 5).Select(version => (uint)version).ToList();

            var result = await AnswerWith(EResult.OK, versions.Select((version, index) => Lobby((ulong)index + 1, version)).ToArray());
            await AnswerWith(EResult.OK, Lobby(100, version: 2000));

            Assert.Equal(versions.Count, result.Lobbies.Count);
            Assert.Equal(UnseenVersions.MaxWarnings + 1, warnings.Count);
            Assert.All(versions.Take(UnseenVersions.MaxWarnings).Zip(warnings), pair => Assert.StartsWith($"Lobby version {pair.First} isn't", pair.Second));
            Assert.Contains("no more are logged", warnings[^1]);
            Assert.Equal(UnseenVersions.MaxWarnings, data.UnseenVersions.Remembered);
        }

        // A lobby without a readable version is read as 42069 and skipped before versions are reported, so it can't take
        // the run's one alert from a new version
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("42069")]
        public async Task LobbyWithoutAReadableVersionIsNotReported(string? version)
        {
            var result = await AnswerWith(EResult.OK, Lobby(1, version), Lobby(2, version: 106));

            Assert.Equal(106u, Assert.Single(result.Lobbies).VersionNumber);
            Assert.Equal(["Lobby version 106 isn't in the version table, so its lobbies show Modes Unknown under Other versions"], warnings);
            Assert.StartsWith("MinerBot found lobbies on game version 106,", Assert.Single(alerts));
        }

        [Fact]
        public async Task OwnerIsAlertedOnceARunAboutTheFirstUnseenVersion()
        {
            await AnswerWith(EResult.OK, Lobby(1), Lobby(2, version: 106), Lobby(3, version: 107));
            await AnswerWith(EResult.OK, Lobby(4, version: 108));

            var alert = Assert.Single(alerts);
            Assert.Equal("MinerBot found lobbies on game version 106, which isn't in its version table. Lobbies on versions it doesn't know show Modes Unknown until the table in SteamWebAPIAccess/GameVersions.cs is updated.", alert);
            Assert.Equal(3, warnings.Count);
        }

        [Fact]
        public async Task AlertThatFailsDoesNotStopTheRead()
        {
            steam.AlertOwner = _ => throw new InvalidOperationException("Discord is down");

            var result = await AnswerWith(EResult.OK, Lobby(1, version: 106), Lobby(2));

            Assert.Equal(2, result.Lobbies.Count);
            Assert.Contains("Unable to alert the owner: Discord is down", warnings);
        }

        [Fact]
        public async Task SlowAlertDoesNotHoldUpTheRead()
        {
            steam.AlertOwner = _ => new TaskCompletionSource().Task;

            var result = await AnswerWith(EResult.OK, Lobby(1, version: 106));

            Assert.Single(result.Lobbies);
        }

        Task<Models.LobbyListResult> AnswerWith(EResult answer, params SteamMatchmaking.Lobby[] lobbies) => AnswerWith(answer, frequent: false, lobbies);

        async Task<Models.LobbyListResult> AnswerWith(EResult answer, bool frequent, params SteamMatchmaking.Lobby[] lobbies)
        {
            LogOn();

            var jobId = NextJobId();
            var pending = data.GetServerList(frequent);

            var constructor = typeof(SteamMatchmaking.GetLobbyListCallback).GetConstructors(NonPublicInstance).Single();
            steam.steamClient.PostCallback((CallbackMsg)constructor.Invoke([jobId, steam.appID, answer, lobbies.ToList()]));

            return await pending.WaitAsync(WaitLimit);
        }

        void LogOn(uint? cellId = 1, bool connected = true)
        {
            typeof(CMClient).GetProperty(nameof(CMClient.IsConnected))!.SetValue(steam.steamClient, connected);
            typeof(CMClient).GetProperty(nameof(CMClient.SessionID))!.SetValue(steam.steamClient, 1);
            typeof(CMClient).GetProperty(nameof(CMClient.CellID))!.SetValue(steam.steamClient, cellId);
            typeof(CMClient).GetProperty(nameof(CMClient.PublicIP))!.SetValue(steam.steamClient, IPAddress.Loopback);
        }

        // Job IDs count up, so this is the one that the next request gets
        JobID NextJobId()
        {
            var jobId = steam.steamClient.GetNextJobID();
            jobId.SequentialCount++;
            return jobId;
        }

        void Invoke(string jobManagerMethod, params object[] arguments)
        {
            var jobManager = typeof(SteamClient).GetField("jobManager", NonPublicInstance)!.GetValue(steam.steamClient)!;

            jobManager.GetType().GetMethod(jobManagerMethod)!.Invoke(jobManager, arguments);
        }

        // The lines the lobby reads write, in the order they came
        List<string> LobbyLines()
        {
            var lines = new List<string>();

            data.LobbyLog = line =>
            {
                lock (lines)
                    lines.Add(line);
            };

            return lines;
        }

        // A lobby whose host wrote no account number the bot can read, which the read skips
        static SteamMatchmaking.Lobby Unreadable(ulong id) =>
            (SteamMatchmaking.Lobby)typeof(SteamMatchmaking.Lobby).GetConstructors(NonPublicInstance).Single()
                .Invoke([new SteamID(id), ELobbyType.Public, 0, null, new Dictionary<string, string> { ["host_steam_id"] = "nobody", ["host_gamertag"] = "Host" }, 8, 1, null, 0f, 0L]);

        // A time the test moves, for the lines a frequent reader repeats
        sealed class ManualTime : TimeProvider
        {
            long ticks;

            public override long TimestampFrequency => TimeSpan.TicksPerSecond;

            public override long GetTimestamp() => Volatile.Read(ref ticks);

            public void Advance(TimeSpan time) => Interlocked.Add(ref ticks, time.Ticks);
        }

        static SteamMatchmaking.Lobby Lobby(ulong id, uint version = 105, string host = "Host", (PropertyUsage Property, uint Value)[]? properties = null) =>
            Lobby(id, version.ToString(), host, properties);

        // A null version is left out, the way a host that writes none leaves it. The properties are the game's own, by
        // their place in its list.
        static SteamMatchmaking.Lobby Lobby(ulong id, string? version, string host = "Host", (PropertyUsage Property, uint Value)[]? properties = null)
        {
            var metadata = new Dictionary<string, string> { ["host_steam_id"] = "1", ["host_gamertag"] = host };

            if (version != null)
                metadata["prop_7"] = version;

            foreach (var (property, value) in properties ?? [])
                metadata[$"prop_{(int)property}"] = value.ToString();

            return (SteamMatchmaking.Lobby)typeof(SteamMatchmaking.Lobby).GetConstructors(NonPublicInstance).Single()
                .Invoke([new SteamID(id), ELobbyType.Public, 0, null, metadata, 8, 1, null, 0f, 0L]);
        }
    }
}
