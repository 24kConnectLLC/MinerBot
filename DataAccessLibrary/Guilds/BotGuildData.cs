using DataAccessLibrary.Guilds.Models;
using System.Globalization;

namespace DataAccessLibrary.Guilds
{
    // The servers the bot is in or has left, their text and announcement channels with the bot's permissions in each, the
    // bot's joins and leaves, and each server's member count once a day, in MinerBot's own database, which only the bot
    // writes, with its own login. A server's row and its channels are written in one transaction, and so is a leave. The
    // bot writes again whatever it isn't sure went through, so every write is safe to repeat: the rows are upserts, and a
    // join or a leave is recorded by what the server's row says under its lock, so one that's written again lands once.
    public class BotGuildData : DbBaseConnection, IBotGuildData
    {
        // The most rows one statement writes, so one statement never grows large
        public const int BatchLimit = 100;

        // Discord's own limit of channels in a server, of every kind
        public const int ChannelLimit = 500;

        const int NameLimit = 100;
        const int IconLimit = 64;
        const int DetailLimit = 300;

        private readonly DatabaseNames _names;

        public BotGuildData(ISQLConnectionFactory connectionFactory, DatabaseNames names) : base(connectionFactory, DBConnectionName.Bot)
        {
            _names = names;
        }

        // The servers the table says the bot is in
        public Task<IEnumerable<ulong>> ListGuildsIn()
        {
            string sql = $@"SELECT guild_id
                            FROM {_names.Bot}.guilds
                            WHERE left_at IS NULL";

            return LoadDataEnum<ulong, object?>(sql, null);
        }

        // The server's row and its channels, in one transaction: the row upserted, its channels upserted, and the channels it
        // no longer has deleted. A server new to the table, or back in it after the bot left, is recorded as joined, at the
        // time Discord says the bot joined, and its row takes the member count Discord sent with it. True when it was.
        public Task<bool> SaveGuild(BotGuild guild, IReadOnlyList<BotGuildChannel> channels, DateTime at, ulong runId, uint seq)
        {
            ArgumentNullException.ThrowIfNull(guild);
            ArgumentNullException.ThrowIfNull(channels);
            RequireUtc(at, nameof(at));
            RequireUtcOrNull(guild.bot_joined_at, nameof(guild.bot_joined_at));
            RequireSeq(seq);

            // A hash cut short would be another icon, or none
            if (guild.icon_hash != null && CodePoints(guild.icon_hash) > IconLimit)
                throw new ArgumentException($"A server's icon hash holds at most {IconLimit} characters", nameof(guild));

            if (string.IsNullOrEmpty(guild.name))
                throw new ArgumentException("A server's row needs its name", nameof(guild));

            if (channels.Count > ChannelLimit)
                throw new ArgumentException($"A server has at most {ChannelLimit} channels", nameof(channels));

            foreach (var channel in channels)
            {
                if (channel == null || channel.name == null)
                    throw new ArgumentException("A channel's row needs its name", nameof(channels));

                if (!GuildChannelType.IsKnown(channel.type))
                    throw new ArgumentException($"A channel's type is one of: {string.Join(", ", GuildChannelType.All)}", nameof(channels));
            }

            // Every statement names the database, here, so a missing BotConnection fails before any connection is tried
            var bot = _names.Bot;

            string lockRow = LockRow(bot);

            // first_seen_at stays as it is, and member_count only changes for a server new to the table or back in it, the
            // only times Discord.Net's count is right
            string upsert(bool joining) => $@"INSERT INTO {bot}.guilds (guild_id, name, icon_hash, member_count, bot_joined_at, first_seen_at, left_at, available,
                                              bot_administrator, bot_permissions, updated_at)
                                              VALUES (@guild_id, @name, @icon_hash, @member_count, @bot_joined_at, @at, NULL, @available,
                                              @bot_administrator, @bot_permissions, @at)
                                              ON DUPLICATE KEY UPDATE name = VALUES(name), icon_hash = VALUES(icon_hash),
                                              {(joining ? "member_count = VALUES(member_count), " : "")}bot_joined_at = VALUES(bot_joined_at), left_at = NULL,
                                              available = VALUES(available), bot_administrator = VALUES(bot_administrator),
                                              bot_permissions = VALUES(bot_permissions), updated_at = VALUES(updated_at)";

            var row = new
            {
                guild.guild_id,
                name = Clip(guild.name, NameLimit),
                guild.icon_hash,
                guild.member_count,
                guild.bot_joined_at,
                at,
                guild.available,
                guild.bot_administrator,
                guild.bot_permissions
            };

            var batches = channels.Chunk(BatchLimit).Select(batch =>
            {
                var (values, parameters) = Rows(batch,
                    ("channel_id", channel => channel.channel_id),
                    ("guild_id", _ => guild.guild_id),
                    ("name", channel => Clip(channel.name, NameLimit)),
                    ("type", channel => channel.type),
                    ("category", channel => Clip(channel.category, NameLimit)),
                    ("category_id", channel => channel.category_id),
                    ("category_position", channel => channel.category_position),
                    ("position", channel => channel.position),
                    ("can_view", channel => channel.can_view),
                    ("can_send", channel => channel.can_send),
                    ("can_embed", channel => channel.can_embed),
                    ("updated_at", _ => at));

                string sql = $@"INSERT INTO {bot}.guild_channels (channel_id, guild_id, name, type, category, category_id, category_position, `position`, can_view,
                                can_send, can_embed, updated_at)
                                VALUES {values}
                                ON DUPLICATE KEY UPDATE guild_id = VALUES(guild_id), name = VALUES(name), type = VALUES(type), category = VALUES(category),
                                category_id = VALUES(category_id), category_position = VALUES(category_position), `position` = VALUES(`position`),
                                can_view = VALUES(can_view), can_send = VALUES(can_send), can_embed = VALUES(can_embed), updated_at = VALUES(updated_at)";

                return (Sql: sql, Parameters: parameters);
            }).ToList();

            // NOT IN an empty list matches every row, so a server without channels has a statement of its own, rather than one
            // that only the server's ID beside it keeps from deleting every server's channels
            string deleteGone = channels.Count == 0
                ? $@"DELETE FROM {bot}.guild_channels
                     WHERE guild_id = @guild_id"
                : $@"DELETE FROM {bot}.guild_channels
                     WHERE guild_id = @guild_id AND channel_id NOT IN @keep";

            var gone = new { guild.guild_id, keep = channels.Select(channel => channel.channel_id).ToArray() };
            string addEvent = AddEvent(bot);

            return InTransaction(async transaction =>
            {
                var before = await transaction.QueryFirst<GuildRow>(lockRow, new { guild.guild_id }).ConfigureAwait(false);
                var joining = before == null || before.left_at != null;

                await transaction.Execute(upsert(joining), row).ConfigureAwait(false);

                foreach (var (sql, parameters) in batches)
                    await transaction.Execute(sql, parameters).ConfigureAwait(false);

                await transaction.Execute(deleteGone, gone).ConfigureAwait(false);

                if (joining)
                {
                    var joinedAt = JoinedAt(guild.bot_joined_at, before?.left_at, at);

                    await transaction.Execute(addEvent, new
                    {
                        run_id = runId,
                        seq,
                        guild.guild_id,
                        at = joinedAt,
                        kind = GuildEventKind.Joined,
                        detail = before?.left_at is { } left ? Again(left, joinedAt) : "first time"
                    }).ConfigureAwait(false);
                }

                return joining;
            });
        }

        // A server the bot left, in one transaction: its row marked left and its channels deleted. Recorded as left only
        // when its row says the bot is in it, so a leave that's written again, or one of a server the bot never wrote,
        // records nothing. True when it was recorded.
        public Task<bool> LeaveGuild(ulong guildId, DateTime at, ulong runId, uint seq, string? detail)
        {
            RequireUtc(at, nameof(at));
            RequireSeq(seq);

            var bot = _names.Bot;

            string lockRow = LockRow(bot);

            string leave = $@"UPDATE {bot}.guilds
                              SET left_at = @at, updated_at = @at
                              WHERE guild_id = @guild_id AND left_at IS NULL";

            string deleteChannels = $@"DELETE FROM {bot}.guild_channels
                                       WHERE guild_id = @guild_id";

            string addEvent = AddEvent(bot);
            var leftDetail = Clip(detail, DetailLimit);

            return InTransaction(async transaction =>
            {
                var before = await transaction.QueryFirst<GuildRow>(lockRow, new { guild_id = guildId }).ConfigureAwait(false);
                var leaving = before != null && before.left_at == null;

                if (leaving)
                {
                    await transaction.Execute(leave, new { guild_id = guildId, at }).ConfigureAwait(false);
                    await transaction.Execute(addEvent, new { run_id = runId, seq, guild_id = guildId, at, kind = GuildEventKind.Left, detail = leftDetail }).ConfigureAwait(false);
                }

                await transaction.Execute(deleteChannels, new { guild_id = guildId }).ConfigureAwait(false);

                return leaving;
            });
        }

        // Servers Discord still listed as unavailable when the bot's session became ready, in an outage, of which the bot
        // has nothing but their IDs: only their rows are marked unavailable, and their channels stay as they were
        public Task<int> MarkUnavailable(IReadOnlyCollection<ulong> guildIds, DateTime at)
        {
            ArgumentNullException.ThrowIfNull(guildIds);
            RequireUtc(at, nameof(at));

            if (guildIds.Count > BatchLimit)
                throw new ArgumentException($"One call marks at most {BatchLimit} servers", nameof(guildIds));

            if (guildIds.Count == 0)
                return Task.FromResult(0);

            string sql = $@"UPDATE {_names.Bot}.guilds
                            SET available = 0, updated_at = @at
                            WHERE guild_id IN @guildIds AND left_at IS NULL AND available = 1";

            return SaveDataCount(sql, new { guildIds, at });
        }

        // Discord's approximate member count of a server, in its row and in the day's row of its trend, together. The day is
        // UTC, from its start.
        public Task SaveMemberCount(ulong guildId, uint memberCount, DateTime day, DateTime at)
        {
            RequireUtc(at, nameof(at));
            RequireUtc(day, nameof(day));

            if (day.TimeOfDay != TimeSpan.Zero)
                throw new ArgumentException("This is a day's start, 00:00 UTC", nameof(day));

            var bot = _names.Bot;

            string update = $@"UPDATE {bot}.guilds
                               SET member_count = @memberCount, updated_at = @at
                               WHERE guild_id = @guildId";

            // MySql.Data sends a DateOnly as the culture's short date, which MariaDB refuses, so the day goes as a time
            string daily = $@"INSERT INTO {bot}.guild_daily (day, guild_id, member_count)
                              VALUES (@day, @guildId, @memberCount)
                              ON DUPLICATE KEY UPDATE member_count = VALUES(member_count)";

            return InTransaction(async transaction =>
            {
                await transaction.Execute(update, new { guildId, memberCount, at }).ConfigureAwait(false);
                await transaction.Execute(daily, new { day, guildId, memberCount }).ConfigureAwait(false);

                return true;
            });
        }

        // Locked until the transaction ends, so a join or a leave that this decides can't be decided twice. A server that
        // has no row reads as null.
        static string LockRow(string bot) => $@"SELECT guild_id, left_at
                                                FROM {bot}.guilds
                                                WHERE guild_id = @guild_id
                                                FOR UPDATE";

        // Not INSERT IGNORE, which would also let a detail that's too long through, cut, without an error. An event that's
        // already there stays as it is.
        static string AddEvent(string bot) => $@"INSERT INTO {bot}.guild_events (run_id, seq, guild_id, at, kind, detail)
                                                 VALUES (@run_id, @seq, @guild_id, @at, @kind, @detail)
                                                 ON DUPLICATE KEY UPDATE id = id";

        // When Discord says the bot joined, unless that's in the future or from before the bot last left, which would put
        // the join out of order: then the time of the write
        static DateTime JoinedAt(DateTime? joined, DateTime? left, DateTime at)
        {
            if (joined is not { } time || time > at)
                return at;

            return left is { } leftAt && time < leftAt ? at : time;
        }

        static string Again(DateTime left, DateTime joined)
        {
            var days = (int)Math.Max(0, Math.Floor((joined - left).TotalDays));

            return days switch
            {
                0 => "again, after less than a day away",
                1 => "again, after 1 day away",
                _ => string.Create(CultureInfo.InvariantCulture, $"again, after {days} days away")
            };
        }

        // The server's row as the locked read finds it
        internal sealed class GuildRow
        {
            public ulong guild_id { get; set; }
            public DateTime? left_at { get; set; }
        }
    }
}
