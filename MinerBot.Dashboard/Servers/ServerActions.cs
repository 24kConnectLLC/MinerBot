using System.Globalization;
using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Guilds.Models;
using MinerBot.Shared.Requests;
using MinerBot.Shared.Text;

namespace MinerBot.Dashboard.Servers
{
    // Whether a server's page offers MinerBot's actions: only for a server the bot is in and Discord has now, since the bot
    // refuses requests about any other, and only while requests are on
    public enum ServerActionState
    {
        Offered,
        NotIn,
        Unavailable,
        RequestsOff
    }

    // One channel a server chose for MinerBot's posts, as a test message sees it: the kinds of post it was chosen for, in
    // /admin set_channels' order, the channel as MinerBot's copy has it in this server, if it does, and why a test message
    // can't go there, null when it can
    public sealed record TestChannel(ulong Id, IReadOnlyList<SettingKind> Kinds, BotChannelRow? Channel, string? Why)
    {
        public bool Offered => Why == null;

        public string IdText => Id.ToString(CultureInfo.InvariantCulture);
    }

    // A server page's actions: a test message to one of the channels the server chose for MinerBot's posts, a refresh of the
    // copy, and, for the Owner, a page to leave it on. Which channels a test message may go to, and the words. A test from
    // the MinerBot team checks the server's own choices, which is what "why aren't posts arriving" needs, so it never goes
    // to a channel the server didn't choose, such as a staff channel the bot happens to see. That's narrower than the bot's
    // own rule, which the bot checks again before it sends.
    internal static class ServerActions
    {
        // The handlers, by the name Razor Pages gives each form's handler
        public const string TestMessage = "TestMessage";
        public const string Refresh = "Refresh";

        // The server page's kinds, for each action's last use and the box ?request= names. A leave is asked for on its own
        // page, which leads back here.
        public static readonly IReadOnlyList<string> Kinds = [RequestKinds.GuildTestMessage, RequestKinds.GuildRefresh, RequestKinds.GuildLeave];

        public const string Heading = "Ask MinerBot";

        public const string NotIn = "MinerBot isn't in this server, so there's nothing to ask it here.";
        public const string Unavailable = "Discord reports this server unavailable, so the bot refuses requests about it until it's back.";
        public const string RequestsOff = "Requests to the bot are off, so these aren't offered.";

        public const string TestMessageLabel = "Send a test message";

        public const string TestMessageHelp = "Sends a fixed, silent embed, 'MinerBot test message', to one of the channels this server chose for MinerBot's "
            + "posts, to show whether a post can arrive there. It pings nobody. The bot checks the channel again before it sends, and takes 3 for each server "
            + "every 10 minutes.";

        public const string ChannelLabel = "Channel";

        public const string NoChannels = "This server hasn't chosen channels for MinerBot's posts, so there's nothing to test.";
        public const string NoneOffered = "None of the channels this server chose for MinerBot's posts can take a test message now.";

        public const string RefreshLabel = "Rewrite the copy";

        public const string RefreshHelp = "Asks the bot to rewrite its copy of this server and its channels from what it holds now, which Discord keeps "
            + "current. Nothing can make Discord send a server again, so this mends a copy that missed a change, not one that Discord hasn't sent. The bot "
            + "takes this once a minute for each server.";

        // Why a chosen channel can't take the test message, after "{kinds}: #{name} can't take the test message: "
        public const string NeedsEmbed = "it needs Embed Links, which plain-text announcements don't";
        public const string NotThisServers = "it isn't one of this server's text or announcement channels in MinerBot's copy";

        public static string Missing(IReadOnlyList<string> permissions) => $"MinerBot is missing {Join(permissions)} there";

        // A form for a channel that could take it when the page was drawn, and can't now
        public static string ChannelNotNow(string why) => $"That channel can't take a test message now: {why}. Nothing was sent.";

        // A form for a channel the server doesn't choose now, as after /admin set_channels changed its choices, which could
        // choose it again later
        public const string NotChosenNow = "That channel isn't one of the channels this server chose for MinerBot's posts now. Nothing was sent.";

        // A form sent for a server the page doesn't offer actions for, such as one the bot left since the page was drawn
        public static string NotOffered(ServerActionState state) => Line(state) + " Nothing was sent.";

        // The leave page's words
        public const string LeaveLink = "Leave this server";
        public const string NothingToLeave = "MinerBot isn't in this server, so there's nothing to leave.";
        public const string LeaveOff = "Requests to the bot are off, so a leave isn't offered.";

        public const string WhatALeaveDoes = "What a leave does: the bot leaves the server, Discord tells it so, and it then deletes this server's channel "
            + "settings, its armor, maps, lobbies and announcements channels on the website, its copy of the server's channels, and its switches and delivery "
            + "status, and drops the posts that wait for it, as when a server removes MinerBot. The server's record and history stay here, marked as left. Only "
            + "a new invite from someone in the server brings MinerBot back, and they then choose their channels again with /admin set_channels. The bot takes "
            + "at most 5 leaves an hour, and sends you a direct message about each.";

        public static string TypeTheServer(string id) => $"Type the server's name as it shows above, or its ID {id}, to confirm";

        public const string LeaveButton = "Leave the server";

        public static ServerActionState State(BotGuildRow? server, bool requestsOn) => server switch
        {
            null or { left_at: not null } => ServerActionState.NotIn,
            { available: false } => ServerActionState.Unavailable,
            _ => requestsOn ? ServerActionState.Offered : ServerActionState.RequestsOff
        };

        public static string Line(ServerActionState state) => state switch
        {
            ServerActionState.NotIn => NotIn,
            ServerActionState.Unavailable => Unavailable,
            ServerActionState.RequestsOff => RequestsOff,
            _ => ""
        };

        // The channels the server chose, each once with every kind it was chosen for, read against the copy: one of this
        // server's channels where the bot can view, send and embed links takes the test message, which is an embed, and the
        // bot checks all three. An announcements channel needs Embed Links for it too, though a plain-text announcement
        // doesn't. The copy keeps only text and announcement channels, so a voice channel's chat is never offered.
        public static IReadOnlyList<TestChannel> TestChannels(ulong guildId, Guild? settings, IReadOnlyDictionary<ulong, BotChannelRow> channels)
        {
            if (settings == null)
                return [];

            var chosen = new List<(ulong Id, List<SettingKind> Kinds)>();

            foreach (var kind in SettingStatuses.Kinds)
            {
                var id = SettingStatuses.ChannelOf(settings, kind);

                if (id == Guild.ChannelOff)
                    continue;

                var index = chosen.FindIndex(channel => channel.Id == id);

                if (index < 0)
                    chosen.Add((id, [kind]));
                else
                    chosen[index].Kinds.Add(kind);
            }

            return [.. chosen.Select(channel => Test(guildId, channel.Id, channel.Kinds, channels))];
        }

        static TestChannel Test(ulong guildId, ulong id, IReadOnlyList<SettingKind> kinds, IReadOnlyDictionary<ulong, BotChannelRow> channels)
        {
            if (!channels.TryGetValue(id, out var channel) || channel.guild_id != guildId)
                return new TestChannel(id, kinds, null, NotThisServers);

            var missing = new List<string>();

            if (!channel.can_view)
                missing.Add(SettingStatuses.ViewChannel);

            if (!channel.can_send)
                missing.Add(SettingStatuses.SendMessages);

            if (!channel.can_embed)
                missing.Add(SettingStatuses.EmbedLinks);

            var why = missing switch
            {
                [] => null,
                [SettingStatuses.EmbedLinks] when !kinds.Any(SettingStatuses.NeedsEmbed) => NeedsEmbed,
                _ => Missing(missing)
            };

            return new TestChannel(id, kinds, channel, why);
        }

        // The kinds a channel was chosen for, as in "Armor and maps"
        public static string KindsText(IReadOnlyList<SettingKind> kinds)
        {
            var words = kinds.Select((kind, index) => index == 0 ? SettingStatuses.KindLabel(kind) : SettingStatuses.KindLabel(kind).ToLowerInvariant()).ToList();
            return Join(words);
        }

        // An option's label, "Armor and maps: #general": text alone, since an option can't hold markup, so the name's hidden
        // and direction-changing characters are written as their codes, and the page encodes the rest
        public static string Label(TestChannel channel) => $"{KindsText(channel.Kinds)}: #{TextInspector.Reveal(channel.Channel?.name ?? "")}";

        // As in "View Channel, Send Messages and Embed Links"
        static string Join(IReadOnlyList<string> words) => words.Count switch
        {
            0 => "",
            1 => words[0],
            _ => string.Join(", ", words.Take(words.Count - 1)) + " and " + words[^1]
        };
    }
}
