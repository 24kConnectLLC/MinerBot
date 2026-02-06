using Discord;
using MinerBot_2._0.Builders;
using Mysqlx.Crud;
using Steam.Models;
using Steam.Models.SteamCommunity;
using SteamKit2;
using SteamWebAPIAccess;
using SteamWebAPIAccess.Models;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web;

namespace MinerBot_2._0.Services
{
    public class SteamAPIService
    {
        private readonly IGetData GetData;
        public string WorkshopItemDetailsURI { get; } = "https://steamcommunity.com/workshop/filedetails/?id=";

        public int ItemsPerPage = 1;

        public bool shouldShowPrevUpdate = true;

        public SteamAPIService(IGetData data)
        {
            GetData = data;
        }

        public void Initialize()
        {
        }
        public async Task<List<LobbyInfo>> GetLobbyList()
        {
            return await GetData.GetServerList();
        }

        public async Task<string> GetLobbiesDisplay(List<LobbyInfo> lobbies = null, bool hideEmpty = false)
        {
            if (lobbies == null)
                lobbies = await GetLobbyList();

            string finalString = "No lobbies available!\n\n[Host your own Lobby instead!](https://murderminershub.com/open-murder-miners)\n\n";

            if (lobbies == null)
                return finalString;

            var latestUpdate = lobbies.Where(result => result.VersionNumber >= 93);

            if (shouldShowPrevUpdate)
            {
                var eggHunt = lobbies.Where(result => result.VersionNumber == 39);                
                var update0 = lobbies.Where(result => result.VersionNumber == 0);
                var update19 = lobbies.Where(result => result.VersionNumber == 53);
                var update29 = lobbies.Where(result => result.VersionNumber == 61);
                var update35 = lobbies.Where(result => result.VersionNumber == 80);
                var update39 = lobbies.Where(result => result.VersionNumber < 90 && result.VersionNumber >= 87);
                var update40 = lobbies.Where(result => result.VersionNumber < 93 && result.VersionNumber >= 90);

                string tempString = "";

                if (latestUpdate.Count() > 0)
                    tempString += formatLobbyRows(latestUpdate, "Update 41");

                if (update40.Count() > 0)
                    tempString += formatLobbyRows(update40, "Update 40");

                // update39 branch
                if (update39.Count() > 0)
                    tempString += formatLobbyRows(update39, "Update 39");

                // classic_xboxone branch
                if (update35.Count() > 0)
                    tempString += formatLobbyRows(update35, "Update 35 (Xbox One Branch)");

                // update29 branch, only exists for legacy speedrun.com leaderboard
                if (update29.Count() > 0)
                    tempString += formatLobbyRows(update29, "Update 29");

                // classic_xbox360 branch
                if (update19.Count() > 0)
                    tempString += formatLobbyRows(update19, "Update 19 (Xbox 360 Branch)");

                // anniversary_atest2 branch
                if (update0.Count() > 0)
                    tempString += formatLobbyRows(update0, "2012 Alpha");

                // egghunt branch
                if (eggHunt.Count() > 0)
                    tempString += formatLobbyRows(update19, "Legacy Egg Hunt Branch");

                if (!string.IsNullOrEmpty(tempString))
                    finalString = tempString;
            }
            else
            {
                if (latestUpdate.Count() > 0)
                {
                    string tempString = "";

                    tempString += formatLobbyRows(latestUpdate, "Update 41");
                    finalString = tempString;
                }
            }

            return finalString;

            string formatLobbyRows(IEnumerable<LobbyInfo> lobbyList, string version)
            {
                string tempString = "";

                tempString += $"__{version}:__\n\n";

                if (lobbyList.Count() == 0)
                {
                    if (!hideEmpty)
                        return tempString + $"No {version} lobbies...\n\n[Host your own Lobby instead!](https://murderminershub.com/open-murder-miners)\n\n";
                    else
                        return "";
                }

                foreach (var lobby in lobbyList) 
                {
                    bool fullLobby = lobby.CurrentPlayers == lobby.MaxPlayers; // \u200b // _ _
                    string hostName = String.IsNullOrWhiteSpace(UIUtils.cleanTextForEmbed(lobby.HostName)) ? "Host Unknown" : UIUtils.cleanTextForEmbed(lobby.HostName);
                    string mapName = String.IsNullOrWhiteSpace(UIUtils.cleanTextForEmbed(lobby.MapName)) ? "Map Unknown" : UIUtils.cleanTextForEmbed(lobby.MapName);
                    string lobbyModes = String.IsNullOrWhiteSpace(UIUtils.cleanTextForEmbed(lobby.Modes)) ? "Modes Unknown" : UIUtils.cleanTextForEmbed(lobby.Modes);
                    tempString += $"- **Host:** `{hostName}` \u200b **Map:** `{mapName}`\n" + 
                                    $" \u200b **Mode:** `{lobbyModes}` \u200b **Players:** `{lobby.CurrentPlayers}/{lobby.MaxPlayers}` \u200b {(fullLobby ? "" : $"[Join]({lobby.JoinURL})")}" + "\n\n";
                }

                return tempString;
            }
        }

        public async Task<string> CreateLobby()
        {
            return await GetData.GetAndCreateLobby();
        }

        public async Task<string> GetUsersInLobby(ulong lobbyID)
        {
            var usersList = await GetData.GetUsersInLobby(lobbyID);
            Console.WriteLine($"LobID-={lobbyID}");

            if (usersList == null || usersList.Count < 1)
                return "No users found! Please check if your LobbyID is correct.";

            string users = "";
            foreach (var user in usersList)
            {
                users += $"Name: {user.PersonaName}, ID: {user.SteamID}";
                Console.WriteLine($"Name: {user.PersonaName}, ID: {user.SteamID} ");
            }
            return users;
        }

        public async Task<string> KickUserFromLobby(ulong userID, ulong lobbyID)
        {
            return await GetData.KickUser(userID, lobbyID);
        }

        public async Task<string> SendMessageToLobby(ulong lobbyID, string msg)
        {
            return GetData.SendMessageToLobby(lobbyID, msg);
        }

        public async Task<string> InviteUserToLobby(ulong lobbyID, ulong userID)
        {
            return GetData.InviteUser(lobbyID, userID);
        }

        public async Task<PublishedFileDetailsModel> GetWorkshopItem(string url)
        {
            NameValueCollection queryParameters = HttpUtility.ParseQueryString(new Uri(url).Query);
            ulong id = ulong.Parse(queryParameters["id"]);
            return await GetData.GetWorkshopItem(id);
        }

        public async Task<List<PublishedFileDetailsModel>> GetLatestWorkshopItems(int totalMaps = 5)
        {
            return await GetData.GetLatestWorkshopItems(totalMaps);
        }

        public async Task<string> JoinLobby(ulong lobbyID)
        {
            return await GetData.JoinLobby(lobbyID);
        }

        public async Task<string> LeaveLobby(ulong lobbyID)
        {
            return await GetData.LeaveLobby(lobbyID);
        }

        public async Task<(PlayerSummaryModel PlayerSummary, SteamID SteamID)> GetSteamIDSummary(string profileURL)
        {
            SteamID steamID = null;

            if (profileURL.Contains("steamcommunity.com/id/"))
                steamID = new SteamID(await GetData.ResolveVanityURL(profileURL.Replace("https://steamcommunity.com/id/", "").Replace("/", "")));
            else if (profileURL.Contains("steamcommunity.com/profiles/"))
                steamID = new SteamID(ulong.Parse(profileURL.Replace("https://steamcommunity.com/profiles/", "").Replace("/", "")));
            else
                return (null,null);

            return (await GetData.GetPlayerSummaries(steamID.ConvertToUInt64()), steamID);
        }

        // Need to embeds seperate into a different class. _SteamAPIService.Embeds.GetMapEmbed()

        public async Task<Embed[]> GetLatestMapsEmbeds(List<Steam.Models.PublishedFileDetailsModel> latestMaps = null, int totalMaps = 0)
        {
            if (latestMaps == null)
                latestMaps = await GetLatestWorkshopItems(totalMaps);
            List<Embed> embeds = new();
            foreach (var item in latestMaps)
                embeds.Add(await FormatMapEmbed(item));

            return embeds.ToArray();
        }

        public async Task<Embed> GetLatestMapsEmbed(List<Steam.Models.PublishedFileDetailsModel> latestMaps = null, int page = 0)
        {
            if (latestMaps == null)
                latestMaps = await GetLatestWorkshopItems();

            var latestMap = latestMaps.Skip(page).Take(ItemsPerPage).First();

            return await FormatMapEmbed(latestMap);
        }

        public async Task<Embed> GetMapEmbed(string workshopURL)
        {
            var result = await GetWorkshopItem(workshopURL);
            return await FormatMapEmbed(result);
        }

        public async Task<Embed> FormatMapEmbed(PublishedFileDetailsModel item)
        {
            Embed embed = new EmbedBuilderTemplate()
            .WithTitle(item.Title)
            .WithDescription(item.Description)
            .WithFields([
                new EmbedFieldBuilder { Name = "Subscriptions:", Value = item.Subscriptions, IsInline = true },
            new EmbedFieldBuilder { Name = "Life-Time Subscriptions:", Value = item.LifetimeSubscriptions, IsInline = true },
            new EmbedFieldBuilder { Name = "Views:", Value = item.Views, IsInline = true },
            new EmbedFieldBuilder { Name = "Favorited:", Value = item.Favorited, IsInline = true },
            new EmbedFieldBuilder { Name = "Life-Time Favorited:", Value = item.LifetimeFavorited, IsInline = true },
            new EmbedFieldBuilder { Name = "Tags:", Value = item.Tags.Count != 0 ? String.Join(", ", item.Tags) : "None", IsInline = true },
            new EmbedFieldBuilder { Name = "Time Created:", Value = item.TimeCreated, IsInline = true },
            new EmbedFieldBuilder { Name = "Time Updated:", Value = item.TimeUpdated, IsInline = true },
            new EmbedFieldBuilder { Name = "Direct Download:", Value = $"[Download Map]({item.FileUrl})", IsInline = true },
                ])
            .WithImageUrl(item.PreviewUrl == null ? "" : item.PreviewUrl.OriginalString)
            .WithUrl($"{WorkshopItemDetailsURI}{item.PublishedFileId}")
            .Build();

            return embed;
        }

        public async Task<Embed> SteamIDEmbed(string profileURL)
        {
            var result = await GetSteamIDSummary(profileURL);
            if (result.PlayerSummary == null || result.SteamID == null)
            {
                return new EmbedBuilderTemplate()
                    .WithTitle("Error: Please Provide a valid Steam User Profile URL/Link...")
                    .WithDescription("Try finding a URL that looks like this:\n https://steamcommunity.com/id/1234567890/ OR https://steamcommunity.com/profiles/1234567890")
                    .Build();
            }
            var playerSummary = result.PlayerSummary;
            var steamID = result.SteamID;
            var embed = new EmbedBuilderTemplate()
                .WithTitle($"Steam IDs for {playerSummary.Nickname}:")
                .WithDescription($"SteamID 2: `{steamID.Render(false)}`\n SteamID 3: `{steamID.Render(true)}`\n SteamID 64: `{steamID.ConvertToUInt64()}`")
                .WithThumbnailUrl(playerSummary.AvatarFullUrl)
                .WithUrl(playerSummary.ProfileUrl)
                .Build();

            return embed;
        }

        public async Task<Embed> LobbyListEmbed(string lobbyDisplay = "", string type = "")
        {
            if (lobbyDisplay == "")
                lobbyDisplay = await GetLobbiesDisplay();

            var embed = new EmbedBuilderTemplate()
                .WithTitle($"{type} Murder Miners Public Lobbies")
                .WithDescription(lobbyDisplay)
                .Build();

            return embed;
        }

        public string ReconnectToSteam()
        {
            string status = "Reconnected to Steam: ";
            try
            {
                status += "Success";
                GetData.Reconnect();
                Console.WriteLine(status);
                return status;
            }
            catch
            {
                status += "Failed";
                Console.WriteLine(status);
                return status;
            }
        }

        public string DisconnectFromSteam()
        {
            string status = "Disconnected from Steam: ";
            try
            {
                status += "Success";
                GetData.Disconnect();
                Console.WriteLine(status);
                return status;
            }
            catch
            {
                status += "Failed";
                Console.WriteLine(status);
                return status;
            }
        }
    }
}
