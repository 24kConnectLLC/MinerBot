using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;

namespace MinerBot_2._0.Posting
{
    // What MinerBot's database didn't take of how a message ended, kept in memory by the attempt's key for the run of the bot:
    // a settle that failed, and a claim whose answer didn't come, as a message not sent, since nothing was sent after it. The
    // next run applies them before it reads anything, each safe to repeat, as the store settles only a message still open
    // and says false for one settled already or never claimed. A restart loses them, and the look in the channel's history
    // then settles those messages.
    public sealed class PendingSettlements
    {
        readonly Lock gate = new();
        readonly List<Settlement> waiting = new();

        public int Count
        {
            get
            {
                lock (gate)
                    return waiting.Count;
            }
        }

        public void Add(Settlement settlement)
        {
            ArgumentNullException.ThrowIfNull(settlement);

            lock (gate)
            {
                waiting.RemoveAll(other => other.AttemptKey == settlement.AttemptKey);
                waiting.Add(settlement);
            }
        }

        // Whether a settle of this attempt waits here, so the look in the history leaves its message alone
        public bool Holds(ulong attemptKey)
        {
            lock (gate)
                return waiting.Exists(settlement => settlement.AttemptKey == attemptKey);
        }

        // In the order they came. One the database doesn't take throws, and it and the ones after it stay.
        public async Task ApplyAsync(INotificationData store)
        {
            List<Settlement> due;

            lock (gate)
                due = waiting.ToList();

            foreach (var settlement in due)
            {
                await store.SettleAsync(settlement);

                lock (gate)
                    waiting.Remove(settlement);
            }
        }
    }
}
