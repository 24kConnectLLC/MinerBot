using Newtonsoft.Json;
using Steam.Models;
using Steam.Models.SteamCommunity;
using SteamKit2;
using SteamKit2.Internal;
using SteamWebAPI2.Interfaces;
using SteamWebAPI2.Models;
using SteamWebAPI2.Utilities;
using SteamWebAPIAccess.Models;
using SteamWebAPIAccess.Murder_Miners_Networking;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SteamWebAPIAccess
{
    public class GetData : IGetData
    {
        private readonly ISteamAPIAccess _steamAPI;
        public SteamWebInterfaceFactory webInterfaceFactory;
        public SteamWebAPI2.Interfaces.SteamUser steamUser;
        public SteamWebAPI2.Interfaces.SteamRemoteStorage steamRemoteStorage;

        public bool IsConnected => _steamAPI.steamClient.IsConnected;

        public GetData(ISteamAPIAccess steamAPI)
        {
            _steamAPI = steamAPI;
        }

        public void Initilization()
        {
            // For SteamKit2
            // Subscribe to callbacks. Do not consider subscribing if you want to use await.

            //_steamAPI.manager.Subscribe<SteamMatchmaking.GetLobbyListCallback>(FormatServerList);
            //_steamAPI.manager.Subscribe<SteamMatchmaking.CreateLobbyCallback>(CreateLobby);

            // For SteamWebAPI2
            webInterfaceFactory = new SteamWebInterfaceFactory(_steamAPI.PublisherWebAPIKey);
            steamUser = webInterfaceFactory.CreateSteamWebInterface<SteamWebAPI2.Interfaces.SteamUser>(new HttpClient());
            steamRemoteStorage = webInterfaceFactory.CreateSteamWebInterface<SteamWebAPI2.Interfaces.SteamRemoteStorage>(new HttpClient());
        }

        // Get

        List<SteamMatchmaking.Lobby> _lobbies = new();
        public async Task<List<LobbyInfo>> GetServerList()
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
            if (_steamAPI.steamClient.SessionID == null)
                return null;

            var callback = await _steamAPI.steamMatchmaking.GetLobbyList(_steamAPI.appID);

            int skippedLobbies = 0;

            foreach (var lobby in callback.Lobbies)
            {
                if (lobby.Metadata.Count == 0 || lobby.Metadata == null)
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

            _lobbies = callback.Lobbies;

            if (skippedLobbies > 0)
                Console.WriteLine($"Skipped {skippedLobbies} lobbies with unreadable metadata");

            return lobbyList;
        }

        // The game only writes the Steam persona name here, which stops at 32 characters
        const int MAX_HOST_NAME_LENGTH = 32;

        internal static string CleanHostName(string? hostName)
        {
            if (hostName == null)
                return "";

            hostName = string.Join(' ', hostName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

            return hostName.Length > MAX_HOST_NAME_LENGTH ? hostName.Substring(0, MAX_HOST_NAME_LENGTH) : hostName;
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

        public async Task<PublishedFileDetailsModel> GetWorkshopItem(ulong id)
        {
            var callback = await steamRemoteStorage.GetPublishedFileDetailsAsync(id);
            return callback.Data.FirstOrDefault();
        }

        // Use WEBAPI Query Files, k_PublishedFileQueryType_RankedByPublicationDate (1), k_PFI_MatchingFileType_Items (0)
        // Learn more: https://partner.steamgames.com/doc/webapi/IPublishedFileService#EPublishedFileInfoMatchingFileType
        // Ideas:
        // - Every 15 minutes, run a query that fetches the first 5 latest published items. Have an old list to compare to. If any changes are made then display the differences.
        // - Make a model for this (if not going to use ids only)
        // Notes:
        // - Currently the query type is correct but is going from oldest. Need newest. Maybe get last page.
        // - Should Probably return ids only. Then use the other steam web api lib to get publishedfiledetails for each one.
        public async Task<List<PublishedFileDetailsModel>> GetLatestWorkshopItems(int numPerPage)
        {
            int pageNum = 1;
            var uriArgs = new Dictionary<string, object?>
            {
                ["query_type"] = 21, //SteamKit2.EPublishedFileQueryType.RankedByPublicationDate
                ["page"] = pageNum,
                ["cursor"] = "",//*
                ["numperpage"] = numPerPage,
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
                using (var steamuser = WebAPI.GetAsyncInterface("IPublishedFileService", _steamAPI.PublisherWebAPIKey))
                {
                    List<PublishedFileDetailsModel> workshopItems = new List<PublishedFileDetailsModel>();

                    while (workshopItems.Count != numPerPage)
                    {
                        uriArgs["page"] = pageNum;
                        var callback = await steamuser.CallAsync(HttpMethod.Get, "QueryFiles", args: uriArgs);

                        var items = callback.Children[1].Children;

                        foreach (var item in items)
                        {
                            // Need to filter if visibility is public
                            if (item.Children[18].Value == "0" && workshopItems.Count < numPerPage)
                                workshopItems.Add(await GetWorkshopItem(ulong.Parse(item.Children[1].Value)));
                        }
                        pageNum++;
                    }

                    return workshopItems;
                }
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
            using (var matchmakingService = WebAPI.GetAsyncInterface("ILobbyMatchmakingService", _steamAPI.PublisherWebAPIKey))
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

        public async Task<string> KickUser(ulong removeID, ulong lobbyID)
        {
            var uriArgs = new Dictionary<string, object?> { ["appid"] = _steamAPI.appID, ["steamid_to_remove"] = removeID, ["steamid_lobby"] = lobbyID };
            using (var matchmakingService = WebAPI.GetAsyncInterface("ILobbyMatchmakingService", _steamAPI.PublisherWebAPIKey))
            {
                var callback = await matchmakingService.CallAsync(HttpMethod.Post, "RemoveUserFromLobby", args: uriArgs);
                return callback.Value;
            }
            return "User Not Kicked";
        }

        public string InviteUser(ulong lobbyID, ulong userSteamID)
        {
            _steamAPI.steamMatchmaking.InviteToLobby(_steamAPI.appID, lobbyID, userSteamID);
            return "Attempted to Invite User...";
        }

        public async Task<string> JoinLobby(ulong lobbyID)
        {
            var callback = await _steamAPI.steamMatchmaking.JoinLobby(_steamAPI.appID, lobbyID);
            return callback.ChatRoomEnterResponse.ToString();
        }

        public async Task<string> LeaveLobby(ulong lobbyID)
        {
            var callback = await _steamAPI.steamMatchmaking.LeaveLobby(_steamAPI.appID, lobbyID);
            return callback.Result.ToString();
        }

        public string SendMessageToLobby(ulong lobbyID, string msg)
        {
            int singleDigitCode = 0;
            var message = new ClientMsgProtobuf<CMsgClientMMSSendLobbyChatMsg>(EMsg.ClientMMSSendLobbyChatMsg);
            message.Body.app_id = _steamAPI.appID;
            message.Body.steam_id_lobby = lobbyID;
            message.Body.lobby_message = Encoding.ASCII.GetBytes(singleDigitCode + msg);
            message.SteamID = _steamAPI.steamClient.SteamID;

            _steamAPI.steamMatchmaking.Send(message, _steamAPI.appID);

            return "Message has been sent!";
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

        public async Task<PlayerSummaryModel> GetPlayerSummaries(ulong steamid)
        {
            var playerSummaryResponse = await steamUser.GetPlayerSummaryAsync(steamid);
            return playerSummaryResponse.Data;
        }

        public async Task<ulong> ResolveVanityURL(string vanityURL)
        {
            var uriArgs = new Dictionary<string, object?> { ["vanityurl"] = vanityURL };
            using (var steamuser = WebAPI.GetAsyncInterface("ISteamUser", _steamAPI.PublisherWebAPIKey))
            {
                var callback = await steamuser.CallAsync(HttpMethod.Get, "ResolveVanityURL", args: uriArgs);
                return ulong.Parse(callback.Children[0].Value);
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

        public ulong getSteamIDTo64(uint steamID)
        {
            ulong convertedTo64bit = steamID * 2;
            convertedTo64bit += ValveMagicNumber; // Valve's magic constant
            convertedTo64bit += 1; // Universe number (Might or might not need this)
            return convertedTo64bit;
        }
    }
}
