using DataAccessLibrary.Feed.Models;

namespace DataAccessLibrary.Feed
{
    // What the bot publishes for the website's Lobbies and Maps pages, in three tables of the website's database:
    // mmh_feed_lobbies, mmh_feed_maps and mmh_feed_state. The website makes them, in its own migration, and only reads
    // them. The bot only writes their rows, with its website login, and never makes or changes a table, so until the
    // website has made them every call here fails with MariaDB's error 1146, which the bot's feed writer waits out.
    // Each read of Steam is written in one transaction, with its feed's row in mmh_feed_state, so a page never sees half
    // of one, and every write is safe to repeat. The tables' times are UTC, in whole seconds.
    // The login needs SELECT, INSERT, UPDATE and DELETE on the three tables: the deletes read the rows they choose from.
    public class WebsiteFeedData : DbBaseConnection, IWebsiteFeedData
    {
        // The most rows one statement writes, so one statement never grows large. A map's row can hold a few
        // thousand bytes of description, so fewer of them go in one.
        public const int LobbyBatch = 100;
        public const int MapBatch = 25;

        // The most lobbies and maps one read writes. Steam lists far fewer lobbies, and the bot reads about 30 maps.
        public const int LobbyLimit = 500;
        public const int MapLimit = 100;

        // How many of a read's last maps, in Steam's order, say how far down Steam's list the read reaches: the newest
        // update among them. One or two maps that Steam ranks above their place can then sit at the read's end without
        // the read seeming to reach back to their old updates. The price is that one of the read's last two maps being
        // hidden goes unnoticed, and its row stays.
        public const int ReachMaps = 3;

        // The columns' lengths, in characters as MariaDB counts them
        public const int HostNameLimit = 64;
        public const int MapNameLimit = 128;
        public const int ModesLimit = 255;
        public const int VersionLabelLimit = 64;
        public const int TitleLimit = 255;
        public const int DescriptionLimit = 2000;
        public const int PreviewUrlLimit = 500;
        public const int TagsLimit = 500;

        public WebsiteFeedData(ISQLConnectionFactory connectionFactory) : base(connectionFactory, DBConnectionName.Feed)
        {
        }

        // A good read of Steam's lobby list, which replaces the whole set in one transaction: the lobbies that are open are
        // upserted, and one that was already there keeps its first_seen; the ones that are no longer open are deleted; and
        // the feed's row says when, and how many. Returns how many lobbies it wrote.
        public Task<int> ReplaceLobbies(IReadOnlyList<FeedLobby> lobbies, DateTime at)
        {
            ArgumentNullException.ThrowIfNull(lobbies);
            RequireUtc(at, nameof(at));

            if (lobbies.Count > LobbyLimit)
                throw new ArgumentException($"One read writes at most {LobbyLimit} lobbies", nameof(lobbies));

            foreach (var lobby in lobbies)
            {
                if (lobby == null || lobby.host_name == null || lobby.map_name == null || lobby.modes == null || lobby.version_label == null)
                    throw new ArgumentException("A lobby's row needs its texts, which are empty when they aren't known", nameof(lobbies));

                if (!FeedHostKind.IsKnown(lobby.host_kind))
                    throw new ArgumentException($"A lobby's host kind is one of: {string.Join(", ", FeedHostKind.All)}", nameof(lobbies));
            }

            at = WholeSecond(at);

            // Steam lists a lobby once, and a list that didn't would still write one row for it
            var open = lobbies.DistinctBy(lobby => lobby.lobby_id).ToList();

            var batches = open.Chunk(LobbyBatch).Select(batch =>
            {
                var (values, parameters) = Rows(batch,
                    ("lobby_id", lobby => lobby.lobby_id),
                    ("owner_id", lobby => lobby.owner_id),
                    ("host_name", lobby => Clip(lobby.host_name, HostNameLimit)),
                    ("host_kind", lobby => lobby.host_kind),
                    ("map_name", lobby => Clip(lobby.map_name, MapNameLimit)),
                    ("modes", lobby => Clip(lobby.modes, ModesLimit)),
                    ("version_number", lobby => lobby.version_number),
                    ("version_label", lobby => Clip(lobby.version_label, VersionLabelLimit)),
                    ("is_current", lobby => lobby.is_current),
                    ("players", lobby => lobby.players),
                    ("max_players", lobby => lobby.max_players),
                    ("first_seen", _ => at),
                    ("last_seen", _ => at));

                // first_seen stays as it is for a lobby that was already there
                string sql = $@"INSERT INTO mmh_feed_lobbies (lobby_id, owner_id, host_name, host_kind, map_name, modes, version_number, version_label,
                                is_current, players, max_players, first_seen, last_seen)
                                VALUES {values}
                                ON DUPLICATE KEY UPDATE owner_id = VALUES(owner_id), host_name = VALUES(host_name), host_kind = VALUES(host_kind),
                                map_name = VALUES(map_name), modes = VALUES(modes), version_number = VALUES(version_number),
                                version_label = VALUES(version_label), is_current = VALUES(is_current), players = VALUES(players),
                                max_players = VALUES(max_players), last_seen = VALUES(last_seen)";

                return (Sql: sql, Parameters: parameters);
            }).ToList();

            // NOT IN an empty list matches every row, which is what an answer with no lobbies asks for, but it has a statement
            // of its own, so the one with a list never deletes more than the lobbies that closed
            string deleteClosed = open.Count == 0
                ? @"DELETE FROM mmh_feed_lobbies"
                : @"DELETE FROM mmh_feed_lobbies
                    WHERE lobby_id NOT IN @open";

            var closed = new { open = open.Select(lobby => lobby.lobby_id).ToArray() };
            var state = new { feed = FeedName.Lobbies, at, count = open.Count };

            return InTransaction(async transaction =>
            {
                foreach (var (sql, parameters) in batches)
                    await transaction.Execute(sql, parameters).ConfigureAwait(false);

                await transaction.Execute(deleteClosed, closed).ConfigureAwait(false);
                await transaction.Execute(GoodRead, state).ConfigureAwait(false);

                return open.Count;
            });
        }

        // A good read of Steam's latest public maps, in Steam's order, in one transaction: every map read is upserted, and
        // one that was already there keeps its first_seen. Then the maps that were hidden or removed on Steam since the last
        // good read are deleted: the rows that read held, that this one doesn't, and that this one reaches down to. Every
        // other row stays, as the page's history. Returns how many maps it wrote.
        //
        // The read is the top of Steam's list by last update, so a map it no longer holds either left Steam or was pushed
        // out by newer ones, and only its time tells which. Steam's order isn't exactly time_updated, though: on
        // 23 November 2026, one of the 120 newest public maps stood 27 days of updates above its place. A rule that went
        // by the read's oldest map, and looked at every row, would delete that much of the history each time such a map
        // was in a read. So only rows of the last good read can go, and the read reaches no further than the newest
        // update among its last maps, where a map out of its place would otherwise say how far.
        public Task<int> SaveMaps(IReadOnlyList<FeedMap> maps, DateTime at)
        {
            ArgumentNullException.ThrowIfNull(maps);
            RequireUtc(at, nameof(at));

            // An empty read says nothing about the maps, and reaches nowhere down Steam's list
            if (maps.Count == 0)
                throw new ArgumentException("A read with no maps isn't written. MarkChecked records a read that failed.", nameof(maps));

            if (maps.Count > MapLimit)
                throw new ArgumentException($"One read writes at most {MapLimit} maps", nameof(maps));

            foreach (var map in maps)
            {
                if (map == null || map.title == null || map.preview_url == null || map.tags == null)
                    throw new ArgumentException("A map's row needs its title, picture and tags, which are empty when it has none", nameof(maps));

                RequireUtc(map.time_created, nameof(map.time_created));
                RequireUtc(map.time_updated, nameof(map.time_updated));

                // Steam's reader leaves a missing time as the default, year 1, which the removal rule would compare as if
                // it were a time
                if (map.time_created == default || map.time_updated == default)
                    throw new ArgumentException("A map's row needs the times Steam gave it", nameof(maps));

                // A link or a list of tags cut short would be another link, or end in half a tag
                if (CodePoints(map.preview_url) > PreviewUrlLimit)
                    throw new ArgumentException($"A map's picture link holds at most {PreviewUrlLimit} characters", nameof(maps));

                if (CodePoints(map.tags) > TagsLimit)
                    throw new ArgumentException($"A map's tags hold at most {TagsLimit} characters", nameof(maps));
            }

            at = WholeSecond(at);

            var read = maps.DistinctBy(map => map.file_id).ToList();

            var batches = read.Chunk(MapBatch).Select(batch =>
            {
                var (values, parameters) = Rows(batch,
                    ("file_id", map => map.file_id),
                    ("title", map => Clip(map.title, TitleLimit)),
                    ("description", map => Clip(map.description, DescriptionLimit)),
                    ("preview_url", map => map.preview_url),
                    ("creator_id", map => map.creator_id),
                    ("tags", map => map.tags),
                    ("file_size", map => map.file_size),
                    ("subscriptions", map => map.subscriptions),
                    ("lifetime_subscriptions", map => map.lifetime_subscriptions),
                    ("favorited", map => map.favorited),
                    ("lifetime_favorited", map => map.lifetime_favorited),
                    ("views", map => map.views),
                    ("time_created", map => WholeSecond(map.time_created)),
                    ("time_updated", map => WholeSecond(map.time_updated)),
                    ("first_seen", _ => at),
                    ("last_seen", _ => at));

                // first_seen stays as it is for a map that was already there
                string sql = $@"INSERT INTO mmh_feed_maps (file_id, title, description, preview_url, creator_id, tags, file_size, subscriptions,
                                lifetime_subscriptions, favorited, lifetime_favorited, views, time_created, time_updated, first_seen, last_seen)
                                VALUES {values}
                                ON DUPLICATE KEY UPDATE title = VALUES(title), description = VALUES(description), preview_url = VALUES(preview_url),
                                creator_id = VALUES(creator_id), tags = VALUES(tags), file_size = VALUES(file_size),
                                subscriptions = VALUES(subscriptions), lifetime_subscriptions = VALUES(lifetime_subscriptions),
                                favorited = VALUES(favorited), lifetime_favorited = VALUES(lifetime_favorited), views = VALUES(views),
                                time_created = VALUES(time_created), time_updated = VALUES(time_updated), last_seen = VALUES(last_seen)";

                return (Sql: sql, Parameters: parameters);
            }).ToList();

            // After the upserts, which leave the rows this read doesn't hold as they were, and before the feed's row takes
            // this read's time: a row of the last good read was last seen when the feed last got an answer. A feed that
            // never had one has no such row. The list is never empty here, so NOT IN never matches every row.
            string deleteGone = @"DELETE FROM mmh_feed_maps
                                  WHERE last_seen = (SELECT ok_at FROM mmh_feed_state WHERE feed = @feed)
                                  AND time_updated >= @reach AND file_id NOT IN @read";

            var gone = new
            {
                feed = FeedName.Maps,
                reach = read.TakeLast(ReachMaps).Max(map => WholeSecond(map.time_updated)),
                read = read.Select(map => map.file_id).ToArray()
            };

            var state = new { feed = FeedName.Maps, at, count = read.Count };

            return InTransaction(async transaction =>
            {
                foreach (var (sql, parameters) in batches)
                    await transaction.Execute(sql, parameters).ConfigureAwait(false);

                await transaction.Execute(deleteGone, gone).ConfigureAwait(false);
                await transaction.Execute(GoodRead, state).ConfigureAwait(false);

                return read.Count;
            });
        }

        // A read where Steam gave no answer: only when the bot tried changes. The feed's rows, when it last got an answer
        // and how many rows that wrote stay as they are, and a feed that never had one starts with none.
        public Task MarkChecked(string feed, DateTime at)
        {
            RequireUtc(at, nameof(at));

            if (!FeedName.IsKnown(feed))
                throw new ArgumentException($"A feed is one of: {string.Join(", ", FeedName.All)}", nameof(feed));

            string sql = @"INSERT INTO mmh_feed_state (feed, checked_at, ok_at, item_count)
                           VALUES (@feed, @at, NULL, 0)
                           ON DUPLICATE KEY UPDATE checked_at = VALUES(checked_at)";

            return SaveDataCount(sql, new { feed, at = WholeSecond(at) });
        }

        // The feed's row after a read that was written: tried and answered at the same time
        const string GoodRead = @"INSERT INTO mmh_feed_state (feed, checked_at, ok_at, item_count)
                                  VALUES (@feed, @at, @at, @count)
                                  ON DUPLICATE KEY UPDATE checked_at = VALUES(checked_at), ok_at = VALUES(ok_at), item_count = VALUES(item_count)";

        // The columns hold whole seconds, and whether MariaDB rounds or cuts the rest depends on its sql_mode, so it's cut
        // here and a row reads back as it was written
        static DateTime WholeSecond(DateTime time) => new(time.Ticks - time.Ticks % TimeSpan.TicksPerSecond, time.Kind);
    }
}
