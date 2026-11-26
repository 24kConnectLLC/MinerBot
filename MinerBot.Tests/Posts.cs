using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using Steam.Models;
using SteamWebAPIAccess.Models;

namespace MinerBot.Tests
{
    // What the notification run's tests share: the posts' titles, the new items they add, and reads of what was sent and
    // what MinerBot's database holds
    internal static class Posts
    {
        public const string ArmorTitle = "New Armor Styles:";
        public const string LobbyTitle = "__NEW__ Murder Miners Public Lobbies";
        public const string SendStep = "send message";

        public const int NewId = 100;
        public const string NewMapTitle = "Map 100";

        public const string NewMapHeading = "__**New Map!**__";
        public const string UpdatedMapHeading = "__**Updated Map!**__";

        public static LobbyInfo Lobby(ulong id, uint version = 105) => new()
        {
            LobbyId = id,
            VersionNumber = version,
            HostName = $"Host {id}",
            MapName = "Map",
            Modes = "Mode",
            CurrentPlayers = 1,
            MaxPlayers = 8,
            JoinURL = "https://example.com/join"
        };

        public static List<Step> Sends(FakeDiscord discord) => discord.Steps.Where(step => step.Name == SendStep).ToList();

        public static List<string> SentTitles(FakeDiscord discord) => Sends(discord).SelectMany(step => step.EmbedTitles).ToList();

        // Only the first message of a map post has text
        public static List<string> MapHeadings(FakeDiscord discord) => Sends(discord).Where(step => !string.IsNullOrEmpty(step.Content)).Select(step => step.Content!).ToList();

        // The update the first map of a map post shows, as its Time Updated field has it
        public static string? UpdateShown(Step step) => step.Message.GetProperty("embeds")[0].GetProperty("fields").EnumerateArray()
            .Where(field => field.GetProperty("name").GetString() == "Time Updated:").Select(field => field.GetProperty("value").GetString()).SingleOrDefault();

        // A map made after the fixture's first five, a minute after NewId's for each ID past it, so the maps of one post are
        // within a day of each other, which a map older than that before the newest of its read isn't news
        public static PublishedFileDetailsModel NewMap(int id) => Dated(FakeGetData.CreateItem(id), id);

        public static PublishedFileDetailsModel NewLongMap(int id) => Dated(MapEmbedFlowTests.LongMap(id), id);

        static PublishedFileDetailsModel Dated(PublishedFileDetailsModel map, int id)
        {
            map.TimeCreated = map.TimeUpdated = DateTime.UnixEpoch.AddDays(NewId).AddMinutes(id - NewId);
            return map;
        }

        // An update puts a map on top of Steam's list
        public static void UpdateMap(FakeDiscord discord, PublishedFileDetailsModel map, int day)
        {
            map.TimeUpdated = DateTime.UnixEpoch.AddDays(day);
            discord.Steam.LatestItems.Remove(map);
            discord.Steam.LatestItems.Insert(0, map);
        }

        // A new armor, a new map on top of Steam's list and a new lobby
        public static void AddNewItems(FakeDiscord discord)
        {
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.Steam.LatestItems.Insert(0, FakeGetData.CreateItem(NewId));
            discord.Steam.Lobbies.Add(Lobby(NewId));
        }

        // A bot whose first run has taken the baselines
        public static async Task<FakeDiscord> StartWithBaselineAsync()
        {
            var discord = await FakeDiscord.StartAsync();
            await discord.RunNotificationJobAsync();
            return discord;
        }

        // The joined server as a second server with every kind on in its general channel, where the bot is Administrator
        public static async Task AddSecondServerAsync(FakeDiscord discord)
        {
            await discord.JoinGuildAsync($"[{FakeDiscord.BotMemberJson(FakeDiscord.BotRoleId)}]");
            discord.Guilds.AllGuilds.Add(SecondServer());
        }

        public static Guild SecondServer()
        {
            var row = FakeGuildData.CreateGuild(FakeDiscord.JoinedChannelId);
            row.guild_id = FakeDiscord.JoinedGuildId;
            return row;
        }

        // The rows one server's kind is owed, in the queue's order
        public static List<QueueRow> Queue(FakeDiscord discord, ulong guildId, string source) =>
            discord.Store.Queue.Where(row => row.guild_id == guildId && row.source == source).ToList();

        public static SeenRow? Seen(FakeDiscord discord, string source, ulong id) => discord.Store.Seen.SingleOrDefault(row => row.source == source && row.item_id == id);

        public static DeliveryStatusRow? Status(FakeDiscord discord, ulong guildId, string source) =>
            discord.Store.Statuses.SingleOrDefault(row => row.guild_id == guildId && row.source == source);

        public static GuildSwitches Switches(ulong guildId, bool armor = true, bool maps = true, bool lobbies = true) => new()
        {
            guild_id = guildId,
            armor_on = armor,
            maps_on = maps,
            lobbies_on = lobbies,
            version = 1,
            changed_at = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc),
            changed_by = SignedRequests.OwnerId,
            changed_by_role = "owner"
        };
    }
}
