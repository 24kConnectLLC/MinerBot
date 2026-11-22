using System.Collections.Concurrent;
using MinerBot.Dashboard.Security;

namespace MinerBot.Dashboard.Requests
{
    // One send at a time for each person, so each send counts the person's requests after the one before it is written.
    // Without it, forms posted at once, as two tabs or a script with a stolen session can post them, all read the same count
    // and all pass the person's limit, and nothing else keeps it: the bot's limits are for each kind, for everyone. People
    // never wait for each other. In memory, because the dashboard is one process and request.sh's requests have no limit for
    // one person; it holds one entry for each person who has sent a request, and only the people on the lists can.
    internal sealed class PersonTurns
    {
        // A send waits for the whole of the one before it: up to five database calls on the way to its write, and a few more
        // when one refuses, each within DbBaseConnection's 15 seconds. On a database that answers, however slowly, that one
        // ends, and this one then counts what it wrote, or finds it, as a double click's second sending finds its first; on
        // one that stopped answering, it ends at its first call's limit. So a send waits as long as nginx waits for a page,
        // 60 seconds, less one call's 15 for the look that finds a double click's first sending. Another form sent at the
        // same moment makes all its calls after the wait, and its session's read before it, so on a database slower than
        // about 5 seconds a call nginx answers for it with its own page, which says the dashboard is restarting, though its
        // request is written. At that speed the page each send's redirect leads to outlasts nginx already, the first form's
        // included, as Bot controls makes 13 to 15 calls to a send's 6: a shorter wait would spare nobody that page, and
        // would turn such a form away. One that waits longer is behind a database too slow to use, and ends as a call to it
        // would, with nothing written: DashboardUnavailableException, whose page says so. Shorter in tests.
        internal TimeSpan WaitLimit { get; init; } = TimeSpan.FromSeconds(45);

        readonly ConcurrentDictionary<ulong, SemaphoreSlim> turns = new();

        // The person's turn, held until it's disposed. The visitor going away doesn't end the wait, as it doesn't end the send.
        public async Task<IDisposable> TakeAsync(ulong personId)
        {
            var turn = turns.GetOrAdd(personId, _ => new SemaphoreSlim(1, 1));

            if (!await turn.WaitAsync(WaitLimit))
                throw new DashboardUnavailableException();

            return new Turn(turn);
        }

        // Given back once, however many times it's disposed
        sealed class Turn(SemaphoreSlim turn) : IDisposable
        {
            int ended;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref ended, 1) == 0)
                    turn.Release();
            }
        }
    }
}
