using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;

namespace MinerBot.Tests
{
    // MinerBot's database for the notification run that fails when a test says: the memory store, with the next calls of a
    // method made to throw before they reach it, as a database that doesn't answer, or after their write landed, as one whose
    // answer didn't come within the call's limit, so the caller can't know. Every call is kept by its method's name, in order.
    internal sealed class FailingNotificationData(MemoryNotificationData inner) : INotificationData
    {
        readonly object gate = new();
        readonly Dictionary<string, List<(Exception Failure, bool Lands, int After)>> failNext = new();
        readonly Dictionary<string, Exception> failAlways = new();
        readonly List<string> calls = new();
        int settlesToLose;

        public MemoryNotificationData Inner => inner;

        // A test's step once a claim has landed, before its answer comes back: a claim that took its time, or the host's stop
        // beginning meanwhile
        public Func<DeliveryClaim, Task>? AfterClaim { get; set; }

        // The next settle answers that it landed and never reaches the store, as only a fault in the bot could lose one
        public void LoseNextSettle()
        {
            lock (gate)
                settlesToLose++;
        }

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (gate)
                    return calls.ToList();
            }
        }

        // The next call of the method fails, after as many of its calls as given went through, before it reaches the store
        // or once its write has landed
        public void FailNext(string method, Exception failure, bool lands = false, int after = 0)
        {
            lock (gate)
            {
                if (!failNext.TryGetValue(method, out var queue))
                    failNext[method] = queue = new();

                queue.Add((failure, lands, after));
            }
        }

        // Every call of the method fails before it reaches the store, until Heal
        public void FailAlways(string method, Exception failure)
        {
            lock (gate)
                failAlways[method] = failure;
        }

        public void Heal()
        {
            lock (gate)
            {
                failNext.Clear();
                failAlways.Clear();
            }
        }

        public Task<PostingState> LoadAsync(PostingLoad load) => Call(nameof(LoadAsync), () => inner.LoadAsync(load));
        public Task<IReadOnlyList<QueueRow>> ListOpenAsync() => Call(nameof(ListOpenAsync), inner.ListOpenAsync);
        public Task<IReadOnlyList<BaselineRow>> ListBaselinesAsync() => Call(nameof(ListBaselinesAsync), inner.ListBaselinesAsync);
        public Task<IReadOnlyList<SeenRow>> ListSeenAsync(string source, IReadOnlyCollection<ulong> itemIds) => Call(nameof(ListSeenAsync), () => inner.ListSeenAsync(source, itemIds));
        public Task<IReadOnlyList<QueueRow>> ListHiddenAsync(IReadOnlyCollection<ulong> mapIds) => Call(nameof(ListHiddenAsync), () => inner.ListHiddenAsync(mapIds));
        public Task<int> CountOlderBotRunsAsync(string job, DateTime since) => Call(nameof(CountOlderBotRunsAsync), () => inner.CountOlderBotRunsAsync(job, since));
        public Task SaveDetectionAsync(Detection detection) => Call(nameof(SaveDetectionAsync), async () => { await inner.SaveDetectionAsync(detection); return true; });
        public Task<int> DropAsync(IReadOnlyCollection<ulong> queueIds, string reason, DateTime at) => Call(nameof(DropAsync), () => inner.DropAsync(queueIds, reason, at));
        public Task<int> ClearFailingAsync(ulong guildId, string source, DateTime at) => Call(nameof(ClearFailingAsync), () => inner.ClearFailingAsync(guildId, source, at));
        public Task<ulong> ClaimAsync(DeliveryClaim claim) => Call(nameof(ClaimAsync), async () =>
        {
            var id = await inner.ClaimAsync(claim);

            if (AfterClaim is { } step)
                await step(claim);

            return id;
        });
        public Task<bool> SettleAsync(Settlement settlement)
        {
            lock (gate)
            {
                if (settlesToLose > 0)
                {
                    settlesToLose--;
                    calls.Add(nameof(SettleAsync));
                    return Task.FromResult(true);
                }
            }

            return Call(nameof(SettleAsync), () => inner.SettleAsync(settlement));
        }
        public Task<bool> RecordRefusalAsync(Refusal refusal) => Call(nameof(RecordRefusalAsync), () => inner.RecordRefusalAsync(refusal));
        public Task<bool> MarkCheckedAsync(ulong attemptKey, byte checks) => Call(nameof(MarkCheckedAsync), () => inner.MarkCheckedAsync(attemptKey, checks));
        public Task<int> EndWaitsAsync(ulong guildId, ulong? channelId, DateTime at) => Call(nameof(EndWaitsAsync), () => inner.EndWaitsAsync(guildId, channelId, at));
        public Task<GuildSwitches?> GetSwitchesAsync(ulong guildId) => Call(nameof(GetSwitchesAsync), () => inner.GetSwitchesAsync(guildId));
        public Task<IReadOnlyList<ulong>> ListMessageIdsAsync(ulong guildId, ulong channelId, DateTime since) =>
            Call(nameof(ListMessageIdsAsync), () => inner.ListMessageIdsAsync(guildId, channelId, since));
        public Task<IReadOnlyList<ulong>> ListCarriedAsync(ulong deliveryId) => Call(nameof(ListCarriedAsync), () => inner.ListCarriedAsync(deliveryId));

        async Task<T> Call<T>(string method, Func<Task<T>> call)
        {
            (Exception Failure, bool Lands)? failure = null;

            lock (gate)
            {
                calls.Add(method);

                if (failAlways.TryGetValue(method, out var always))
                    failure = (always, false);
                else if (failNext.TryGetValue(method, out var queue) && queue.Count > 0)
                {
                    if (queue[0].After > 0)
                        queue[0] = queue[0] with { After = queue[0].After - 1 };
                    else
                    {
                        failure = (queue[0].Failure, queue[0].Lands);
                        queue.RemoveAt(0);
                    }
                }
            }

            if (failure is { Lands: false } before)
                throw before.Failure;

            var result = await call();

            if (failure is { } after)
                throw after.Failure;

            return result;
        }
    }
}
