using System.Data;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Requests.Models;
using MinerBot.Shared.Requests;

namespace MinerBot.Tests
{
    // MinerBot's request table and settings row for the request worker, in memory, by the conditions of BotRequestData's
    // SQL: a claim takes only a waiting row that hasn't expired, a decision only a waiting row, a finish only this run's
    // claim, whatever its state, and a message's state is written once. A test can make every call fail or wait, make the
    // next call of one method fail with a timeout that did or didn't land, run something just before a claim, as another
    // process taking the row, or just before a limit's count, as the time a slow MariaDB takes or a stop asked for while it
    // answers, or make a row unreadable, as a zero date is. Every call is kept by its method's name, in order.
    internal sealed class FakeRequestData : IBotRequestData
    {
        readonly object gate = new();
        readonly SortedDictionary<ulong, BotRequestRow> rows = new();
        readonly Dictionary<string, Queue<(Exception Failure, bool Lands)>> failNext = new();
        readonly List<string> calls = new();
        ulong lastId;

        public BotSettingsRow? Settings { get; set; }
        public Exception? Failure { get; set; }
        public Task? Hold { get; set; }

        // Rows GetRequest can't read, as MySql.Data can't read a time of 0000-00-00
        public HashSet<ulong> Unreadable { get; } = new();

        // Runs just before a claim, with the row's ID
        public Action<ulong>? BeforeClaim { get; set; }

        // Runs just before a limit's count, as time that passes, or a stop asked for, while MariaDB answers it
        public Action? BeforeCount { get; set; }

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (gate)
                    return calls.ToList();
            }
        }

        // A line of the test's own among the calls, such as when the bot stopped
        public void Note(string what)
        {
            lock (gate)
                calls.Add(what);
        }

        public ulong Add(BotRequestRow row)
        {
            lock (gate)
            {
                var copy = Copy(row);
                copy.id = ++lastId;
                rows[copy.id] = copy;
                return copy.id;
            }
        }

        public BotRequestRow Row(ulong id)
        {
            lock (gate)
                return Copy(rows[id]);
        }

        // A row another run took, such as another bot process, or this bot's run before a restart
        public void ClaimFor(ulong id, ulong runId, DateTime at)
        {
            lock (gate)
                (rows[id].state, rows[id].run_id, rows[id].claimed_at) = (RequestStates.Running, runId, at);
        }

        // A row another run finished, as a restart's own row is before the bot stops
        public void FinishFor(ulong id, ulong runId, DateTime at, string state)
        {
            lock (gate)
                (rows[id].state, rows[id].run_id, rows[id].claimed_at, rows[id].ended_at, rows[id].result) = (state, runId, at, at, "Finished by another run.");
        }

        public IReadOnlyList<BotRequestRow> Rows
        {
            get
            {
                lock (gate)
                    return rows.Values.Select(Copy).ToList();
            }
        }

        public void FailNext(string method, Exception failure, bool lands = false)
        {
            lock (gate)
            {
                if (!failNext.TryGetValue(method, out var queue))
                    failNext[method] = queue = new();

                queue.Enqueue((failure, lands));
            }
        }

        public Task<IReadOnlyList<ulong>> ListOpenIds(ulong runId, int take, bool restartOnly) => Answer(nameof(ListOpenIds), () =>
            (IReadOnlyList<ulong>)rows.Values
                .Where(row => (row.state == null || (row.state == RequestStates.Running && row.run_id == runId)) && (!restartOnly || row.kind == RequestKinds.BotRestart))
                .Select(row => row.id)
                .Take(take)
                .ToList());

        public Task<BotRequestRow?> GetRequest(ulong id) => Answer(nameof(GetRequest), () =>
        {
            // The type MySql.Data's read fails with, quoting the column, which the bot must never repeat
            if (Unreadable.Contains(id))
                throw new DataException("Error parsing column 9 (created_at=0000-00-00 00:00:00.000)", new FormatException("zero date"));

            return rows.TryGetValue(id, out var row) ? Copy(row) : null;
        });

        public Task<bool> Decide(ulong id, ulong runId, string state, string result, DateTime now)
        {
            if (state is not (RequestStates.Refused or RequestStates.Expired))
                throw new ArgumentException("A request is decided refused or expired", nameof(state));

            return Answer(nameof(Decide), () =>
            {
                if (!rows.TryGetValue(id, out var row) || row.state != null)
                    return false;

                (row.state, row.run_id, row.ended_at, row.result) = (state, runId, now, Clip(result));
                return true;
            });
        }

        public Task<bool> Claim(ulong id, ulong runId, DateTime now)
        {
            BeforeClaim?.Invoke(id);

            return Answer(nameof(Claim), () =>
            {
                if (!rows.TryGetValue(id, out var row) || row.state != null || row.expires_at <= now)
                    return false;

                (row.state, row.run_id, row.claimed_at) = (RequestStates.Running, runId, now);
                return true;
            });
        }

        public Task<bool> Finish(ulong id, ulong runId, string state, string result, ulong? number, DateTime now)
        {
            if (state is not (RequestStates.Done or RequestStates.Failed or RequestStates.Unknown))
                throw new ArgumentException("A request that was taken ends done, failed or unknown", nameof(state));

            return Answer(nameof(Finish), () =>
            {
                if (!rows.TryGetValue(id, out var row) || row.run_id != runId || row.claimed_at == null)
                    return false;

                (row.state, row.ended_at, row.result, row.result_number) = (state, now, Clip(result), number);
                return true;
            });
        }

        public Task<bool> SetDm(ulong id, string dmState) => Answer(nameof(SetDm), () =>
        {
            if (!rows.TryGetValue(id, out var row) || row.dm_state != null)
                return false;

            row.dm_state = dmState;
            return true;
        });

        public Task<IReadOnlyList<SweptRequestRow>> SweepOtherRuns(ulong runId, DateTime startedAt, string result, DateTime now) => Answer(nameof(SweepOtherRuns), () =>
        {
            var swept = rows.Values.Where(row => row.state == RequestStates.Running && row.run_id != runId && row.claimed_at < startedAt).ToList();

            foreach (var row in swept)
                (row.state, row.ended_at, row.result) = (RequestStates.Unknown, now, Clip(result));

            return (IReadOnlyList<SweptRequestRow>)swept.Select(row => new SweptRequestRow { id = row.id, kind = row.kind }).ToList();
        });

        public Task<ClaimCountRow> CountClaims(IReadOnlyCollection<string> kinds, DateTime since, ulong? guildId = null)
        {
            if (kinds.Count == 0 || kinds.Any(kind => RequestKinds.Find(kind) == null))
                throw new ArgumentException("A limit counts kinds on the request list", nameof(kinds));

            BeforeCount?.Invoke();

            return Answer(nameof(CountClaims), () =>
            {
                var counted = rows.Values.Where(row => kinds.Contains(row.kind) && row.claimed_at >= since && (guildId == null || row.guild_id == guildId)).ToList();
                return new ClaimCountRow { count = counted.Count, oldest = counted.Min(row => row.claimed_at) };
            });
        }

        public Task<IReadOnlyList<BotRequestRow>> ListUntoldRestarts(ulong runId, DateTime since) => Answer(nameof(ListUntoldRestarts), () =>
            (IReadOnlyList<BotRequestRow>)rows.Values
                .Where(row => row.kind == RequestKinds.BotRestart && row.state == RequestStates.Done && row.run_id != runId && row.dm_state == null && row.claimed_at >= since)
                .Select(Copy)
                .ToList());

        public Task<BotSettingsRow?> GetSettings() => Answer(nameof(GetSettings), () => Settings == null ? null : Copy(Settings));

        public Task SavePause(bool paused, string? reason, DateTime changedAt, ulong changedBy, ulong requestId, DateTime now) => Answer(nameof(SavePause), () =>
        {
            var settings = Settings ?? new BotSettingsRow();
            (settings.posts_paused, settings.pause_reason, settings.pause_changed_at, settings.pause_changed_by, settings.pause_request_id, settings.updated_at) =
                (paused, reason, changedAt, changedBy, requestId, now);
            Settings = settings;
            return true;
        });

        public Task SaveStatus(string? text, DateTime changedAt, ulong changedBy, ulong requestId, DateTime now) => Answer(nameof(SaveStatus), () =>
        {
            var settings = Settings ?? new BotSettingsRow();
            (settings.status_text, settings.status_changed_at, settings.status_changed_by, settings.status_request_id, settings.updated_at) =
                (text, changedAt, changedBy, requestId, now);
            Settings = settings;
            return true;
        });

        async Task<T> Answer<T>(string method, Func<T> apply)
        {
            (Exception Failure, bool Lands)? next = null;

            lock (gate)
            {
                calls.Add(method);

                if (failNext.TryGetValue(method, out var queue) && queue.Count > 0)
                    next = queue.Dequeue();
            }

            if (Failure is { } failure)
                throw failure;

            if (Hold is { } hold)
                await hold;

            if (next is { Lands: false } refused)
                throw refused.Failure;

            T result;

            lock (gate)
                result = apply();

            // It went through, but its answer didn't come
            if (next is { } landed)
                throw landed.Failure;

            return result;
        }

        static string Clip(string result) => DataAccessLibrary.DbBaseConnection.Clip(result, 300)!;

        static BotRequestRow Copy(BotRequestRow row) => new()
        {
            id = row.id,
            request_key = row.request_key,
            kind = row.kind,
            actor_id = row.actor_id,
            actor_name = row.actor_name,
            actor_role = row.actor_role,
            guild_id = row.guild_id,
            channel_id = row.channel_id,
            argument = row.argument,
            created_at = row.created_at,
            expires_at = row.expires_at,
            signature = row.signature.ToArray(),
            state = row.state,
            run_id = row.run_id,
            claimed_at = row.claimed_at,
            ended_at = row.ended_at,
            result = row.result,
            result_number = row.result_number,
            dm_state = row.dm_state
        };

        static BotSettingsRow Copy(BotSettingsRow settings) => new()
        {
            posts_paused = settings.posts_paused,
            pause_reason = settings.pause_reason,
            pause_changed_at = settings.pause_changed_at,
            pause_changed_by = settings.pause_changed_by,
            pause_request_id = settings.pause_request_id,
            status_text = settings.status_text,
            status_changed_at = settings.status_changed_at,
            status_changed_by = settings.status_changed_by,
            status_request_id = settings.status_request_id,
            updated_at = settings.updated_at
        };
    }
}
