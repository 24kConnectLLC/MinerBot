using MinerBot_2._0.Status;

namespace MinerBot_2._0.Requests
{
    // Whether the notification job's armor, map and lobby posts are paused from the dashboard, why and since when. Nothing
    // else is: not /admin announce, the welcome message, a test message or the owner's messages. A singleton, because
    // JobHandler makes the job anew for every run. Until the request worker has read MinerBot's database once in this run
    // it isn't known, and posts wait, so a pause the Owner chose isn't lost because a restart met a database that didn't
    // answer. Without a usable BotConnection there's no pause to read, so it's known as not paused, and the bot posts as it
    // always has. Only the request worker changes it: from the row it read, or once MinerBot's database has taken a change.
    public sealed class PostsPause
    {
        // Paused is null while it isn't known. Since is UTC.
        public sealed record State(bool? Paused, string? Reason, DateTime? Since);

        State state;

        public PostsPause(IConfiguration configuration)
        {
            state = StatusWriter.BotConnectionProblem(configuration) == null ? new State(null, null, null) : new State(false, null, null);
        }

        // All three at once, so they always belong together
        public State Read() => Volatile.Read(ref state);

        // Whether the notification job holds its posts: while they're paused, and while it isn't known yet whether they are,
        // which known tells apart
        public bool Holds(out bool known)
        {
            var paused = Read().Paused;
            known = paused != null;
            return paused != false;
        }

        internal void Load(bool paused, string? reason, DateTime? since) => Volatile.Write(ref state, new State(paused, paused ? reason : null, since));
    }
}
