namespace DataAccessLibrary
{
    // Empties the bot's two cached reads at once, the armor names and the latest maps, for the dashboard's "clear caches"
    // request. Each read takes the token before it asks the database or Steam, and its entry expires with it, so a read
    // that was under way when the caches were cleared can't put its old list back: MemoryCache treats an entry set with a
    // cancelled token as expired. Nothing else is cleared. A cleared source isn't disposed, because a read that took its
    // token may still set an entry with it.
    public sealed class CacheReset
    {
        CancellationTokenSource current = new();

        // For the entry a read is about to set, taken before the read
        public CancellationToken Token => Volatile.Read(ref current).Token;

        public void Clear() => Interlocked.Exchange(ref current, new CancellationTokenSource()).Cancel();
    }
}
