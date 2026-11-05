namespace MinerBot.Dashboard.Tests.Support
{
    // The time the app sees. It starts at the real time, to the second, and only moves forward, because the correlation
    // cookie's expiry is this time plus 15 minutes and the client's cookie jar drops one that's already past on the real
    // clock. Timers stay on real time.
    internal sealed class FakeClock : TimeProvider
    {
        long ticks = DateTimeOffset.UtcNow.UtcTicks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
            Interlocked.Add(ref ticks, by.Ticks);
        }
    }
}
