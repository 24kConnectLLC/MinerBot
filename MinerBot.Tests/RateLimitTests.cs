using System.Collections.Concurrent;
using MinerBot_2._0.Attributes;

namespace MinerBot.Tests
{
    // The cooldowns are static and shared with the Discord tests, so these use ids of their own.
    // Their times are an hour in the past, so a cleanup here can't end a cooldown of a test that runs now.
    public class RateLimitTests
    {
        const ulong UserId = ulong.MaxValue - 1;

        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

        static readonly DateTime Now = DateTime.UtcNow.AddHours(-1);

        static (ulong, string) Key(string command) => (UserId, $"{nameof(RateLimitTests)} {command}");

        [Fact]
        public void CleanupRemovesCooldownsThatHaveAllEnded()
        {
            var key = Key("ended");
            Assert.True(new RateLimitAttribute(4).TryUse(key, Now, out _));

            RateLimitAttribute.ClearExpiredCommands(Now.AddSeconds(5));

            Assert.False(RateLimitAttribute.Items.ContainsKey(key));
        }

        [Fact]
        public void CleanupKeepsACooldownThatIsStillRunning()
        {
            var key = Key("running");
            var limit = new RateLimitAttribute(60);
            Assert.True(limit.TryUse(key, Now, out _));

            RateLimitAttribute.ClearExpiredCommands(Now.AddSeconds(5));

            Assert.False(limit.TryUse(key, Now.AddSeconds(5), out var usableAt));
            Assert.Equal(Now.AddSeconds(60), usableAt);
        }

        [Fact]
        public void CommandCanBeUsedAgainOnceItsCooldownEnds()
        {
            var key = Key("again");
            var limit = new RateLimitAttribute(4);
            Assert.True(limit.TryUse(key, Now, out _));
            Assert.False(limit.TryUse(key, Now.AddSeconds(3), out _));

            Assert.True(limit.TryUse(key, Now.AddSeconds(4), out _));
        }

        [Fact]
        public void OnlyOneOfManyUsesAtOnceGetsThrough()
        {
            // Many rounds, because a decision that isn't atomic only lets a second use through now and then
            const int threads = 16, rounds = 1000;

            var limit = new RateLimitAttribute(60);
            var allowed = new int[rounds];
            var failures = new ConcurrentQueue<Exception>();
            var stop = TestContext.Current.CancellationToken;
            using var start = new Barrier(threads);

            var workers = Enumerable.Range(0, threads).Select(thread => new Thread(() =>
            {
                for (int round = 0; round < rounds; round++)
                {
                    try
                    {
                        // Every thread starts the round together, so their uses land at the same moment
                        start.SignalAndWait(WaitLimit, stop);

                        if (limit.TryUse(Key($"race {round}"), Now, out _))
                            Interlocked.Increment(ref allowed[round]);
                    }
                    // An exception on a thread of its own would end the whole test run
                    catch (Exception e)
                    {
                        failures.Enqueue(e);
                    }
                }
            })).ToList();

            workers.ForEach(worker => worker.Start());

            Assert.All(workers, worker => Assert.True(worker.Join(WaitLimit)));
            Assert.Empty(failures);
            Assert.All(allowed, count => Assert.Equal(1, count));
        }
    }
}
