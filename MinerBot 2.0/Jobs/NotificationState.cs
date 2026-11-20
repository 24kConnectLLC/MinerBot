using DataAccessLibrary.Armor.Models;
using Steam.Models;
using SteamWebAPIAccess.Models;

namespace MinerBot_2._0.Jobs
{
    // JobHandler makes a new GuildNotificationJob for every run, so what it has seen is kept here
    public class NotificationState
    {
        // An hour of runs, and without a limit an item that can never be sent would hold up its source for good
        public const int MaxFailedRuns = 12;

        public SeenItems<ArmorStyle> Armor { get; } = new("armor styles", armor => (ulong)armor.ID);
        public SeenMaps Maps { get; } = new();
        public SeenItems<LobbyInfo> Lobbies { get; } = new("lobbies", lobby => lobby.LobbyId);
    }

    public class SeenItems<T>(string name, Func<T, ulong> getID)
    {
        private readonly HashSet<ulong> seenIDs = new();
        private bool hasBaseline;
        private Dictionary<ulong, int> failedRuns = new();

        public int Count => seenIDs.Count;

        // Only the first good read of a source records anything, because all of it was there before the bot looked
        public List<T> FindNew(List<T> items)
        {
            if (!hasBaseline)
            {
                seenIDs.UnionWith(items.Select(getID));
                hasBaseline = true;
            }

            return items.Where(item => !seenIDs.Contains(getID(item))).DistinctBy(getID).ToList();
        }

        // For after the guild loop, with how many of the new items reached a guild. The rest aren't recorded, so the next
        // run finds them again. Runs are counted per item, so an item that has only just appeared isn't dropped with an older one.
        public void Commit(List<T>? newItems, int delivered)
        {
            if (newItems == null)
                return;

            seenIDs.UnionWith(newItems.Take(delivered).Select(getID));

            failedRuns = newItems.Skip(delivered).Select(getID).ToDictionary(id => id, id => failedRuns.GetValueOrDefault(id) + 1);

            var droppedIDs = failedRuns.Where(item => item.Value >= NotificationState.MaxFailedRuns).Select(item => item.Key).ToList();

            if (droppedIDs.Count > 0)
            {
                Console.WriteLine($"Gave up on {droppedIDs.Count} new {name} after {NotificationState.MaxFailedRuns} runs that reached no guild");
                seenIDs.UnionWith(droppedIDs);
            }
        }
    }

    // A map to post, and the update time it's recorded with. That's a copy, because /maps latest shares the map's object.
    public record MapPost(PublishedFileDetailsModel Map, bool IsNew, DateTime UpdatedAt);

    // Maps are posted when they're new and again when they're updated, so each one's last update is kept, and the time of
    // its last post for the cooldown. Steam's times are only compared with Steam's, and the bot's time only with the bot's.
    public class SeenMaps
    {
        // So a map that's updated again and again is posted once a day at most
        public static readonly TimeSpan Cooldown = TimeSpan.FromHours(24);

        // A run reads the latest maps, as /maps latest shows them
        public const int LatestCount = 5;

        record SeenMap(DateTime UpdatedAt, DateTimeOffset? PostedAt);

        private readonly Dictionary<ulong, SeenMap> seen = new();
        private Dictionary<ulong, int> failedRuns = new();

        // The maps that runs found while posts were paused, or before the bot had read whether they were, which wait for a run
        // that posts, and the ones a run that posts didn't deliver when the last of those went out. Each one makes the read one
        // map deeper, so the maps updated after it can't push it out of the read before that run, within the three pages of
        // Steam's list that a read takes at most.
        private readonly HashSet<ulong> held = new();

        // The maps that take places in Steam's list above the maps that wait without waiting themselves, so each makes the read
        // one map deeper too, until no map waits: the ones a read passed over because they were updated again in their
        // cooldown, such as maps posted shortly before a pause, and the rest of what a run that posts found while maps still
        // wait, delivered or not. The last read's passed over are kept until the run says whether it held its posts.
        private readonly HashSet<ulong> passedOver = new();
        private List<ulong> lastPassedOver = new();

        public int Count => seen.Count;

        // How many of the latest maps a run reads: five, and one more for each map that waits or that takes a place above them
        // while they wait
        public int ReadCount => LatestCount + held.Count + passedOver.Count;

        // When the oldest map of the first good read was last updated
        public DateTime? BaselineTime { get; private set; }

        public List<MapPost> FindNew(List<PublishedFileDetailsModel> maps, DateTimeOffset now)
        {
            lastPassedOver = new();

            // Steam's reader leaves a missing update time as the default, year 1. As the baseline that would make every later
            // map look new, so a map without one is left out of the read.
            maps = maps.Where(map => map.TimeUpdated != default).ToList();

            // Only the first good read records anything, because all of it was there before the bot looked.
            // The read is only the top of Steam's ranking by last update, so a map moves into it from below when one above is
            // deleted or hidden. That map is no newer than the oldest one of the first read, unlike a map that was added or updated.
            if (BaselineTime == null)
            {
                // Nothing to set it with, so the next read tries again
                if (maps.Count == 0)
                    return [];

                BaselineTime = maps.Min(map => map.TimeUpdated);

                foreach (var map in maps)
                    seen[map.PublishedFileId] = new(map.TimeUpdated, null);

                return [];
            }

            List<MapPost> posts = new();

            foreach (var map in maps.DistinctBy(map => map.PublishedFileId))
            {
                bool isNew;

                if (seen.TryGetValue(map.PublishedFileId, out var record))
                {
                    if (map.TimeUpdated <= record.UpdatedAt)
                        continue;

                    // Not recorded either, so the first read after the cooldown posts the update
                    if (record.PostedAt is DateTimeOffset postedAt && now - postedAt < Cooldown)
                    {
                        lastPassedOver.Add(map.PublishedFileId);
                        continue;
                    }

                    isNew = false;
                }
                else if (map.TimeCreated > BaselineTime)
                    isNew = true;
                else if (map.TimeUpdated > BaselineTime)
                    isNew = false;
                else
                    continue;

                posts.Add(new(map, isNew, map.TimeUpdated));
            }

            return posts;
        }

        // What a run that held its posts found, which waits for the first run that posts, and while maps wait, the maps its
        // read passed over
        public void Hold(List<MapPost>? posts)
        {
            if (posts == null)
                return;

            held.UnionWith(posts.Select(post => post.Map.PublishedFileId));

            if (held.Count > 0)
                passedOver.UnionWith(lastPassedOver);
        }

        // For after the guild loop, with how many of the maps reached a guild, and when, or no time when no server has maps
        // on. The rest aren't recorded, so the next run finds them again, and runs are counted per map as in SeenItems.
        // Returns how many maps that waited this run didn't find again, which are never posted.
        public int Commit(List<MapPost>? posts, int delivered, DateTimeOffset? postedAt)
        {
            if (posts == null)
                return 0;

            foreach (var post in posts.Take(delivered))
                seen[post.Map.PublishedFileId] = new(post.UpdatedAt, postedAt);

            var undelivered = posts.Skip(delivered).ToList();

            failedRuns = undelivered.ToDictionary(post => post.Map.PublishedFileId, post => failedRuns.GetValueOrDefault(post.Map.PublishedFileId) + 1);

            var dropped = undelivered.Where(post => failedRuns[post.Map.PublishedFileId] >= NotificationState.MaxFailedRuns).ToList();

            if (dropped.Count > 0)
            {
                Console.WriteLine($"Gave up on {dropped.Count} new or updated maps after {NotificationState.MaxFailedRuns} runs that reached no guild");

                // With its update, so it isn't found again, but not as a post. Unlike an armor style or a lobby, it's found
                // again when it's updated, so its count goes too, and that update gets runs of its own.
                foreach (var post in dropped)
                {
                    seen[post.Map.PublishedFileId] = new(post.UpdatedAt, null);
                    failedRuns.Remove(post.Map.PublishedFileId);
                }
            }

            // A map that waited is settled once a run that posts has delivered it, given up on it, or not found it again: it was
            // hidden meanwhile, or more maps were updated while it waited than the three pages of Steam's list a read takes
            // hold. One still to be delivered keeps the read deep enough to reach it, with the maps above it that don't wait.
            var notFound = held.Count(id => !posts.Exists(post => post.Map.PublishedFileId == id));
            var waited = held.Count > 0;
            held.IntersectWith(failedRuns.Keys);

            // When the last maps that waited went out but a later message of the run reached no server, the maps this run didn't
            // deliver wait in their place: they can be below every map that went out, as a map posted shortly before the pause
            // and updated again during it is, and a read of five wouldn't reach them
            if (waited && held.Count == 0)
                held.UnionWith(failedRuns.Keys);

            // While maps still wait, the rest of what this run found counts too, as the maps it passed over do: the ones it
            // delivered are above the ones a later message of the run didn't take, and the read must reach the ones that reached
            // no server without having waited, which are tried again, whether they're newer than the maps that wait, as maps
            // found after the resume are, or older, as a map posted shortly before the pause and updated again during it is
            if (held.Count == 0)
                passedOver.Clear();
            else
            {
                passedOver.UnionWith(lastPassedOver);
                passedOver.UnionWith(posts.Select(post => post.Map.PublishedFileId).Where(id => !held.Contains(id)));
            }

            return notFound;
        }
    }
}
