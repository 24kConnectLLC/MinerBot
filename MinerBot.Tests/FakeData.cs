using System.Runtime.CompilerServices;
using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Discord;
using DataAccessLibrary.Discord.Models;
using Steam.Models;
using Microsoft.Extensions.Internal;
using Steam.Models.SteamCommunity;
using SteamKit2;
using SteamWebAPIAccess;
using SteamWebAPIAccess.Models;
using SteamWebAPIAccess.Murder_Miners_Networking;

namespace MinerBot.Tests
{
    // Each fake reports its calls to the step list, so tests see them in order with the Discord requests.
    // Anything the tests don't use throws, so a new call to the data layer can't pass by accident.

    internal sealed class FakeGetData(Action<string> addStep) : IGetData
    {
        public List<PublishedFileDetailsModel> LatestItems { get; set; } = Enumerable.Range(1, 5).Select(CreateItem).ToList();
        public PublishedFileDetailsModel WorkshopItem { get; set; } = CreateItem(1);
        public PlayerSummaryModel PlayerSummary { get; set; } = new()
        {
            Nickname = "Player",
            AvatarFullUrl = "https://example.com/avatar.png",
            ProfileUrl = "https://steamcommunity.com/profiles/1"
        };
        public List<LobbyInfo> Lobbies { get; set; } = new();
        public bool LobbiesAvailable { get; set; } = true;
        public Exception? LobbyFailure { get; set; }
        public Exception? Failure { get; set; }
        public ulong? RequestedId { get; private set; }

        // Steam's IDs grow over time, so a higher ID is also a newer map
        public static PublishedFileDetailsModel CreateItem(int id) => new()
        {
            PublishedFileId = (ulong)id,
            Title = $"Map {id}",
            Description = "A map",
            FileUrl = new Uri("https://example.com/map"),
            Tags = new List<string>(),
            TimeUpdated = DateTime.UnixEpoch.AddDays(id)
        };

        public Task<List<PublishedFileDetailsModel>> GetLatestWorkshopItems(int numPerPage) => Call(LatestItems.Take(numPerPage).ToList());

        public Task<PublishedFileDetailsModel> GetWorkshopItem(ulong id)
        {
            RequestedId = id;
            return Call(WorkshopItem);
        }

        public Task<PlayerSummaryModel> GetPlayerSummaries(ulong steamid)
        {
            RequestedId = steamid;
            return Call(PlayerSummary);
        }

        public Task<LobbyListResult> GetServerList() => LobbyFailure != null
            ? Task.FromException<LobbyListResult>(LobbyFailure)
            : Call(new LobbyListResult { Available = LobbiesAvailable, Lobbies = Lobbies });

        Task<T> Call<T>(T value, [CallerMemberName] string name = "")
        {
            addStep(name);
            return Failure != null ? Task.FromException<T>(Failure) : Task.FromResult(value);
        }

        public bool IsConnected => throw new NotImplementedException();
        public void Disconnect() => throw new NotImplementedException();
        public Task<string> GetAndCreateLobby() => throw new NotImplementedException();
        public Task<string> GetInviteLink(uint lobbyID) => throw new NotImplementedException();
        public Task<string> GetLobbyData(ulong lobbyID) => throw new NotImplementedException();
        public ulong GetSteamID3To64(uint steamID3) => throw new NotImplementedException();
        public ulong getSteamIDTo64(uint steamID) => throw new NotImplementedException();
        public Task<List<SteamMatchmaking.Lobby.Member>> GetUsersInLobby(ulong lobbyID) => throw new NotImplementedException();
        public void Initilization() => throw new NotImplementedException();
        public string InviteUser(ulong lobbyID, ulong userSteamID) => throw new NotImplementedException();
        public Task<string> JoinLobby(ulong lobbyID) => throw new NotImplementedException();
        public Task<string> KickUser(ulong removeID, ulong lobbyID) => throw new NotImplementedException();
        public Task<string> LeaveLobby(ulong lobbyID) => throw new NotImplementedException();
        public Task<NetworkSessionProperties> ReadLobbyMetadata(IReadOnlyDictionary<string, string> keyPairs) => throw new NotImplementedException();
        public void Reconnect() => throw new NotImplementedException();
        public Task<ulong> ResolveVanityURL(string vanityURL) => throw new NotImplementedException();
        public string SendMessageToLobby(ulong lobbyID, string msg) => throw new NotImplementedException();
    }

    internal sealed class FakeArmorData(Action<string> addStep) : IArmorData
    {
        public IEnumerable<string> ArmorNames { get; set; } = ["Armor One", "Armor Two"];
        public ArmorStyle? Armor { get; set; }
        public List<ArmorStyle> AllArmor { get; set; } = [CreateArmor(1), CreateArmor(2)];
        public Exception? Failure { get; set; }
        public string? RequestedHelmet { get; private set; }

        public static ArmorStyle CreateArmor(int id) => new() { ID = id, ArmorName = $"Armor {id}" };

        public Task<IEnumerable<ArmorStyle>> GetAllArmor() => Call<IEnumerable<ArmorStyle>>(AllArmor);

        public Task<IEnumerable<string>> GetAllArmorNamesCache(TimeSpan timeSpan) => Call(ArmorNames);

        public Task<IEnumerable<string>> GetArmorNamesByHelmet(string helmetChoice)
        {
            RequestedHelmet = helmetChoice;
            return Call(ArmorNames);
        }

        public Task<ArmorStyle> GetArmorStyle(string name) => Call(Armor!);

        Task<T> Call<T>(T value, [CallerMemberName] string name = "")
        {
            addStep(name);
            return Failure != null ? Task.FromException<T>(Failure) : Task.FromResult(value);
        }

        public Task DeleteArmor(ArmorStyle armor) => throw new NotImplementedException();
        public Task<IEnumerable<string>> GetAllArmorNames() => throw new NotImplementedException();
        public Task InsertArmor(ArmorStyle armor) => throw new NotImplementedException();
        public Task UpdateArmor(ArmorStyle armor) => throw new NotImplementedException();
        public Task UpdateColorArray(int ID, string colorArray) => throw new NotImplementedException();
    }

    internal sealed class FakeGuildData(Action<string> addStep) : IGuildData
    {
        public Guild? Guild { get; set; }
        public List<Guild> AllGuilds { get; set; } = [CreateGuild(FakeDiscord.ChannelId)];
        public Exception? Failure { get; set; }

        public static Guild CreateGuild(ulong channelId) => new()
        {
            guild_id = FakeDiscord.GuildId,
            armor_channel_id = channelId,
            map_channel_id = channelId,
            lobby_channel_id = channelId
        };

        public Task<IEnumerable<Guild>> GetAllGuilds()
        {
            addStep(nameof(GetAllGuilds));
            return Failure != null ? Task.FromException<IEnumerable<Guild>>(Failure) : Task.FromResult<IEnumerable<Guild>>(AllGuilds);
        }

        public Task<Guild> GetGuild(string guildId)
        {
            addStep(nameof(GetGuild));
            return Task.FromResult(Guild!);
        }

        public Task InsertGuild(Guild guild)
        {
            addStep(nameof(InsertGuild));
            return Task.CompletedTask;
        }

        public Task UpdateGuild(Guild guild)
        {
            addStep(nameof(UpdateGuild));
            return Task.CompletedTask;
        }

        public Task DeleteGuild(ulong guildId) => throw new NotImplementedException();
        public Task<int> GetArmorChannelID(string guildId) => throw new NotImplementedException();
        public Task<int> GetLobbyChannelID(string guildId) => throw new NotImplementedException();
        public Task<int> GetMapChannelID(string guildId) => throw new NotImplementedException();
    }

    internal sealed class FakeClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }
}
