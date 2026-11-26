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

        // Murder Miners' app ID, which a Workshop map has to carry
        uint AppID { get; }

        void Disconnect();
        Task<string> GetAndCreateLobby();
        Task<string> GetInviteLink(uint lobbyID);

        // The latest public maps, from at most maxPages pages of Steam's list: three unless the caller says otherwise
        Task<List<PublishedFileDetailsModel>> GetLatestWorkshopItems(int numPerPage, int maxPages = GetData.MaxQueryPages);

        Task<string> GetLobbyData(ulong lobbyID);
        Task<PlayerSummaryModel?> GetPlayerSummaries(ulong steamid);

        // The open public lobbies. frequent is for a caller that reads them far more often than every 5 minutes: its
        // failed reads write their line once every 5 minutes, not every time.
        Task<LobbyListResult> GetServerList(bool frequent = false);

        ulong GetSteamID3To64(uint steamID3);
        Task<List<SteamMatchmaking.Lobby.Member>> GetUsersInLobby(ulong lobbyID);
        Task<PublishedFileDetailsModel?> GetWorkshopItem(ulong id);

        // The maps of these IDs that Steam still lists publicly, in one call: a map hidden, removed or banned since isn't
        // among them. Throws when Steam doesn't answer, which says nothing about the maps.
        Task<IReadOnlyList<PublishedFileDetailsModel>> GetPublicWorkshopItems(IReadOnlyCollection<ulong> ids);
        void Initilization();
        string InviteUser(ulong lobbyID, ulong userSteamID);
        Task<NetworkSessionProperties> ReadLobbyMetadata(IReadOnlyDictionary<string, string> keyPairs);
        void Reconnect();
        Task<ulong?> ResolveVanityURL(string vanityURL);
    }
}