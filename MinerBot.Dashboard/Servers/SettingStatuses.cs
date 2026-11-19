using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Guilds.Models;

namespace MinerBot.Dashboard.Servers
{
    // The four kinds of post a server can choose a channel for on the website, in /admin set_channels' order
    public enum SettingKind
    {
        Armor,
        Maps,
        Lobbies,
        Announcements
    }

    // What a setting's channel is, as far as MinerBot's copy of its servers can tell
    public enum SettingState
    {
        // The server has no row on the website: it never ran /admin set_channels
        NotSetUp,

        // The row's channel ID is 0, which turns the kind off
        Off,

        // A channel of this server in the copy, where the bot has every permission the kind's post needs
        Ok,

        // A channel of this server in the copy, where the bot lacks a permission the post needs
        CantPost,

        // A channel the copy has under another server, where the bot doesn't post
        OtherServer,

        // Not a text or announcement channel in the copy: a voice channel's chat, which posts do reach, a forum, or a
        // channel that was deleted. Never counted as a problem.
        CantCheck,

        // MinerBot has left the server, or was never recorded in it, so its posts are skipped
        NotIn
    }

    // One setting's status, with its channel when the copy has it, and the permissions missing there in Discord's words
    public sealed record SettingStatus(SettingKind Kind, ulong ChannelId, SettingState State, BotChannelRow? Channel, IReadOnlyList<string> Missing);

    // The rule each of a server's four settings is read by
    internal static class SettingStatuses
    {
        public static readonly IReadOnlyList<SettingKind> Kinds = [SettingKind.Armor, SettingKind.Maps, SettingKind.Lobbies, SettingKind.Announcements];

        // Discord's own names, as an admin sees them in a channel's settings and as the bot's replies name them
        public const string ViewChannel = "View Channel";
        public const string SendMessages = "Send Messages";
        public const string EmbedLinks = "Embed Links";

        public static string KindLabel(SettingKind kind) => kind switch
        {
            SettingKind.Armor => "Armor",
            SettingKind.Maps => "Maps",
            SettingKind.Lobbies => "Lobbies",
            _ => "Announcements"
        };

        public static ulong ChannelOf(Guild settings, SettingKind kind) => kind switch
        {
            SettingKind.Armor => settings.armor_channel_id,
            SettingKind.Maps => settings.map_channel_id,
            SettingKind.Lobbies => settings.lobby_channel_id,
            _ => settings.announcements_channel_id
        };

        // From the bot's posting code: armor, maps and lobbies go out as embeds, so they need Embed Links too, and an
        // announcement is plain text (GuildNotificationJob and DiscordService.SendAnnouncement)
        public static bool NeedsEmbed(SettingKind kind) => kind != SettingKind.Announcements;

        public static IReadOnlyList<SettingStatus> Of(ulong guildId, bool botIsIn, Guild? settings, IReadOnlyDictionary<ulong, BotChannelRow> channels) =>
            Kinds.Select(kind => Status(kind, guildId, botIsIn, settings, channels)).ToList();

        public static SettingStatus Status(SettingKind kind, ulong guildId, bool botIsIn, Guild? settings, IReadOnlyDictionary<ulong, BotChannelRow> channels)
        {
            var id = settings == null ? Guild.ChannelOff : ChannelOf(settings, kind);

            if (!botIsIn)
                return new SettingStatus(kind, id, SettingState.NotIn, null, []);

            if (settings == null)
                return new SettingStatus(kind, id, SettingState.NotSetUp, null, []);

            if (id == Guild.ChannelOff)
                return new SettingStatus(kind, id, SettingState.Off, null, []);

            if (!channels.TryGetValue(id, out var channel))
                return new SettingStatus(kind, id, SettingState.CantCheck, null, []);

            if (channel.guild_id != guildId)
                return new SettingStatus(kind, id, SettingState.OtherServer, channel, []);

            var missing = Missing(kind, channel);
            return new SettingStatus(kind, id, missing.Count == 0 ? SettingState.Ok : SettingState.CantPost, channel, missing);
        }

        public static IReadOnlyList<string> Missing(SettingKind kind, BotChannelRow channel)
        {
            var missing = new List<string>();

            if (!channel.can_view)
                missing.Add(ViewChannel);

            if (!channel.can_send)
                missing.Add(SendMessages);

            if (NeedsEmbed(kind) && !channel.can_embed)
                missing.Add(EmbedLinks);

            return missing;
        }

        // What needs someone: a post the copy shows can't go out. Can't check isn't counted, because the copy can't tell a
        // voice channel's chat, which posts reach, from a forum or a deleted channel, which they don't.
        public static bool IsProblem(SettingState state) => state is SettingState.CantPost or SettingState.OtherServer;

        public static string Label(SettingState state) => state switch
        {
            SettingState.NotSetUp => "Not set up",
            SettingState.Off => "Off",
            SettingState.Ok => "OK",
            SettingState.CantPost => "Can't post",
            SettingState.OtherServer => "Another server's channel",
            SettingState.CantCheck => "Can't check",
            _ => "MinerBot isn't in this server"
        };

        // The label with what's missing, such as Can't post: missing Send Messages
        public static string Words(SettingStatus status) => status.State == SettingState.CantPost
            ? $"{Label(status.State)}: missing {string.Join(", ", status.Missing)}"
            : Label(status.State);

        // The mark's look: good, a problem, or neither
        public static string Marker(SettingState state) => state switch
        {
            SettingState.Ok => "marker-ok",
            SettingState.CantPost or SettingState.OtherServer => "marker-block",
            SettingState.NotSetUp => "marker-warn",
            _ => "marker-quiet"
        };
    }
}
