using System.Collections.Concurrent;
using System.Data;
using System.Runtime.CompilerServices;
using DataAccessLibrary;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Requests.Models;
using DataAccessLibrary.Tests;
using MinerBot.Shared.Requests;
using MySql.Data.MySqlClient;

namespace MinerBot.Dashboard.Tests.Support
{
    // The real queue, whose look by key misses each key once, as a look that came just before another tab's write would
    internal sealed class RacingQueue(BotRequestQueue real, ConcurrentDictionary<string, bool> missed) : IBotRequestQueue
    {
        public Task<BotRequestRow?> GetRequestByKey(string requestKey) =>
            missed.TryAdd(requestKey, true) ? Task.FromResult<BotRequestRow?>(null) : real.GetRequestByKey(requestKey);

        public Task<ulong> AddRequest(RequestFields fields, byte[] signature, DataTransaction transaction) => real.AddRequest(fields, signature, transaction);
        public Task<BotRequestRow?> GetRequest(ulong id) => real.GetRequest(id);
        public Task<BotSettingsRow?> GetSettings() => real.GetSettings();
        public Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work) => real.RunInTransaction(work);
    }

    // The real reads, where what a test puts in the box runs once, right after the next of the two reads the bot's limit is
    // checked from, whichever of them comes first: the bot taking a request between them, as its 3-second tick can
    internal sealed class BetweenLimitReads(BotRequestQueue real, StrongBox<Func<Task>?> between) : IBotRequestReads
    {
        public async Task<IReadOnlyList<WaitingRow>> ListWaiting(DateTime now) => await ThenAsync(await real.ListWaiting(now));
        public async Task<IReadOnlyList<ClaimRow>> ListClaimsSince(DateTime since) => await ThenAsync(await real.ListClaimsSince(since));

        public Task<long> CountAlertsSince(DateTime since) => real.CountAlertsSince(since);
        public Task<RequestPageRow?> GetRequestView(ulong id) => real.GetRequestView(id);
        public Task<RequestPageRow?> LatestDm() => real.LatestDm();
        public Task<RequestPageRow?> LatestRefusedAlert() => real.LatestRefusedAlert();
        public Task<IReadOnlyList<AskedRow>> ListAskedBy(ulong actorId, DateTime since) => real.ListAskedBy(actorId, since);
        public Task<IReadOnlyList<RequestPageRow>> ListLatest(IReadOnlyCollection<string> kinds, ulong? guildId = null) => real.ListLatest(kinds, guildId);

        async Task<T> ThenAsync<T>(T read)
        {
            if (Interlocked.Exchange(ref between.Value, null) is { } action)
                await action();

            return read;
        }
    }

    // The real queue, whose transactions commit and then time out, as a commit whose answer never came, and what a test puts
    // in afterward runs once the commit has landed, such as the database going down
    internal sealed class UnconfirmedQueue(BotRequestQueue real, Action? afterward = null) : IBotRequestQueue
    {
        public async Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work)
        {
            await real.RunInTransaction(work);
            afterward?.Invoke();
            throw new TimeoutException("The database didn't answer within 15 seconds");
        }

        public Task<ulong> AddRequest(RequestFields fields, byte[] signature, DataTransaction transaction) => real.AddRequest(fields, signature, transaction);
        public Task<BotRequestRow?> GetRequest(ulong id) => real.GetRequest(id);
        public Task<BotRequestRow?> GetRequestByKey(string requestKey) => real.GetRequestByKey(requestKey);
        public Task<BotSettingsRow?> GetSettings() => real.GetSettings();
    }

    // The real queue, whose transactions run their work and then roll back, and whose commit's answer never comes, as a
    // commit the database lost when it stopped answering: the request was given its ID, but neither it nor its audit row landed
    internal sealed class LostCommitQueue(BotRequestQueue real) : IBotRequestQueue
    {
        public async Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work)
        {
            try
            {
                await real.RunInTransaction<T>(async transaction =>
                {
                    await work(transaction);
                    throw new RolledBack();
                });
            }
            catch (RolledBack)
            {
            }

            throw new TimeoutException("The database didn't answer within 15 seconds");
        }

        public Task<ulong> AddRequest(RequestFields fields, byte[] signature, DataTransaction transaction) => real.AddRequest(fields, signature, transaction);
        public Task<BotRequestRow?> GetRequest(ulong id) => real.GetRequest(id);
        public Task<BotRequestRow?> GetRequestByKey(string requestKey) => real.GetRequestByKey(requestKey);
        public Task<BotSettingsRow?> GetSettings() => real.GetSettings();

        sealed class RolledBack() : Exception("The transaction was rolled back, as a lost commit's is");
    }

    // The real connections until a test takes the database down, after which every call, a read or a write, gets MySql.Data's
    // error for a database it can't connect to, as after the server stopped answering, until the test brings it back
    internal sealed class SwitchedDatabase(ISQLConnectionFactory real) : ISQLConnectionFactory
    {
        volatile bool down;

        public bool Down { get => down; set => down = value; }

        public IDbConnection Create(DBConnectionName connectionName) =>
            down ? throw MySqlErrors.Error((int)MySqlErrorCode.UnableToConnectToHost, "Unable to connect to any of the specified MySQL hosts.") : real.Create(connectionName);
    }

    // The real queue, whose own transactions fail before any work with MySql.Data's error for a database it can't connect to,
    // while its reads, and a write in another's transaction such as a sign-in's, still work
    internal sealed class UnavailableQueue(BotRequestQueue real) : IBotRequestQueue
    {
        public Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work) =>
            throw MySqlErrors.Error((int)MySqlErrorCode.UnableToConnectToHost, "Unable to connect to any of the specified MySQL hosts.");

        public Task<ulong> AddRequest(RequestFields fields, byte[] signature, DataTransaction transaction) => real.AddRequest(fields, signature, transaction);
        public Task<BotRequestRow?> GetRequest(ulong id) => real.GetRequest(id);
        public Task<BotRequestRow?> GetRequestByKey(string requestKey) => real.GetRequestByKey(requestKey);
        public Task<BotSettingsRow?> GetSettings() => real.GetSettings();
    }

    // The real reads, whose read of what a person asked for answers only after a pause, as a busy database's can, so sends
    // made at once all count before any of them writes, unless something makes them take turns
    internal sealed class SlowCountReads(BotRequestQueue real, TimeSpan pause) : IBotRequestReads
    {
        public async Task<IReadOnlyList<AskedRow>> ListAskedBy(ulong actorId, DateTime since)
        {
            var rows = await real.ListAskedBy(actorId, since);
            await Task.Delay(pause);
            return rows;
        }

        public Task<long> CountAlertsSince(DateTime since) => real.CountAlertsSince(since);
        public Task<RequestPageRow?> GetRequestView(ulong id) => real.GetRequestView(id);
        public Task<RequestPageRow?> LatestDm() => real.LatestDm();
        public Task<RequestPageRow?> LatestRefusedAlert() => real.LatestRefusedAlert();
        public Task<IReadOnlyList<ClaimRow>> ListClaimsSince(DateTime since) => real.ListClaimsSince(since);
        public Task<IReadOnlyList<RequestPageRow>> ListLatest(IReadOnlyCollection<string> kinds, ulong? guildId = null) => real.ListLatest(kinds, guildId);
        public Task<IReadOnlyList<WaitingRow>> ListWaiting(DateTime now) => real.ListWaiting(now);
    }

    // What a test holds one send with, and watches another's looks by: armed, the next count of a person's requests says it
    // has started and waits to be let go, as a send held in its turn before its write; and every look for a form's key is
    // counted as it starts, the one under way when LookDone is set saying when it ends
    internal sealed class SendHold
    {
        public int Armed;
        public int Looks;
        public TaskCompletionSource? LookDone;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // The real reads, whose next count of a person's requests, once the hold is armed, waits for the hold to let it go
    internal sealed class HeldCountReads(BotRequestQueue real, SendHold hold) : IBotRequestReads
    {
        public async Task<IReadOnlyList<AskedRow>> ListAskedBy(ulong actorId, DateTime since)
        {
            if (Interlocked.Exchange(ref hold.Armed, 0) == 1)
            {
                hold.Entered.TrySetResult();
                await hold.Release.Task;
            }

            return await real.ListAskedBy(actorId, since);
        }

        public Task<long> CountAlertsSince(DateTime since) => real.CountAlertsSince(since);
        public Task<RequestPageRow?> GetRequestView(ulong id) => real.GetRequestView(id);
        public Task<RequestPageRow?> LatestDm() => real.LatestDm();
        public Task<RequestPageRow?> LatestRefusedAlert() => real.LatestRefusedAlert();
        public Task<IReadOnlyList<ClaimRow>> ListClaimsSince(DateTime since) => real.ListClaimsSince(since);
        public Task<IReadOnlyList<RequestPageRow>> ListLatest(IReadOnlyCollection<string> kinds, ulong? guildId = null) => real.ListLatest(kinds, guildId);
        public Task<IReadOnlyList<WaitingRow>> ListWaiting(DateTime now) => real.ListWaiting(now);
    }

    // The real queue, counting each look for a form's key as it starts, before anything is awaited
    internal sealed class CountedLooks(BotRequestQueue real, SendHold hold) : IBotRequestQueue
    {
        public async Task<BotRequestRow?> GetRequestByKey(string requestKey)
        {
            Interlocked.Increment(ref hold.Looks);
            var row = await real.GetRequestByKey(requestKey);
            Volatile.Read(ref hold.LookDone)?.TrySetResult();
            return row;
        }

        public Task<ulong> AddRequest(RequestFields fields, byte[] signature, DataTransaction transaction) => real.AddRequest(fields, signature, transaction);
        public Task<BotRequestRow?> GetRequest(ulong id) => real.GetRequest(id);
        public Task<BotSettingsRow?> GetSettings() => real.GetSettings();
        public Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work) => real.RunInTransaction(work);
    }
}
