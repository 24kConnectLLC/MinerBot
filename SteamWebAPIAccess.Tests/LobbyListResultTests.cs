using System.Net;
using System.Reflection;
using SteamKit2;
using SteamKit2.Internal;

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

        public LobbyListResultTests()
        {
            // SteamAPIAccess reads this without checking it
            Environment.SetEnvironmentVariable("STEAMAPP_ID", "1");

            steam = new SteamAPIAccess();
            steam.CreateClient();
            data = new GetData(steam);
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

        async Task<Models.LobbyListResult> AnswerWith(EResult answer, params SteamMatchmaking.Lobby[] lobbies)
        {
            LogOn();

            var jobId = NextJobId();
            var pending = data.GetServerList();

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

        static SteamMatchmaking.Lobby Lobby(ulong id)
        {
            var metadata = new Dictionary<string, string> { ["host_steam_id"] = "1", ["host_gamertag"] = "Host", ["prop_7"] = "105" };

            return (SteamMatchmaking.Lobby)typeof(SteamMatchmaking.Lobby).GetConstructors(NonPublicInstance).Single()
                .Invoke([new SteamID(id), ELobbyType.Public, 0, null, metadata, 8, 1, null, 0f, 0L]);
        }
    }
}
