using System.Globalization;
using System.Text.RegularExpressions;
using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Guilds.Models;
using MinerBot.Dashboard.Status;

namespace MinerBot.Dashboard.Servers
{
    // A server's members over a stretch: the first count in it and the last, so the change between them, and whether the
    // first is from the stretch's start or only from when the counting began
    public sealed record MemberTrend(DateTime From, uint FromCount, DateTime To, uint ToCount, bool SinceFirstCount)
    {
        public long Change => (long)ToCount - FromCount;
    }

    // One server as the servers list shows it, with its four settings
    public sealed record ServerRow(BotGuildRow Guild, IReadOnlyList<SettingStatus> Settings, MemberTrend? Trend)
    {
        public bool IsIn => Guild.left_at == null;
        public bool Unavailable => IsIn && !Guild.available;
        public bool NotSetUp => IsIn && Settings.All(setting => setting.State == SettingState.NotSetUp);
        public bool HasProblems => Unavailable || Settings.Any(setting => SettingStatuses.IsProblem(setting.State));
        public bool CantCheck => Settings.Any(setting => setting.State == SettingState.CantCheck);
        public string IdText => Guild.guild_id.ToString(CultureInfo.InvariantCulture);
        public string? Link => ServerRows.Link(Guild.guild_id);
        public string? IconUrl => ServerRows.IconUrl(Guild.guild_id, Guild.icon_hash);
    }

    // A filter of the servers list: the word its link carries, its label, and which rows it shows
    public sealed record ServerFilter(string Word, string Label, Func<ServerRow, bool> Shows);

    internal static partial class ServerRows
    {
        // The servers the bot is in first, because that's what the bot works on
        public static readonly IReadOnlyList<ServerFilter> Filters =
        [
            new("in", "In", row => row.IsIn),
            new("not-set-up", "Not set up", row => row.NotSetUp),
            new("problems", "Has problems", row => row.IsIn && row.HasProblems),
            new("cant-check", "Can't check", row => row.IsIn && row.CantCheck),
            new("left", "Left", row => !row.IsIn),
            new("all", "All", _ => true)
        ];

        public static ServerFilter Default => Filters[0];

        // What the Administrator warning asks for instead, the README's recommended permissions: the posts need the first
        // three, and the pin command reads a channel's pins and pins with the bot's own permission, so a role without the
        // other two breaks it. /admin announce posts an admin's text with Discord's default mentions, so its @everyone,
        // @here and pings of a role that isn't mentionable notify no one unless the bot also has Mention Everyone, which
        // the five leave out and Administrator includes.
        public const string AdministratorAdvice = "MinerBot works with the five permissions its README recommends: View Channel, Send Messages and Embed Links where it posts, and Read Message History and Pin Messages for its pin command. Its /admin announce also needs Mention Everyone to ping @everyone, @here or a role that isn't mentionable. A role with just those is safer.";

        // No word is the default filter. Null for a word that isn't a filter's, which the page answers with a 404.
        public static ServerFilter? FromWord(string? word) =>
            string.IsNullOrEmpty(word) ? Default : Filters.FirstOrDefault(filter => string.Equals(filter.Word, word, StringComparison.OrdinalIgnoreCase));

        public static string FilterLink(ServerFilter filter) => filter == Default ? "/admin/servers" : "/admin/servers?show=" + filter.Word;

        // A server's page, only for an ID its route takes: every Discord ID does, but a row typed in by hand may not
        public static string? Link(ulong guildId) => guildId is > 0 and <= long.MaxValue ? "/admin/servers/" + guildId.ToString(CultureInfo.InvariantCulture) : null;

        // Discord's CDN at 64 pixels, made only from the ID and a hash in Discord's form, so a row written by hand can't
        // point the page anywhere else. Null for a server with no icon, which gets a plain circle.
        public static string? IconUrl(ulong guildId, string? hash) =>
            hash != null && IconHash().IsMatch(hash) ? $"https://cdn.discordapp.com/icons/{guildId.ToString(CultureInfo.InvariantCulture)}/{hash}.png?size=64" : null;

        // Every server the copy has, each with its four settings read against the channels the copy has, and its members'
        // trend over the last 30 days or since the first count
        public static IReadOnlyList<ServerRow> Rows(IReadOnlyList<BotGuildRow> guilds, IReadOnlyList<Guild> settings, IReadOnlyList<BotChannelRow> channels,
            IReadOnlyList<GuildDayRow> trends, DateTime trendFrom)
        {
            var settingsById = settings.GroupBy(row => row.guild_id).ToDictionary(group => group.Key, group => group.First());
            var channelsById = channels.GroupBy(row => row.channel_id).ToDictionary(group => group.Key, group => group.First());
            var trendsById = trends.GroupBy(row => row.guild_id).ToDictionary(group => group.Key, group => Trend(group.ToList(), trendFrom));

            return guilds.Select(guild => new ServerRow(guild,
                SettingStatuses.Of(guild.guild_id, guild.left_at == null, settingsById.GetValueOrDefault(guild.guild_id), channelsById),
                trendsById.GetValueOrDefault(guild.guild_id))).ToList();
        }

        // The channel IDs the settings of the servers the bot is in name, for one lookup
        public static IReadOnlyList<ulong> SettingChannels(IReadOnlyList<BotGuildRow> guilds, IReadOnlyList<Guild> settings)
        {
            var inIds = guilds.Where(guild => guild.left_at == null).Select(guild => guild.guild_id).ToHashSet();

            return settings.Where(row => inIds.Contains(row.guild_id))
                .SelectMany(row => SettingStatuses.Kinds.Select(kind => SettingStatuses.ChannelOf(row, kind)))
                .Where(id => id != Guild.ChannelOff)
                .Distinct()
                .ToList();
        }

        // The website's settings of servers the bot isn't in, having left them or never been recorded in them, whose posts
        // the bot skips
        public static IReadOnlyList<Guild> SettingsElsewhere(IReadOnlyList<BotGuildRow> guilds, IReadOnlyList<Guild> settings)
        {
            var inIds = guilds.Where(guild => guild.left_at == null).Select(guild => guild.guild_id).ToHashSet();
            return settings.Where(row => !inIds.Contains(row.guild_id)).ToList();
        }

        // From a server's first and last count in the stretch, which MemberTrends reads
        public static MemberTrend? Trend(IReadOnlyList<GuildDayRow> days, DateTime from)
        {
            var counted = days.Where(day => day.member_count != null).OrderBy(day => day.day).ToList();

            if (counted.Count == 0)
                return null;

            var first = counted[0];
            var last = counted[^1];

            return new MemberTrend(first.day, first.member_count!.Value, last.day, last.member_count!.Value, first.day > from);
        }

        // Such as up 5 over 30 days, or no change since 2026-11-18
        public static string TrendWords(MemberTrend trend, int days)
        {
            var change = trend.Change switch
            {
                > 0 => "up " + trend.Change.ToString("N0", CultureInfo.InvariantCulture),
                < 0 => "down " + (-trend.Change).ToString("N0", CultureInfo.InvariantCulture),
                _ => "no change"
            };

            if (trend.From == trend.To)
                return "first counted on " + TimeWords.Day(trend.From);

            return trend.SinceFirstCount ? $"{change} since {TimeWords.Day(trend.From)}" : $"{change} over {days} days";
        }

        [GeneratedRegex("^(a_)?[0-9a-f]{32}\\z", RegexOptions.CultureInvariant)]
        private static partial Regex IconHash();
    }
}
