using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using Discord;
using Discord.WebSocket;
using MinerBot.Shared.Delivery;

namespace MinerBot_2._0.Posting
{
    // Whether a server is owed a kind now, by the website's channel rows, the dashboard's switches and Discord.Net's cache:
    // it has a row, the kind's channel isn't 0, its switch isn't off, which no switches row means, and the bot is in it. The
    // same four decide when an item is found, so a server that's off then gets no row for it, and again before a send, so
    // what waits for a server that's off now is dropped, with the reason. Announcements have no switch: the job never posts
    // them. One for each run, from that run's reads, with no cache, so a switch saved on the dashboard holds from the next run.
    public sealed class GroupCheck
    {
        readonly Dictionary<ulong, Guild> rows = new();
        readonly List<ulong> order = new();
        readonly Dictionary<ulong, GuildSwitches> switches;
        readonly Func<ulong, bool> inServer;

        public GroupCheck(IEnumerable<Guild> channelRows, IEnumerable<GuildSwitches> switchRows, Func<ulong, bool> inServer)
        {
            // The website's table is keyed by the server, so a second row for one can only be a fake's
            foreach (var row in channelRows)
            {
                if (rows.TryAdd(row.guild_id, row))
                    order.Add(row.guild_id);
            }

            switches = switchRows.GroupBy(row => row.guild_id).ToDictionary(same => same.Key, same => same.First());
            this.inServer = inServer;
        }

        public static GroupCheck Of(IEnumerable<Guild> channelRows, IEnumerable<GuildSwitches> switchRows, DiscordSocketClient client) =>
            new(channelRows, switchRows, guildId => client.GetGuild(guildId) != null);

        // Why the server isn't owed the kind now, as the reason its waiting items are dropped with, or null when it is
        public string? OffReason(ulong guildId, string source)
        {
            if (!rows.TryGetValue(guildId, out var row))
                return DeliveryReasons.NoSettings;

            if (ChannelOf(row, source) == Guild.ChannelOff)
                return DeliveryReasons.ChannelOff;

            if (switches.TryGetValue(guildId, out var switched) && !switched.IsOn(source))
                return DeliveryReasons.SwitchedOff;

            if (!inServer(guildId))
                return DeliveryReasons.NotInServer;

            return null;
        }

        // The kind's channel on the server's row, 0 without a row
        public ulong ChannelOf(ulong guildId, string source) => rows.TryGetValue(guildId, out var row) ? ChannelOf(row, source) : Guild.ChannelOff;

        // The version of the server's switches, one more at each save, 0 with none: what a wait started from holds it, so a
        // switch turned off and on again ends the wait
        public uint SwitchesVersion(ulong guildId) => switches.TryGetValue(guildId, out var row) ? row.version : 0;

        // The servers owed each kind now, in the order of the website's rows
        public IReadOnlyDictionary<string, IReadOnlyList<ulong>> Owed() =>
            PostSource.Posted.ToDictionary(source => source, source => (IReadOnlyList<ulong>)order.Where(guildId => OffReason(guildId, source) == null).ToList(),
                StringComparer.Ordinal);

        // The permissions a post needs that the bot lacks in the channel, from the cache, as the mask a refusal keeps: every
        // post is embeds, so View Channel, Send Messages and Embed Links. Discord.Net works these out the way Discord does,
        // apart from timeouts.
        public static int MissingMask(SocketGuildUser bot, IGuildChannel channel)
        {
            var permissions = bot.GetPermissions(channel);

            return (permissions.ViewChannel ? 0 : DeliveryReasons.ViewChannel)
                | (permissions.SendMessages ? 0 : DeliveryReasons.SendMessages)
                | (permissions.EmbedLinks ? 0 : DeliveryReasons.EmbedLinks);
        }

        // Rows past their kind's time limit, which are given up as too old to be news
        public static bool TooOld(QueueRow row, DateTime now) => now - row.queued_at > PostingLimits.GiveUpAfter(row.source);

        static ulong ChannelOf(Guild row, string source) => source switch
        {
            PostSource.Armor => row.armor_channel_id,
            PostSource.Maps => row.map_channel_id,
            PostSource.Lobbies => row.lobby_channel_id,
            _ => Guild.ChannelOff
        };
    }
}
