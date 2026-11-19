using DataAccessLibrary.Guilds.Models;

namespace DataAccessLibrary.Guilds
{
    // The dashboard's reads of the bot's copy of its servers, which only the bot writes: the servers, their channels with
    // the bot's permissions, its joins and leaves, and the daily member counts. Every list has a limit, every input is
    // checked before any SQL, and every window is a UTC time the caller works out from its own clock.
    public class BotGuildReads : DbBaseConnection, IBotGuildReads
    {
        // The most servers the list reads, and so the most channels it looks up for their four settings each. The bot is
        // in 10.
        public const int GuildLimit = 500;
        public const int ChannelLookupLimit = GuildLimit * 4;

        // The most days one read of member counts covers
        public const int DayLimit = 100;

        private readonly DatabaseNames _names;

        public BotGuildReads(ISQLConnectionFactory connectionFactory, DatabaseNames names) : base(connectionFactory, DBConnectionName.Bot)
        {
            _names = names;
        }

        const string GuildColumns = "guild_id, name, icon_hash, member_count, bot_joined_at, first_seen_at, left_at, available, bot_administrator, bot_permissions, updated_at";
        const string ChannelColumns = "channel_id, guild_id, name, type, category, category_id, category_position, `position`, can_view, can_send, can_embed, updated_at";

        // Every server the bot is in or has left, by name
        public async Task<IReadOnlyList<BotGuildRow>> ListGuilds()
        {
            string sql = $@"SELECT {GuildColumns}
                            FROM {_names.Bot}.guilds
                            ORDER BY name, guild_id
                            LIMIT @take";

            return (await LoadDataEnum<BotGuildRow, object>(sql, new { take = GuildLimit }).ConfigureAwait(false)).ToList();
        }

        // Null for a server the bot was never recorded in
        public Task<BotGuildRow?> GetGuild(ulong guildId)
        {
            string sql = $@"SELECT {GuildColumns}
                            FROM {_names.Bot}.guilds
                            WHERE guild_id = @guildId";

            return LoadFirstData<BotGuildRow?, object>(sql, new { guildId });
        }

        // A server's channels in Discord's order: the ones outside a category first, then each category by its position,
        // two of the same name kept apart by their IDs, and each one's channels by their positions, the lower ID first on
        // a tie. MariaDB puts NULL first in an ascending order, which is where the channels outside a category go.
        public async Task<IReadOnlyList<BotChannelRow>> ListChannels(ulong guildId)
        {
            string sql = $@"SELECT {ChannelColumns}
                            FROM {_names.Bot}.guild_channels
                            WHERE guild_id = @guildId
                            ORDER BY category_position, category_id, `position`, channel_id
                            LIMIT @take";

            return (await LoadDataEnum<BotChannelRow, object>(sql, new { guildId, take = BotGuildData.ChannelLimit }).ConfigureAwait(false)).ToList();
        }

        // The channels with these IDs, whichever server each is in, such as the ones the website's settings name
        public async Task<IReadOnlyList<BotChannelRow>> FindChannels(IReadOnlyCollection<ulong> channelIds)
        {
            ArgumentNullException.ThrowIfNull(channelIds);

            if (channelIds.Count > ChannelLookupLimit)
                throw new ArgumentException($"One call looks up at most {ChannelLookupLimit} channels", nameof(channelIds));

            string sql = $@"SELECT {ChannelColumns}
                            FROM {_names.Bot}.guild_channels
                            WHERE channel_id IN @channelIds
                            LIMIT @take";

            // MariaDB takes the empty list Dapper writes, but there's nothing to ask
            if (channelIds.Count == 0)
                return [];

            return (await LoadDataEnum<BotChannelRow, object>(sql, new { channelIds, take = channelIds.Count }).ConfigureAwait(false)).ToList();
        }

        // The bot's joins and leaves of one server, newest first
        public async Task<IReadOnlyList<GuildEventRow>> ListGuildEvents(ulong guildId, int take)
        {
            if (take is < 1 or > MaxPageSize)
                throw new ArgumentOutOfRangeException(nameof(take), take, $"From 1 to {MaxPageSize} joins and leaves");

            string sql = $@"SELECT id, guild_id, at, kind, detail
                            FROM {_names.Bot}.guild_events
                            WHERE guild_id = @guildId
                            ORDER BY at DESC, id DESC
                            LIMIT @take";

            return (await LoadDataEnum<GuildEventRow, object>(sql, new { guildId, take }).ConfigureAwait(false)).ToList();
        }

        // One server's member counts from the first day given up to the second, oldest first
        public async Task<IReadOnlyList<GuildDayRow>> ListMemberDays(ulong guildId, DateTime from, DateTime to)
        {
            RequireDay(from, nameof(from));
            RequireDay(to, nameof(to));

            if (to < from || to - from > TimeSpan.FromDays(DayLimit))
                throw new ArgumentException($"One read covers from 0 to {DayLimit} days", nameof(to));

            // A DateTime, because MySql.Data sends a DateOnly as the culture's short date, which MariaDB refuses
            string sql = $@"SELECT day, guild_id, member_count
                            FROM {_names.Bot}.guild_daily
                            WHERE guild_id = @guildId AND day >= @from AND day < @to
                            ORDER BY day
                            LIMIT @take";

            return (await LoadDataEnum<GuildDayRow, object>(sql, new { guildId, from, to, take = DayLimit }).ConfigureAwait(false)).ToList();
        }

        // Each server's first and last member count from the day given on, for its trend: one row for a server counted on
        // one day only, two otherwise, the first first
        public async Task<IReadOnlyList<GuildDayRow>> MemberTrends(DateTime from)
        {
            RequireDay(from, nameof(from));

            string sql = $@"SELECT day, guild_id, member_count
                            FROM (SELECT day, guild_id, member_count,
                                ROW_NUMBER() OVER (PARTITION BY guild_id ORDER BY day) AS first_n,
                                ROW_NUMBER() OVER (PARTITION BY guild_id ORDER BY day DESC) AS last_n
                                FROM {_names.Bot}.guild_daily
                                WHERE day >= @from) days
                            WHERE first_n = 1 OR last_n = 1
                            ORDER BY guild_id, day
                            LIMIT @take";

            return (await LoadDataEnum<GuildDayRow, object>(sql, new { from, take = GuildLimit * 2 }).ConfigureAwait(false)).ToList();
        }

        static void RequireDay(DateTime day, string name)
        {
            RequireUtc(day, name);

            if (day.TimeOfDay != TimeSpan.Zero)
                throw new ArgumentException("This is a day's start, 00:00 UTC", name);
        }
    }
}
