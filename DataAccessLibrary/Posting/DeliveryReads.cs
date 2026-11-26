using DataAccessLibrary.Posting.Models;

namespace DataAccessLibrary.Posting
{
    // The dashboard's reads of the bot's posts, which only the bot writes: each server's delivery status, the servers whose
    // posts keep failing, a server's latest messages and its last post of each kind, and where one item went. All in
    // MinerBot's own database, so the pages show them while the bot is down. Every read is by a key or an index, has a
    // limit, and has its inputs checked before any SQL.
    public class DeliveryReads : DbBaseConnection, IDeliveryReads
    {
        // The most servers one read of statuses covers, as many as the dashboard lists, and so the most rows it reads
        public const int GuildLimit = 500;
        public const int StatusLimit = GuildLimit * 3;

        // The most rows of one item, a row a server, and for a map one an update
        public const int ItemLimit = 2000;

        private readonly DatabaseNames _names;

        public DeliveryReads(ISQLConnectionFactory connectionFactory, DatabaseNames names) : base(connectionFactory, DBConnectionName.Bot)
        {
            _names = names;
        }

        // The statuses of these servers, a row for each kind each has tried, by server and kind
        public async Task<IReadOnlyList<DeliveryStatusRow>> ListStatuses(IReadOnlyCollection<ulong> guildIds)
        {
            ArgumentNullException.ThrowIfNull(guildIds);

            if (guildIds.Count > GuildLimit)
                throw new ArgumentException($"One read covers at most {GuildLimit} servers", nameof(guildIds));

            if (guildIds.Count == 0)
                return [];

            string sql = $@"SELECT {NotificationData.StatusColumns}
                            FROM {_names.Bot}.guild_delivery_status
                            WHERE guild_id IN @guildIds
                            ORDER BY guild_id, source
                            LIMIT {StatusLimit}";

            return (await LoadDataEnum<DeliveryStatusRow, object>(sql, new { guildIds }).ConfigureAwait(false)).ToList();
        }

        // Every status whose posts failed at least this many tries in a row, the longest failing first, whichever server it is
        // and whether its kind is on: the page decides that from the switches and the bot's copy of its servers
        public async Task<IReadOnlyList<DeliveryStatusRow>> ListFailing(uint minFailures)
        {
            if (minFailures == 0)
                throw new ArgumentException("A status that keeps failing has failed at least once", nameof(minFailures));

            string sql = $@"SELECT {NotificationData.StatusColumns}
                            FROM {_names.Bot}.guild_delivery_status
                            WHERE failing_since IS NOT NULL AND failures >= @minFailures
                            ORDER BY failing_since, guild_id, source
                            LIMIT {StatusLimit}";

            return (await LoadDataEnum<DeliveryStatusRow, object>(sql, new { minFailures }).ConfigureAwait(false)).ToList();
        }

        // A server's latest messages and refusals, newest first
        public async Task<IReadOnlyList<DeliveryRow>> ListRecentPosts(ulong guildId, int take)
        {
            if (take is < 1 or > MaxPageSize)
                throw new ArgumentOutOfRangeException(nameof(take), take, $"From 1 to {MaxPageSize} posts");

            string sql = $@"SELECT {NotificationData.DeliveryColumns}
                            FROM {_names.Bot}.notification_deliveries
                            WHERE guild_id = @guildId
                            ORDER BY attempted_at DESC, id DESC
                            LIMIT @take";

            return (await LoadDataEnum<DeliveryRow, object>(sql, new { guildId, take }).ConfigureAwait(false)).ToList();
        }

        // A server's latest message that went of each kind, a row at most for each, however many messages of other kinds came
        // after it: the last post a server's page shows for a kind whose status holds none, as after a leave, which deletes the
        // statuses while the record of the messages stays for 90 days. Each kind's is read back by the server's index only as
        // far as that message.
        public async Task<IReadOnlyList<DeliveryRow>> ListLastPosts(ulong guildId)
        {
            string Latest(string source) => $@"(SELECT {NotificationData.DeliveryColumns}
                                                FROM {_names.Bot}.notification_deliveries
                                                WHERE guild_id = @guildId AND source = @{source} AND state = @sent
                                                ORDER BY attempted_at DESC, id DESC
                                                LIMIT 1)";

            string sql = $"{Latest("armor")} UNION ALL {Latest("maps")} UNION ALL {Latest("lobbies")}";

            return (await LoadDataEnum<DeliveryRow, object>(sql, new { guildId, armor = PostSource.Armor, maps = PostSource.Maps, lobbies = PostSource.Lobbies, sent = DeliveryState.Sent })
                .ConfigureAwait(false)).ToList();
        }

        // Where an item went: the bot's seen row and each server's row in the queue, with the message that holds or carried
        // it and the server's wait. In a transaction, such as the armor review's on the website's connection, it sees that
        // transaction's moment, since MinerBot's tables are named with their database.
        public async Task<ItemPosts> GetItemPosts(string source, ulong itemId, DataTransaction? transaction = null)
        {
            PostingChecks.RequireSource(source);

            var bot = _names.Bot;

            string seen = $@"SELECT {NotificationData.SeenColumns}
                             FROM {bot}.notification_seen
                             WHERE source = @source AND item_id = @itemId";

            string servers = $@"SELECT q.id AS queue_id, q.guild_id, q.revision, q.is_new, q.queued_at, q.state, q.reason, q.settled_at,
                                d.state AS delivery_state, d.channel_id, d.message_id, d.attempted_at, s.next_try_at
                                FROM {bot}.notification_queue q
                                LEFT JOIN {bot}.notification_deliveries d ON d.id = q.delivery_id
                                LEFT JOIN {bot}.guild_delivery_status s ON s.guild_id = q.guild_id AND s.source = q.source
                                WHERE q.source = @source AND q.item_id = @itemId
                                ORDER BY q.guild_id, q.revision, q.id
                                LIMIT {ItemLimit}";

            var parameters = new { source, itemId };

            if (transaction != null)
            {
                return new ItemPosts(await transaction.QueryFirst<SeenRow>(seen, parameters).ConfigureAwait(false),
                    await transaction.Query<ItemServerRow>(servers, parameters).ConfigureAwait(false));
            }

            return new ItemPosts(await LoadFirstData<SeenRow?, object>(seen, parameters).ConfigureAwait(false),
                (await LoadDataEnum<ItemServerRow, object>(servers, parameters).ConfigureAwait(false)).ToList());
        }
    }
}
