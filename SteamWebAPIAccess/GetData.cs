using Steam.Models;
using Steam.Models.SteamCommunity;
using SteamKit2;
using SteamWebAPI2.Utilities;
using SteamWebAPIAccess.Models;
using SteamWebAPIAccess.Murder_Miners_Networking;

namespace SteamWebAPIAccess
{
    public class GetData : IGetData
    {
        private readonly ISteamAPIAccess _steamAPI;
        private readonly HttpClient _httpClient;
        private readonly WebRetryHandler _webHandler;
        public SteamWebInterfaceFactory webInterfaceFactory;
        public SteamWebAPI2.Interfaces.SteamUser steamUser;
        public SteamWebAPI2.Interfaces.SteamRemoteStorage steamRemoteStorage;
        private SteamConfiguration? _steamWebAPI;

        public bool IsConnected => _steamAPI.steamClient.IsConnected;

        public uint AppID => _steamAPI.appID;

        // GetData is a singleton, so these warnings and the one alert are per run
        internal UnseenVersions UnseenVersions { get; }

        public GetData(ISteamAPIAccess steamAPI, HttpClient httpClient, WebRetryHandler webHandler)
        {
            _steamAPI = steamAPI;
            _httpClient = httpClient;
            _webHandler = webHandler;
            UnseenVersions = new UnseenVersions(steamAPI);
        }

        public void Initilization()
        {
            // For SteamKit2
            // Subscribe to callbacks. Do not consider subscribing if you want to use await.

            //_steamAPI.manager.Subscribe<SteamMatchmaking.GetLobbyListCallback>(FormatServerList);
            //_steamAPI.manager.Subscribe<SteamMatchmaking.CreateLobbyCallback>(CreateLobby);

            // For SteamWebAPI2
            webInterfaceFactory = new SteamWebInterfaceFactory(_steamAPI.PublisherWebAPIKey);
            steamUser = webInterfaceFactory.CreateSteamWebInterface<SteamWebAPI2.Interfaces.SteamUser>(_httpClient);
            steamRemoteStorage = webInterfaceFactory.CreateSteamWebInterface<SteamWebAPI2.Interfaces.SteamRemoteStorage>(_httpClient);

            // For SteamKit2's web calls. SteamKit sets its own timeout on a client and disposes it with the interface,
            // so each interface gets a client of its own on the shared handler.
            _steamWebAPI = SteamConfiguration.Create(config => config
                .WithHttpClientFactory(_ => new HttpClient(_webHandler, disposeHandler: false))
                .WithWebAPIKey(_steamAPI.PublisherWebAPIKey));
        }

        // In place of SteamKit's timeout, which is 100 seconds
        internal WebAPI.AsyncInterface GetSteamInterface(string name)
        {
            var steamInterface = _steamWebAPI!.GetAsyncWebAPIInterface(name);
            steamInterface.Timeout = _webHandler.ClientTimeout;
            return steamInterface;
        }

        // Get

        public async Task<LobbyListResult> GetServerList()
        {
            // Working With -
            // Host Name
            // Current Player Count
            // Max Player Count
            // Open Public Gamer Slots = Max Player Count - Current Player Count
            // Members
            // Owner SteamID
            // Lobby (object)
            // Lobby Type: Public, Private, etc.
            // Lobby Flags
            // Distance
            // Weight

            // MetaData:
            // Network Session Properties (ReadOnly)

            List<LobbyInfo> lobbyList = new();
            LobbyListResult unavailable = new() { Available = false };

            if (_steamAPI.steamClient.SessionID == null)
                return unavailable;

            SteamMatchmaking.GetLobbyListCallback callback;

            try
            {
                // SteamKit returns no job while it can't send the request yet
                var lobbyListJob = _steamAPI.steamMatchmaking.GetLobbyList(_steamAPI.appID);

                if (lobbyListJob == null)
                    return unavailable;

                callback = await lobbyListJob;
            }
            catch (Exception e)
            {
                // The job fails or is canceled when Steam times out or the connection drops
                Console.WriteLine("Error getting the lobby list: " + e.Message);
                return unavailable;
            }

            // NoMatch is Steam finding nothing, the same answer as an empty game
            if (callback.Result != EResult.OK && callback.Result != EResult.NoMatch)
            {
                Console.WriteLine($"Steam didn't return the lobby list: {callback.Result}");
                return unavailable;
            }

            int skippedLobbies = 0;

            foreach (var lobby in callback.Lobbies)
            {
                if (lobby.Metadata.Count == 0)
                    continue;

                lobby.Metadata.TryGetValue("host_steam_id", out string ownerSteamID);
                lobby.Metadata.TryGetValue("host_gamertag", out string ownerUsername);

                // Lobby metadata comes from the host, so one bad lobby must not stop the rest of the list
                if (!uint.TryParse(ownerSteamID, out uint ownerAccountID))
                {
                    skippedLobbies++;
                    continue;
                }

                var spResult = ReadLobbyMetadata(lobby.Metadata).Result;

                ulong props = NetPropertiesFunctions.GetBoolsFromUints(
                spResult.GetIntProperty(PropertyUsage.NetSettingsBools),
                spResult.GetIntProperty(PropertyUsage.NetSettingsBoolsExtended));

                string hostName = CleanHostName(ownerUsername);

                if (spResult.GetIntProperty(PropertyUsage.Gamenight) > 0)
                    hostName = "Gamenight";
                else if (spResult.GetIntProperty(PropertyUsage.Official) > 0)
                    hostName = "Official";

                uint versionNumber = spResult.GetVersion();

                // The game treats a lobby without a version as invalid, it's not the newest build
                if (versionNumber == NetPropertiesFunctions.INVALID_GAME_VERSION)
                {
                    skippedLobbies++;
                    continue;
                }

                // A version the bot doesn't know is still listed, under Other versions with Modes Unknown
                UnseenVersions.Report(versionNumber);

                lobbyList.Add(new LobbyInfo()
                {
                    HostName = hostName,
                    OwnerID = GetSteamID3To64(ownerAccountID),
                    MapName = CleanMapName(spResult.GetMapName()),
                    CurrentPlayers = lobby.NumMembers,
                    MaxPlayers = lobby.MaxMembers,
                    LobbyType = lobby.LobbyType.ToString(),
                    LobbyId = lobby.SteamID.ConvertToUInt64(),
                    JoinURL = $"https://murderminershub.com/join-lobby?lobbyId={lobby.SteamID.ConvertToUInt64()}&ownerId={GetSteamID3To64(ownerAccountID)}", // ?redirect=steam://joinlobby/{_steamAPI.appID}/{lobby.SteamID.ConvertToUInt64()}/{GetSteamID3To64(uint.Parse(ownerSteamID))}"
                    Distance = (float)lobby.Distance,
                    Modes = GameModesMenuEntry.GetGameModeText(props, spResult.GetBonusBools(), version:versionNumber).Trim(),
                    VersionNumber = versionNumber
                });

                //Console.WriteLine("Reading Members...");
                //foreach (var member in lobby.Members)
                //{
                //    Console.WriteLine($"Member: {member}");
                //}

                //Console.WriteLine();

                //Console.Write("Finding Metadata...");

                //foreach (var data in lobby.Metadata)
                //{
                //    Console.WriteLine($"Key: {data.Key}, Value: {data.Value}");
                //}

                //Console.Write("Reading Metadata...");
                //Console.WriteLine(lobby.Metadata.Values.ToString());
                //foreach (var data in sessionProperties)
                //{
                //    Console.WriteLine($"Value: {data}");
                //}
                //Console.WriteLine();
            }

            if (skippedLobbies > 0)
                Console.WriteLine($"Skipped {skippedLobbies} lobbies with unreadable metadata");

            return new LobbyListResult { Available = true, Lobbies = lobbyList };
        }

        // The game only writes the Steam persona name here, which stops at 32 characters
        const int MAX_HOST_NAME_LENGTH = 32;

        internal static string CleanHostName(string? hostName)
        {
            if (hostName == null)
                return "";

            hostName = string.Join(' ', hostName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

            if (hostName.Length <= MAX_HOST_NAME_LENGTH)
                return hostName;

            // Half of a surrogate pair isn't a valid character
            return hostName.Substring(0, char.IsHighSurrogate(hostName[MAX_HOST_NAME_LENGTH - 1]) ? MAX_HOST_NAME_LENGTH - 1 : MAX_HOST_NAME_LENGTH);
        }

        // Map names are shown inside inline code, and a backtick is the only character of the game's table that ends it
        internal static string CleanMapName(string mapName)
        {
            return mapName.Trim().Replace('`', '\'');
        }

        // availableSession.SessionProperties.GetIntProperty(PropertyUsage.Gamenight/NetSsettingsBools)

        public async Task<List<SteamMatchmaking.Lobby.Member>> GetUsersInLobby(ulong lobbyID)
        {
            var callback = await _steamAPI.steamMatchmaking.GetLobbyData(_steamAPI.appID, lobbyID);//_steamAPI.steamMatchmaking.GetLobby(_steamAPI.appID, lobbyID);
            List<SteamMatchmaking.Lobby.Member> list = callback == null ? null : [.. callback.Lobby.Members];
            return list;
        }

        public async Task<string> GetInviteLink(uint lobbyID)
        {
            return "";
        }

        // Null when Steam's reply has no details for the ID. Steam answers an ID it doesn't have with a placeholder whose
        // result isn't OK, which WorkshopItems.Check turns down.
        public async Task<PublishedFileDetailsModel?> GetWorkshopItem(ulong id)
        {
            return (await GetWorkshopDetails([id])).FirstOrDefault(item => item.PublishedFileId == id);
        }

        // SteamWebAPI2 catches a failed request and gives no response at all, which says nothing about the maps, so it's
        // an error here like a timeout, and a lookup can't tell a user that a map doesn't exist while Steam is down.
        // Steam's list has an entry for every ID it's asked about, so a reply without a list (an empty body, or a failed
        // result) says nothing about the maps either.
        async Task<IReadOnlyCollection<PublishedFileDetailsModel>> GetWorkshopDetails(List<ulong> ids)
        {
            var response = await steamRemoteStorage.GetPublishedFileDetailsAsync(ids)
                ?? throw new HttpRequestException("Steam didn't answer the Workshop details request");

            return response.Data ?? throw new InvalidDataException("Steam's Workshop details reply held no list");
        }

        // Each page of QueryFiles also holds private and unlisted items, so a page of 20 almost always holds five public maps
        const int QueryPageSize = 20;

        // Steam can list fewer public maps than asked for, so the read stops here with what it has
        const int MaxQueryPages = 3;

        // Use WEBAPI Query Files, k_PublishedFileQueryType_RankedByLastUpdatedDate (21), k_PFI_MatchingFileType_Items (0)
        // Learn more: https://partner.steamgames.com/doc/webapi/IPublishedFileService#EPublishedFileInfoMatchingFileType
        // The first page has the most recently updated maps, whether they're new or updated. Pages are read until there
        // are numPerPage public maps, and all of their details come from one GetPublishedFileDetails call.
        public async Task<List<PublishedFileDetailsModel>> GetLatestWorkshopItems(int numPerPage)
        {
            var uriArgs = new Dictionary<string, object?>
            {
                ["query_type"] = 21, //SteamKit2.Internal.EPublishedFileQueryType.k_PublishedFileQueryType_RankedByLastUpdatedDate
                ["page"] = 1,
                ["cursor"] = "",//*
                ["numperpage"] = QueryPageSize,
                ["creator_app_id"] = _steamAPI.appID,
                ["appid"] = _steamAPI.appID,
                ["requiretags"] = "",
                ["excludedtags"] = "",
                ["match_all_tags"] = false,
                ["required_flags"] = "",
                ["omitted_flags"] = "",
                ["search_text"] = "",
                ["filetype"] = 0, // Game Item
                ["child_publishedfileid"] = 0,
                ["days"] = 0,
                ["include_recent_votes_only"] = false,
                ["required_kv_tags"] = "",
                ["totalonly"] = false,
                ["ids_only"] = false,
                ["return_vote_data"] = true,
                ["return_tags"] = true,
                ["return_kv_tags"] = true,
                ["return_previews"] = true,
                ["return_children"] = true,
                ["return_short_description"] = true,
                ["return_for_sale_data"] = false,
                ["return_metadata"] = true,
                ["return_playtime_stats"] = true
            };

            try
            {
                // In Steam's ranking, which the maps keep
                List<ulong> ids = new();

                using (var steamuser = GetSteamInterface("IPublishedFileService"))
                {
                    for (int pageNum = 1; pageNum <= MaxQueryPages && ids.Count < numPerPage; pageNum++)
                    {
                        uriArgs["page"] = pageNum;
                        var callback = await steamuser.CallAsync(HttpMethod.Get, "QueryFiles", args: uriArgs);

                        // Read by name, because Steam leaves out the fields an item doesn't have and the rest move up
                        var items = callback["publishedfiledetails"].Children;

                        if (items.Count == 0)
                            break;

                        foreach (var item in items)
                        {
                            ulong id = item["publishedfileid"].AsUnsignedLong();

                            // Only public maps are listed, and an item without a readable ID or visibility is skipped.
                            // A page can repeat an item of the one before, if Steam pages by its cursor instead.
                            if (id == 0 || item["visibility"].AsInteger(-1) != 0 || ids.Contains(id))
                                continue;

                            ids.Add(id);

                            if (ids.Count == numPerPage)
                                break;
                        }

                        // total counts every item of the query, so no later page holds any
                        int total = callback["total"].AsInteger(-1);
                        if (total >= 0 && pageNum * QueryPageSize >= total)
                            break;
                    }
                }

                List<PublishedFileDetailsModel> workshopItems = new();

                // GetPublishedFileDetails refuses an empty list
                if (ids.Count > 0)
                {
                    var details = (await GetWorkshopDetails(ids)).DistinctBy(item => item.PublishedFileId).ToDictionary(item => item.PublishedFileId);

                    // A map removed or hidden since the query is left out, so it can't be posted with empty fields
                    foreach (var id in ids)
                        if (details.TryGetValue(id, out var item) && WorkshopItems.Check(item, _steamAPI.appID, unlistedAllowed: false) == WorkshopItemCheck.Shown)
                            workshopItems.Add(item);
                }

                // The callers read an empty list as Steam failing
                if (workshopItems.Count == 0)
                    Console.WriteLine("Error getting the latest Workshop Items: Steam listed no public maps");

                return workshopItems;
            }
            catch(Exception e)
            {
                Console.WriteLine("Error getting the latest Workshop Items: " + e.Message);
                return new List<PublishedFileDetailsModel>();
            }
        }

        public async Task<string> GetLobbyData(ulong lobbyID)
        {
            var uriArgs = new Dictionary<string, object?> { ["appid"] = _steamAPI.appID, ["steamid_lobby"] = lobbyID };
            using (var matchmakingService = GetSteamInterface("ILobbyMatchmakingService"))
            {
                var callback = await matchmakingService.CallAsync(HttpMethod.Get, "GetLobbyData", args: uriArgs);
                return callback.AsString();
            }
        }

        // Post
        public async Task<string> GetAndCreateLobby()
        {
            ELobbyType lobbyType = new();
            lobbyType = ELobbyType.Public;
            int maxMembers = 5;

            var callback = await _steamAPI.steamMatchmaking.CreateLobby(_steamAPI.appID, lobbyType, maxMembers);

            return $"LobbyID: {callback.LobbySteamID}, AppID: {callback.AppID}";
        }

        public string InviteUser(ulong lobbyID, ulong userSteamID)
        {
            _steamAPI.steamMatchmaking.InviteToLobby(_steamAPI.appID, lobbyID, userSteamID);
            return "Attempted to Invite User...";
        }

        // Helper Methods (Should move these to a seperate class)

        public void Reconnect()
        {
            // Should have option to reconnect with old refresh token or authenticate again
            _steamAPI.steamClient.Connect();
        }

        public void Disconnect()
        {
            _steamAPI.steamClient.Disconnect();
        }

        public async Task<NetworkSessionProperties> ReadLobbyMetadata(IReadOnlyDictionary<string, string> keyPairs)
        {
            // We do this to clear previous data and make it read only.
            // A new object per lobby also keeps concurrent lobby list requests apart.
            NetworkSessionProperties sessionProperties = NetworkSessionProperties.CreateReadOnly();

            sessionProperties.ReadProperties(keyPairs);

            // Update 40 example. Update 39 has Version 87.
            //Value: 0
            //Value: 0
            //Value: 0
            //Value: 0
            //Value: 0
            //Value: 895630364
            //Value: 1421871492
            //Value: 90 (Version Number)
            //Value:
            //Value:
            //Value: 0
            //Value: 1010950144
            //Value: 3399896
            //Value:
            //Value:
            //Value:

            //NetPropertiesFunctions.GetIntProperty(SessionProperties, PropertyUsage.Team);

            return sessionProperties;
        }

        // Null when Steam has no such player, which SteamWebAPI2 answers with no response at all
        public async Task<PlayerSummaryModel?> GetPlayerSummaries(ulong steamid)
        {
            var playerSummaryResponse = await steamUser.GetPlayerSummaryAsync(steamid);
            return playerSummaryResponse?.Data;
        }

        // Null when nobody has the name. Steam answers that with success 42 and "No match", and the fields come in any order.
        public async Task<ulong?> ResolveVanityURL(string vanityURL)
        {
            var uriArgs = new Dictionary<string, object?> { ["vanityurl"] = vanityURL };
            using (var steamuser = GetSteamInterface("ISteamUser"))
            {
                var callback = await steamuser.CallAsync(HttpMethod.Get, "ResolveVanityURL", args: uriArgs);

                ulong steamId = callback["steamid"].AsUnsignedLong();
                return callback["success"].AsInteger() == 1 && steamId != 0 ? steamId : null;
            }
        }

        public const ulong ValveMagicNumber = 76561197960265728;

        // Without the special formating please..
        public ulong GetSteamID3To64(uint steamID3)
        {
            ulong convertedTo64bit = steamID3;// * 2;
            convertedTo64bit += ValveMagicNumber; // Valve's magic constant
            //convertedTo64bit += 1; // Universe number
            return convertedTo64bit;
        }
    }
}
