using Discord;
using Microsoft.Extensions.Caching.Memory;
using MinerBot_2._0.Builders;
using Steam.Models;
using Steam.Models.SteamCommunity;
using SteamKit2;
using SteamWebAPIAccess;
using SteamWebAPIAccess.Models;

namespace MinerBot_2._0.Services
{
    public class SteamAPIService
    {
        private readonly IGetData GetData;
        private readonly IMemoryCache _cache;
        private static readonly TimeSpan LatestWorkshopItemsCacheTime = TimeSpan.FromSeconds(60);
        private const int MaxMapDescriptionLength = 650; // Leaves room for most lists of five maps in one message
        public string WorkshopItemDetailsURI { get; } = "https://steamcommunity.com/workshop/filedetails/?id=";

        // Steam failing can only be waited out. The page buttons say it in a private message, and the commands in an embed.
        public const string SteamUnreachableText = "Steam can't be reached right now. Please try again later.";

        public SteamAPIService(IGetData data, IMemoryCache cache)
        {
            GetData = data;
            _cache = cache;
        }

        public void Initialize()
        {
        }
        public async Task<LobbyListResult> GetLobbyList()
        {
            return await GetData.GetServerList();
        }

        public async Task<string> GetLobbiesDisplay(List<LobbyInfo> lobbies = null, bool hideEmpty = false)
        {
            if (lobbies == null)
            {
                var lobbyList = await GetLobbyList();

                if (!lobbyList.Available)
                    return "Steam can't be reached right now, so the lobbies can't be listed. Please try again later.";

                lobbies = lobbyList.Lobbies;
            }

            string finalString = "No lobbies available!\n\n[Host your own Lobby instead!](https://murderminershub.com/open-murder-miners)\n\n";

            // All the rows share one embed description, so the version groups share one budget
            int charactersLeft = EmbedBuilder.MaxDescriptionLength - MoreLobbiesText(lobbies.Count).Length;
            int hiddenLobbies = 0;

            string tempString = "";

            // The game refuses a version mismatch, so a branch lists only the lobbies of its own version
            foreach (var branch in GameVersions.Branches)
            {
                var branchLobbies = lobbies.Where(result => result.VersionNumber == branch.Number);

                if (branchLobbies.Count() > 0)
                    tempString += formatLobbyRows(branchLobbies, branch.Heading!);
            }

            // Every other version comes last, with the version on each row, so no lobby goes unlisted
            var otherVersions = lobbies.Where(result => GameVersions.Find(result.VersionNumber)?.Heading == null);

            if (otherVersions.Count() > 0)
                tempString += formatLobbyRows(otherVersions, GameVersions.OtherVersionsHeading, showVersion: true);

            if (!string.IsNullOrEmpty(tempString))
                finalString = tempString;

            if (hiddenLobbies > 0)
                finalString += MoreLobbiesText(hiddenLobbies);

            return finalString;

            string formatLobbyRows(IEnumerable<LobbyInfo> lobbyList, string groupName, bool showVersion = false)
            {
                string heading = $"__{groupName}:__\n\n";
                string tempString = heading;

                if (lobbyList.Count() == 0)
                {
                    if (!hideEmpty)
                        return tempString + $"No {groupName} lobbies...\n\n[Host your own Lobby instead!](https://murderminershub.com/open-murder-miners)\n\n";
                    else
                        return "";
                }

                foreach (var lobby in lobbyList) 
                {
                    bool fullLobby = lobby.CurrentPlayers == lobby.MaxPlayers; // \u200b // _ _
                    string hostName = String.IsNullOrWhiteSpace(UIUtils.cleanTextForEmbed(lobby.HostName)) ? "Host Unknown" : UIUtils.cleanTextForEmbed(lobby.HostName);
                    string mapName = String.IsNullOrWhiteSpace(lobby.MapName) ? "Map Unknown" : lobby.MapName; // Already cleaned in GetData

                    // Written by the bot, not the host. For a version whose layout isn't known exactly, the modes would be guesses.
                    string lobbyModes = String.IsNullOrWhiteSpace(lobby.Modes) || !GameVersions.ModesTrusted(lobby.VersionNumber) ? "Modes Unknown" : lobby.Modes;
                    string version = showVersion ? $" \u200b **Version:** `v{lobby.VersionNumber}`" : "";
                    string row = $"- **Host:** `{hostName}` \u200b **Map:** `{mapName}`\n" + 
                                    $"{version} \u200b **Mode:** `{lobbyModes}` \u200b **Players:** `{lobby.CurrentPlayers}/{lobby.MaxPlayers}` \u200b {(fullLobby ? "" : $"[Join]({lobby.JoinURL})")}" + "\n\n";

                    // Once a row doesn't fit the rest stay out too, so the list keeps its order
                    if (hiddenLobbies > 0 || tempString.Length + row.Length > charactersLeft)
                    {
                        hiddenLobbies++;
                        continue;
                    }

                    tempString += row;
                }

                if (tempString == heading)
                    return "";

                charactersLeft -= tempString.Length;

                return tempString;
            }
        }

        // The website has no lobby list, so this opens the game, which shows all of them
        private static string MoreLobbiesText(int hiddenLobbies)
        {
            return $"...and {hiddenLobbies} more {(hiddenLobbies == 1 ? "lobby" : "lobbies")}\n\n[See them all in Murder Miners](https://murderminershub.com/open-murder-miners)";
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

        public async Task<string> InviteUserToLobby(ulong lobbyID, ulong userID)
        {
            return GetData.InviteUser(lobbyID, userID);
        }

        public async Task<List<PublishedFileDetailsModel>> GetLatestWorkshopItems(int totalMaps = 5)
        {
            string key = $"LatestWorkshopItems{totalMaps}";

            var items = _cache.Get<List<PublishedFileDetailsModel>>(key);
            if (items != null)
                return items;

            items = await GetData.GetLatestWorkshopItems(totalMaps);

            // GetData returns an empty list when Steam fails, so the next request should try again
            if (items.Count > 0)
                _cache.Set(key, items, LatestWorkshopItemsCacheTime);

            return items;
        }

        // Need to embeds seperate into a different class. _SteamAPIService.Embeds.GetMapEmbed()

        // In a post of new and updated maps, each embed says which its map is
        public async Task<Embed[]> GetLatestMapsEmbeds(List<(Steam.Models.PublishedFileDetailsModel Map, bool IsNew)> latestMaps)
        {
            bool newAndUpdated = latestMaps.Any(item => item.IsNew) && latestMaps.Any(item => !item.IsNew);

            List<Embed> embeds = new();
            foreach (var item in latestMaps)
                embeds.Add(await FormatMapEmbed(item.Map, newAndUpdated ? (item.IsNew ? "New Map" : "Updated Map") : null));

            return embeds.ToArray();
        }

        public async Task<List<(string? Text, Embed[] Embeds)>> GetLatestMapsMessages(List<(Steam.Models.PublishedFileDetailsModel Map, bool IsNew)> latestMaps)
        {
            var groups = GroupEmbedsByLength(await GetLatestMapsEmbeds(latestMaps));
            string heading = MapPostHeading(latestMaps.Count(item => item.IsNew), latestMaps.Count(item => !item.IsNew));

            // Only the first message has the heading
            return groups.Select((embeds, index) => (index == 0 ? heading : null, embeds)).ToList();
        }

        // Says what the post holds
        private static string MapPostHeading(int newMaps, int updatedMaps) => (newMaps, updatedMaps) switch
        {
            (1, 0) => "__**New Map!**__",
            (_, 0) => "__**New Maps!**__",
            (0, 1) => "__**Updated Map!**__",
            (0, _) => "__**Updated Maps!**__",
            _ => "__**New & Updated Maps!**__"
        };

        // Discord limits the length of all the embeds of a message together, so a long list goes out as several messages
        public static List<Embed[]> GroupEmbedsByLength(IEnumerable<Embed> embeds)
        {
            List<Embed[]> groups = new();
            List<Embed> group = new();
            int groupLength = 0;

            foreach (var embed in embeds)
            {
                if (groupLength + embed.Length > EmbedBuilder.MaxEmbedLength)
                {
                    groups.Add(group.ToArray());
                    group.Clear();
                    groupLength = 0;
                }

                group.Add(embed);
                groupLength += embed.Length;
            }

            groups.Add(group.ToArray());

            return groups;
        }

        // For /maps latest and its page buttons, which read the list once, answer an empty one themselves, and pass in a page of it
        public async Task<Embed> GetLatestMapsEmbed(List<Steam.Models.PublishedFileDetailsModel> latestMaps, int page = 0)
        {
            return await FormatMapEmbed(latestMaps[page]);
        }

        public async Task<Embed> GetMapEmbed(string workshopURL)
        {
            // Steam is only asked about a Workshop link or a map's ID
            if (!SteamLinks.TryParseWorkshopId(workshopURL, out ulong id))
            {
                return ErrorEmbed("Error: Please Provide a valid Steam Workshop Map URL/Link...",
                    "Try finding a URL that looks like this:\n https://steamcommunity.com/sharedfiles/filedetails/?id=1234567890\nOr enter only the map's ID, like 1234567890.");
            }

            PublishedFileDetailsModel? item;

            try
            {
                item = await GetData.GetWorkshopItem(id);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Couldn't look up a map on Steam: {e.GetType().Name}: {e.Message}");
                return SteamUnreachableEmbed();
            }

            // An unlisted map is meant to be shared by its link, so a lookup shows it
            switch (WorkshopItems.Check(item, GetData.AppID, unlistedAllowed: true))
            {
                case WorkshopItemCheck.Shown:
                    return await FormatMapEmbed(item!);
                case WorkshopItemCheck.OtherGame:
                    return ErrorEmbed("Error: That isn't a Murder Miners map...", "That link or ID is for another game's Workshop.");
                default:
                    return ErrorEmbed("Error: Steam has no public map with that ID...", "It may have been removed or hidden.");
            }
        }

        public async Task<Embed> FormatMapEmbed(PublishedFileDetailsModel item, string? label = null)
        {
            var embed = new EmbedBuilderTemplate()
            .WithTitle(item.Title)
            .WithDescription(ShortenMapDescription(item.Description))
            .WithFields([
                new EmbedFieldBuilder { Name = "Subscriptions:", Value = item.Subscriptions, IsInline = true },
            new EmbedFieldBuilder { Name = "Life-Time Subscriptions:", Value = item.LifetimeSubscriptions, IsInline = true },
            new EmbedFieldBuilder { Name = "Views:", Value = item.Views, IsInline = true },
            new EmbedFieldBuilder { Name = "Favorited:", Value = item.Favorited, IsInline = true },
            new EmbedFieldBuilder { Name = "Life-Time Favorited:", Value = item.LifetimeFavorited, IsInline = true },
            new EmbedFieldBuilder { Name = "Tags:", Value = MapTags(item.Tags), IsInline = true },
            new EmbedFieldBuilder { Name = "Time Created:", Value = WorkshopText.DiscordTimestamp(item.TimeCreated), IsInline = true },
            new EmbedFieldBuilder { Name = "Time Updated:", Value = WorkshopText.DiscordTimestamp(item.TimeUpdated), IsInline = true },
            new EmbedFieldBuilder { Name = "Direct Download:", Value = $"[Download Map]({item.FileUrl})", IsInline = true },
                ])
            .WithImageUrl(item.PreviewUrl == null ? "" : item.PreviewUrl.OriginalString)
            .WithUrl($"{WorkshopItemDetailsURI}{item.PublishedFileId}");

            // A small line above the title, which the template leaves free
            if (label != null)
                embed.WithAuthor(label);

            return embed.Build();
        }

        // The title of the embed links to the full description on Steam. The BBCode goes first, and the cut comes after the
        // escaping, so it can't split an escape from its character. A description with nothing left, such as one that was
        // only a picture, is sent as none, like a missing one, rather than as empty text, which Discord isn't documented to take.
        private static string? ShortenMapDescription(string? description)
        {
            if (description == null)
                return null;

            var shown = WorkshopText.ShortenEscaped(WorkshopText.EscapeMarkdown(WorkshopText.StripBBCode(description)), MaxMapDescriptionLength);

            return String.IsNullOrWhiteSpace(shown) ? null : shown;
        }

        // Each tag is escaped like the description, and escaping can lengthen the list past what a field holds
        private static string MapTags(IList<string>? tags)
        {
            var shown = String.Join(", ", (tags ?? []).Where(tag => !String.IsNullOrWhiteSpace(tag)).Select(WorkshopText.EscapeMarkdown));

            return shown.Length == 0 ? "None" : WorkshopText.ShortenEscaped(shown, EmbedFieldBuilder.MaxFieldValueLength)!;
        }

        // Errors have a title that says what went wrong and a line on what to do
        private static Embed ErrorEmbed(string title, string description) => new EmbedBuilderTemplate()
            .WithTitle(title)
            .WithDescription(description)
            .Build();

        public static Embed SteamUnreachableEmbed() => ErrorEmbed("Error: Steam can't be reached right now...", "Please try again later.");

        public async Task<Embed> SteamIDEmbed(string profileURL)
        {
            // Steam is only asked about a profile link or a SteamID64
            if (!SteamLinks.TryParseProfile(profileURL, out string? vanityName, out ulong steamId64))
            {
                return ErrorEmbed("Error: Please Provide a valid Steam User Profile URL/Link...",
                    "Try finding a URL that looks like this:\n https://steamcommunity.com/id/1234567890/ OR https://steamcommunity.com/profiles/76561198000000000");
            }

            PlayerSummaryModel? playerSummary = null;

            try
            {
                // A custom URL's name is looked up first, and a name nobody has gives no ID
                if (vanityName != null)
                    steamId64 = await GetData.ResolveVanityURL(vanityName) ?? 0;

                if (steamId64 != 0)
                    playerSummary = await GetData.GetPlayerSummaries(steamId64);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Couldn't look up a Steam profile: {e.GetType().Name}: {e.Message}");
                return SteamUnreachableEmbed();
            }

            if (playerSummary == null)
                return ErrorEmbed("Error: Steam has no profile for that link or ID...", "Please check it and try again.");

            var steamID = new SteamID(steamId64);
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
