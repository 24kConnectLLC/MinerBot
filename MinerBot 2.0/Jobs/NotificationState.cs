using DataAccessLibrary.Armor.Models;
using Steam.Models;
using SteamWebAPIAccess.Models;

namespace MinerBot_2._0.Jobs
{
    // JobHandler makes a new GuildNotificationJob for every run, so what it has seen is kept here
    public class NotificationState
    {
        // An hour of runs, and without a limit a batch that can never be sent would hold up its source for good
        public const int MaxFailedRuns = 12;

        public SeenItems<ArmorStyle> Armor { get; } = new("armor styles", armor => (ulong)armor.ID);
        public SeenItems<PublishedFileDetailsModel> Maps { get; } = new("maps", map => map.PublishedFileId);
        public SeenItems<LobbyInfo> Lobbies { get; } = new("lobbies", lobby => lobby.LobbyId);

        public bool GuildEventsHooked { get; set; }
    }

    public class SeenItems<T>(string name, Func<T, ulong> getID)
    {
        private readonly HashSet<ulong> seenIDs = new();
        private bool hasBaseline;
        private int failedRuns;

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

        // For after the guild loop. A batch that reached no guild isn't recorded, so the next run finds it again.
        public void Commit(List<T>? newItems, bool delivered)
        {
            if (newItems == null)
                return;

            if (newItems.Count > 0 && !delivered)
            {
                if (++failedRuns < NotificationState.MaxFailedRuns)
                    return;

                Console.WriteLine($"Gave up on {newItems.Count} new {name} after {failedRuns} runs that reached no guild");
            }

            failedRuns = 0;
            seenIDs.UnionWith(newItems.Select(getID));
        }
    }
}
