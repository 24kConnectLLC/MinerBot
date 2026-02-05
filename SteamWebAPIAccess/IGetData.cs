using Steam.Models;
using Steam.Models.SteamCommunity;
using SteamKit2;
using SteamWebAPIAccess.Models;
using SteamWebAPIAccess.Murder_Miners_Networking;

namespace SteamWebAPIAccess
{
    public interface IGetData
    {
        bool IsConnected { get; }

        void Disconnect();
        Task<string> GetAndCreateLobby();
        Task<string> GetInviteLink(uint lobbyID);
        Task<List<PublishedFileDetailsModel>> GetLatestWorkshopItems(int numPerPage);
        Task<string> GetLobbyData(ulong lobbyID);
        Task<PlayerSummaryModel> GetPlayerSummaries(ulong steamid);
        Task<List<LobbyInfo>> GetServerList();
        ulong GetSteamID3To64(uint steamID3);
        ulong getSteamIDTo64(uint steamID);
        Task<List<SteamMatchmaking.Lobby.Member>> GetUsersInLobby(ulong lobbyID);
        Task<PublishedFileDetailsModel> GetWorkshopItem(ulong id);
        void Initilization();
        string InviteUser(ulong lobbyID, ulong userSteamID);
        Task<string> JoinLobby(ulong lobbyID);
        Task<string> KickUser(ulong removeID, ulong lobbyID);
        Task<string> LeaveLobby(ulong lobbyID);
        Task<NetworkSessionProperties> ReadLobbyMetadata(IReadOnlyDictionary<string, string> keyPairs);
        void Reconnect();
        Task<ulong> ResolveVanityURL(string vanityURL);
        string SendMessageToLobby(ulong lobbyID, string msg);
    }
}