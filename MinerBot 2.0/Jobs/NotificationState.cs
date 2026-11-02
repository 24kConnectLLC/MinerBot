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
        public SeenItems<PublishedFileDetailsModel> Maps { get; } = new("maps", map => map.PublishedFileId);
        public SeenItems<LobbyInfo> Lobbies { get; } = new("lobbies", lobby => lobby.LobbyId);

        public bool GuildEventsHooked { get; set; }

        // When the oldest map of the first good read was last updated, see GuildNotificationJob.GetNewMaps
        public DateTime? MapBaselineTime { get; set; }
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
}
