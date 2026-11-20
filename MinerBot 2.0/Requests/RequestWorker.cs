using DataAccessLibrary.Requests;
using DataAccessLibrary.Requests.Models;
using MinerBot.Shared.Requests;
using MinerBot_2._0.Handlers;
using MinerBot_2._0.Services;
using MinerBot_2._0.Status;
using MySql.Data.MySqlClient;
using System.Runtime.ExceptionServices;

namespace MinerBot_2._0.Requests
{
    // Takes the dashboard's signed requests from MinerBot's database every 3 seconds, one at a time, in the order they came.
    // A request is checked against the request-signing key, the fixed list, its expiry and its kind's limit, counted from
    // the table, then its kind's own conditions, and only then taken, with a conditional UPDATE only one call can win, so
    // it's acted on at most once. Its result goes back to its row in the bot's own words, and the owner gets a direct
    // message after each Owner-level one. Requests act only while the bot is up on Discord, so every message to the owner
    // can go, apart from a restart, which is the remedy for a bot stuck off it. Each row is handled on its own: one that
    // can't be read is refused by its ID, and the rest go on. A failure of MinerBot's database ends the tick and waits 30
    // seconds, then twice as long each time, up to 5 minutes, as the status writer does, with one warning when the failures
    // start and one line when they end. Without BotConnection it does nothing, and posts are never paused. Without a usable
    // key it refuses every request, saying why, and still reads the pause and the status text. Nothing leaves its loop,
    // because an exception there would stop the whole bot.
    public sealed class RequestWorker : BackgroundService
    {
        // bot.env's name for the key, which setup-dashboard.sh writes
        public const string KeySetting = "REQUEST_SIGNING_KEY";

        static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(30);
        static readonly TimeSpan LongestRetry = TimeSpan.FromMinutes(5);

        // Longer than any expiry, so a row deleted and written again with its key is refused for as long as it could be taken
        static readonly TimeSpan KeepUsedKeys = TimeSpan.FromMinutes(15);

        // The restarts of earlier runs whose owner's message never went, which the first tick up on Discord sends
        static readonly TimeSpan UntoldRestartsWithin = TimeSpan.FromHours(1);

        // A minute of forged rows makes two events, and a signature that doesn't match one message an hour
        static readonly TimeSpan RejectionMinute = TimeSpan.FromMinutes(1);
        static readonly TimeSpan RejectionMessageEvery = TimeSpan.FromHours(1);

        // How long the settings are read again at every tick after a pause, a resume or a status text that didn't end done.
        // The bot stops waiting for a save at DbBaseConnection's 15-second limit, but MySql.Data doesn't stop the
        // statement, so one can still land after the first read back: it waits for a row lock at most
        // innodb_lock_wait_timeout, 50 seconds, and a commit that waits on the disk rarely longer. A read is one row by
        // its primary key. Only this run's own changes start it: a run that starts before an earlier run's change lands
        // reads the settings once.
        static readonly TimeSpan ReadBackFor = TimeSpan.FromMinutes(10);

        // The rows a tick reads, and the results and message states it keeps to write later, the oldest dropped past it. The
        // second is fewer in tests.
        internal const int RowsPerTick = 20;
        internal int KeptLimit = 200;

        // Shorter in tests, which move the clock themselves
        internal TimeSpan Interval = TimeSpan.FromSeconds(3);
        internal TimeSpan StopLimit = TimeSpan.FromSeconds(5);
        internal TimeSpan MessageLimit = TimeSpan.FromSeconds(30);
        internal TimeSpan RestartMessageLimit = TimeSpan.FromSeconds(10);

        readonly IBotRequestData _data;
        readonly RequestActions _actions;
        readonly OwnerAlertService _alerts;
        readonly BotRecorder _recorder;
        readonly IDiscordStatus _discord;
        readonly PostsPause _pause;
        readonly PresenceKeeper _presence;
        readonly JobHandler _jobs;
        readonly IHostApplicationLifetime _lifetime;
        readonly TimeProvider _clock;
        readonly ILogger<RequestWorker> _logger;
        readonly FailureLog _failures;

        // Why nothing runs, or null when BotConnection can be used, and the key, or null with why it can't be used
        readonly string? _unusable, _keyProblem;
        readonly byte[]? _key;

        // The requests this run took whose results aren't written yet, the one being acted on, and the keys it took, so a
        // row deleted and written again is refused
        readonly HashSet<ulong> started = new();
        readonly Dictionary<string, DateTime> used = new(StringComparer.Ordinal);
        ulong? inFlight;

        // Results and message states that MinerBot's database didn't take, in the order they came, written at each tick's
        // start. A message that outlasted its wait adds its state from another thread.
        readonly object keptGate = new();
        readonly List<(ulong Id, string State, string Result, ulong? Number)> keptResults = new();
        readonly List<(ulong Id, string Dm)> keptDms = new();

        // The forged rows' events: for each reason, the minute it started, and the rows counted since its first
        readonly Dictionary<Rejection, (DateTime Start, int Count, ulong First, ulong Last)> rejections = new();
        DateTime? rejectionMessageAt;

        DateTimeOffset? failingSince;
        DateTimeOffset retryAt;
        TimeSpan retryDelay;
        bool settingsRead, swept, untoldTold, offRecorded;

        // Until when every tick reads the settings, after a change that didn't end done, and the stored status text the log
        // last said breaks the rules, so it says so once rather than at each read
        DateTime? readBackUntil;
        string? refusedText;

        public RequestWorker(IBotRequestData data, RequestActions actions, OwnerAlertService alerts, BotRecorder recorder, IDiscordStatus discord, PostsPause pause,
            PresenceKeeper presence, JobHandler jobs, IHostApplicationLifetime lifetime, IConfiguration configuration, TimeProvider clock, ILogger<RequestWorker> logger)
        {
            _data = data;
            _actions = actions;
            _alerts = alerts;
            _recorder = recorder;
            _discord = discord;
            _pause = pause;
            _presence = presence;
            _jobs = jobs;
            _lifetime = lifetime;
            _clock = clock;
            _logger = logger;
            _failures = new FailureLog(logger);
            _unusable = StatusWriter.BotConnectionProblem(configuration);

            // Never throws, and never quotes the value
            _keyProblem = RequestSigning.KeyProblem(configuration[KeySetting]);
            _key = RequestSigning.ParseKey(configuration[KeySetting]);
        }

        ulong RunId => _recorder.RunId;

        DateTime Now() => _clock.GetUtcNow().UtcDateTime;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // The status writer has warned about it already
            if (_unusable != null)
            {
                _logger.LogInformation("ConnectionStrings:BotConnection {Problem}, so the bot takes no requests from the dashboard, and its posts are never paused", _unusable);
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(Interval, _clock, stoppingToken);
                    await TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    _failures.Log(e, "The request worker failed");
                }
            }

            await StopWriteAsync();
        }

        // One tick: everything that's due, unless an earlier failure's wait isn't over
        internal async Task TickAsync(CancellationToken stop)
        {
            if (_keyProblem != null && !offRecorded)
            {
                offRecorded = true;
                _logger.LogWarning("{Setting} {Problem}, so the bot acts on no request from the dashboard and refuses each one, saying why. Run ./setup-dashboard.sh --no-questions on the server.",
                    KeySetting, _keyProblem);
                _recorder.Record(BotEvents.RequestsOff, RequestTexts.RequestsOff(_keyProblem));
            }

            var now = _clock.GetUtcNow();

            if (failingSince != null && now < retryAt)
                return;

            try
            {
                await WorkAsync(stop);
            }
            catch (Exception e) when (!(e is OperationCanceledException && stop.IsCancellationRequested))
            {
                Failed(e, now);
                return;
            }
            finally
            {
                FlushRejections(Now(), all: false);
            }

            Succeeded();
        }

        async Task WorkAsync(CancellationToken stop)
        {
            var now = Now();

            foreach (var key in used.Where(entry => now - entry.Value >= KeepUsedKeys).Select(entry => entry.Key).ToList())
                used.Remove(key);

            // 1. What earlier ticks couldn't write
            await WriteKeptAsync();

            // 2. The pause and the status text, until they're read once, and at every tick for a while after a change that
            // didn't end done, then, once a run, the requests other runs took and never finished
            if (!settingsRead || now < readBackUntil)
                await ReadSettingsAsync(now);

            if (!swept)
            {
                foreach (var row in await _data.SweepOtherRuns(RunId, _recorder.StartedAt, RequestTexts.Interrupted, now))
                    _recorder.Record(BotEvents.RequestInterrupted, RequestTexts.InterruptedEvent(row.id, row.kind));

                swept = true;
            }

            var up = _discord.Read().Up;

            // 3. Earlier runs' restarts that couldn't tell the owner before they stopped
            if (up && !untoldTold)
            {
                foreach (var row in await _data.ListUntoldRestarts(RunId, now - UntoldRestartsWithin))
                    await TellOwnerAsync(row.id, RequestTexts.ToldAfterRestart(row), RequestTexts.ToldAfterRestartRecord(row.id), MessageLimit, stop);

                untoldTold = true;
            }

            // 4. The requests, oldest first: off Discord, only restarts, so a queue of others can't hide one
            foreach (var id in await _data.ListOpenIds(RunId, RowsPerTick, restartOnly: !up))
            {
                stop.ThrowIfCancellationRequested();

                if (!await HandleRowAsync(id, stop))
                    return;
            }
        }

        // One request, on its own, so one that can't be read or whose handling throws can't stop the rest. False when the
        // tick ends here, as after a restart, or after a change of the settings that MinerBot's database didn't confirm.
        async Task<bool> HandleRowAsync(ulong id, CancellationToken stop)
        {
            try
            {
                return await HandleAsync(id, stop);
            }
            catch (Exception e) when (!IsDatabase(e) && !(e is OperationCanceledException && stop.IsCancellationRequested))
            {
                // The row, not the database: its handling threw after it was read, or this run took it and it can't be read
                // now, which the ID alone can decide. Never the message, which can quote the row.
                _logger.LogWarning("Request {Id} couldn't be handled ({Problem}), so the bot didn't act on it", id, FailureLog.Describe(e));

                var result = RequestTexts.Unreadable(e.GetType().Name);

                if (started.Contains(id))
                    await FinishAsync(id, RequestStates.Failed, result, null);
                else
                    await _data.Decide(id, RunId, RequestStates.Refused, result, Now());

                return true;
            }
        }

        async Task<bool> HandleAsync(ulong id, CancellationToken stop)
        {
            BotRequestRow? read;

            try
            {
                read = await _data.GetRequest(id);
            }
            catch (Exception e) when (!IsDatabase(e) && !(e is OperationCanceledException && stop.IsCancellationRequested) && !started.Contains(id))
            {
                await UnreadableAsync(id, e, stop);
                return true;
            }

            if (read is not { } row)
                return true;

            var now = Now();

            // This run took it already
            if (row.state == RequestStates.Running && row.run_id == RunId)
            {
                // Its claim landed, but its answer never came, so it was never acted on, and never is. Otherwise its result
                // waits in memory to be written.
                if (!started.Contains(id))
                    await FinishAsync(id, RequestStates.Failed, RequestTexts.Unconfirmed(RequestKinds.Find(row.kind), row.claimed_at), null);

                return true;
            }

            // Another process decided or took it since the list was read
            if (row.state != null)
                return true;

            // 0 to 8
            var verdict = RequestChecks.Check(row, _key, now, used.ContainsKey);

            if (!verdict.Accepted)
            {
                if (await _data.Decide(id, RunId, verdict.State!, verdict.Result!, now) && verdict.Rejected is { } rejected)
                    await RejectedAsync(id, row.kind, rejected, now, stop);

                return true;
            }

            var kind = verdict.Kind!;

            // 9. The limit, counted from the table, so a restart resets nothing
            var claims = await _data.CountClaims(kind.CountedWith, now - kind.Window, kind.PerServer ? row.guild_id : null);

            if (claims.count >= kind.Limit && claims.oldest is { } oldest)
            {
                await RefuseAsync(row, RequestTexts.OverLimit(kind, claims.count, oldest), now);
                return true;
            }

            // 10. The kind's own conditions
            if (_actions.Refusal(kind, row, now) is { } refusal)
            {
                await RefuseAsync(row, refusal, now);
                return true;
            }

            // Off Discord only a restart is taken: the rest wait, and expire by their time
            if (kind.Word != RequestKinds.BotRestart && !_discord.Read().Up)
                return true;

            // A job asked for before JobHandler has found the jobs waits for it, unless its start failed
            if (kind.Job != null && !_jobs.IsStarted)
            {
                if (_jobs.StartFailed)
                    await RefuseAsync(row, RequestTexts.JobsDidntStart, now);

                return true;
            }

            // Taken, only now, by the clock at the claim, as the count may have waited on MinerBot's database: a request that
            // expired meanwhile isn't taken, and the next tick finds it expired. Once the bot is stopping, nothing is taken. A
            // claim whose answer doesn't come may or may not have landed, so the tick ends without acting, and the next one sees
            // which.
            stop.ThrowIfCancellationRequested();

            var claimedAt = Now();

            if (!await _data.Claim(id, RunId, claimedAt))
                return true;

            started.Add(id);
            used[row.request_key] = claimedAt;

            if (kind.Word == RequestKinds.BotRestart)
                return !await RestartAsync(row, kind, claimedAt, stop);

            var outcome = await ActAsync(kind, row, stop);
            await FinishAndTellAsync(row, kind, outcome, stop);

            // A pause, a resume or a status text that didn't end done may still have changed bot_settings, so the tick ends
            // here, and the next one reads the settings back before another request is checked against them
            return settingsRead;
        }

        // Refused without being taken, so it never counts against a limit
        Task<bool> RefuseAsync(BotRequestRow row, string result, DateTime now) => _data.Decide(row.id, RunId, RequestStates.Refused, result, now);

        async Task<RequestOutcome> ActAsync(RequestKind kind, BotRequestRow row, CancellationToken stop)
        {
            inFlight = row.id;

            try
            {
                return await _actions.ActAsync(kind, row, stop);
            }
            catch (Exception e) when (!(e is OperationCanceledException && stop.IsCancellationRequested))
            {
                _logger.LogWarning("Request {Id}'s action failed ({Problem})", row.id, FailureLog.Describe(e));
                return new RequestOutcome(RequestStates.Failed, RequestTexts.Unreadable(e.GetType().Name));
            }
        }

        // The result, then the owner's message for an Owner-level kind, whether or not the result could be written, since the
        // action happened. A pause or a status text that didn't end done is read back: a change MinerBot's database didn't
        // confirm, or one whose answer was a lost connection, may have landed all the same, and may still land later.
        async Task FinishAndTellAsync(BotRequestRow row, RequestKind kind, RequestOutcome outcome, CancellationToken stop)
        {
            Exception? unwritten = null;

            try
            {
                await FinishAsync(row.id, outcome.State, outcome.Result, outcome.Number);
            }
            catch (Exception e) when (IsDatabase(e))
            {
                unwritten = e;
            }
            finally
            {
                inFlight = null;
            }

            if (outcome.State != RequestStates.Done && kind.Word is RequestKinds.PostsPause or RequestKinds.PostsResume or RequestKinds.StatusSet)
            {
                settingsRead = false;
                readBackUntil = Now() + ReadBackFor;
            }

            if (kind.OwnerOnly)
                await TellOwnerAsync(row.id, RequestTexts.OwnerMessage(kind, row, outcome.Result, outcome.Extra), RequestTexts.OwnerRecord(kind, row.id, outcome.State),
                    MessageLimit, stop);
            else if (outcome.Dm != null)
                await SetDmAsync(row.id, outcome.Dm);
            else if (outcome.DmGoing != null)
                KeepDmWhenItEnds(row.id, outcome.DmGoing);

            if (unwritten != null)
                ExceptionDispatchInfo.Throw(unwritten);
        }

        // The result is saved first, and without it there's no stop, so the next run finds the request done and a restart can't
        // loop. Taken even while the bot is off Discord, when its next run tells the owner. True once the host is stopping.
        async Task<bool> RestartAsync(BotRequestRow row, RequestKind kind, DateTime claimedAt, CancellationToken stop)
        {
            bool saved;

            try
            {
                saved = await _data.Finish(row.id, RunId, RequestStates.Done, RequestTexts.Restarting, null, Now());
            }
            catch (Exception e) when (IsDatabase(e))
            {
                // The done may have landed: this run's later write wins over it
                Keep(row.id, RequestStates.Failed, RequestTexts.RestartNotSaved(kind, claimedAt), null);
                throw;
            }

            started.Remove(row.id);

            if (!saved)
            {
                _logger.LogWarning("Restart request {Id}'s result found no row this run took, so the bot didn't restart", row.id);
                return false;
            }

            if (_discord.Read().Up)
                await TellOwnerAsync(row.id, RequestTexts.OwnerMessage(kind, row, RequestTexts.Restarting, null), RequestTexts.OwnerRecord(kind, row.id, RequestStates.Done),
                    RestartMessageLimit, stop);

            // A notification run the schedule started since the check would be cut in half by the stop, which doesn't wait for
            // a job, so the restart waits for the run to end, or for JobHandler's limit to cut it off
            while (_jobs.Runs(RequestActions.NotificationJob))
                await Task.Delay(Interval, _clock, stop);

            _logger.LogWarning("The bot restarts, as dashboard request {Id} asked", row.id);

            // The same graceful stop as docker stop, and an exit code that a restart policy of on-failure also restarts
            Environment.ExitCode = 3;
            _lifetime.StopApplication();

            return true;
        }

        // A result this run's claim needs. One MinerBot's database didn't take is kept, to write at a later tick, and the
        // action never runs again.
        async Task FinishAsync(ulong id, string state, string result, ulong? number)
        {
            try
            {
                if (!await _data.Finish(id, RunId, state, result, number, Now()))
                    _logger.LogWarning("Request {Id}'s result found no row this run took, so it wasn't written", id);
            }
            catch (Exception e) when (IsDatabase(e))
            {
                Keep(id, state, result, number);
                throw;
            }

            started.Remove(id);
        }

        // Waits for the message up to its limit, then writes how it went. One that outlasts the wait may still go, and its
        // state is written once it ends. Never throws for the message or its state: the action happened either way.
        async Task TellOwnerAsync(ulong id, string message, string recordAs, TimeSpan limit, CancellationToken stop)
        {
            var sending = _alerts.SendAsync(message, recordAs);
            string dm;

            try
            {
                await sending.WaitAsync(limit, stop);
                dm = RequestDmStates.Sent;
            }
            catch (TimeoutException) when (!sending.IsCompleted)
            {
                KeepDmWhenItEnds(id, sending);
                return;
            }
            catch (Exception e) when (!(e is OperationCanceledException && stop.IsCancellationRequested))
            {
                // OwnerAlertService recorded alert.failed
                dm = RequestDmStates.Failed;
            }

            await SetDmAsync(id, dm);
        }

        async Task SetDmAsync(ulong id, string dm)
        {
            try
            {
                await _data.SetDm(id, dm);
            }
            catch (Exception e) when (IsDatabase(e))
            {
                KeepDm(id, dm);
            }
        }

        void KeepDmWhenItEnds(ulong id, Task sending) =>
            _ = sending.ContinueWith(ended => KeepDm(id, ended.IsCompletedSuccessfully ? RequestDmStates.Sent : RequestDmStates.Failed), TaskScheduler.Default);

        // A row MySql.Data can't read, such as one with a time of 0000-00-00, which the dashboard's code can't write: so it was
        // written without the dashboard, and it's refused by its ID as a row in another form is, with that event and the
        // owner's message. One line in the log a minute, which counts nothing, rather than one for each row. Never the
        // exception's message, which can quote the row.
        async Task UnreadableAsync(ulong id, Exception e, CancellationToken stop)
        {
            _failures.Log(e, "The request worker couldn't read a request");

            var now = Now();

            if (await _data.Decide(id, RunId, RequestStates.Refused, RequestTexts.Unreadable(e.GetType().Name), now))
                await RejectedAsync(id, null, Rejection.Form, now, stop);
        }

        // A row the checks say isn't one the dashboard signed for this bot: an event, the first of its reason in a minute at
        // once and the rest of that minute in one, and for a form or a signature, a message to the owner at most once an hour
        async Task RejectedAsync(ulong id, string? kind, Rejection reason, DateTime now, CancellationToken stop)
        {
            if (rejections.TryGetValue(reason, out var minute) && now - minute.Start < RejectionMinute)
            {
                rejections[reason] = (minute.Start, minute.Count + 1, minute.Count == 0 ? id : minute.First, id);
            }
            else
            {
                FlushRejection(reason);
                rejections[reason] = (now, 0, 0, 0);

                // A kind only once the signature passed
                _recorder.Record(BotEvents.RequestRejected, RequestTexts.RejectedEvent(id, reason is Rejection.Form or Rejection.Signature ? null : kind, reason));
            }

            if (reason is Rejection.Form or Rejection.Signature && (rejectionMessageAt == null || now - rejectionMessageAt >= RejectionMessageEvery) && _discord.Read().Up)
            {
                rejectionMessageAt = now;
                await TellOwnerAsync(id, RequestTexts.RejectionMessage(id, reason), RequestTexts.RejectionRecord(id, reason), MessageLimit, stop);
            }
        }

        // The minutes that have ended, or all of them at a stop
        void FlushRejections(DateTime now, bool all)
        {
            foreach (var reason in rejections.Keys.ToList())
            {
                if (all || now - rejections[reason].Start >= RejectionMinute)
                {
                    FlushRejection(reason);
                    rejections.Remove(reason);
                }
            }
        }

        void FlushRejection(Rejection reason)
        {
            if (rejections.TryGetValue(reason, out var minute) && minute.Count > 0)
                _recorder.Record(BotEvents.RequestRejected, minute.Count == 1
                    ? RequestTexts.RejectedEvent(minute.First, null, reason)
                    : RequestTexts.RejectedEvents(minute.First, minute.Last, minute.Count, reason));
        }

        // The pause and the status text as MinerBot's database holds them. A pause found at the first read is an event, and
        // a change found at a later one, after a change whose answer didn't come. A stored text that breaks the rules, from
        // an older rule or written by hand, isn't shown, and the log says so once for each such text: one written by hand
        // that keeps them is, since the settings carry no signature.
        async Task ReadSettingsAsync(DateTime now)
        {
            var settings = await _data.GetSettings();
            var before = _pause.Read();
            var paused = settings?.posts_paused ?? false;

            _pause.Load(paused, settings?.pause_reason, settings?.pause_changed_at);

            if (before.Paused == null && paused)
                _recorder.Record(BotEvents.PostsPaused, RequestTexts.StillPaused(settings!.pause_changed_at, settings.pause_request_id));
            else if (before.Paused != null && before.Paused != paused && settings?.pause_request_id is { } request)
                _recorder.Record(paused ? BotEvents.PostsPaused : BotEvents.PostsResumed, RequestTexts.ByRequest(request));

            var text = settings?.status_text;

            if (text != null && RequestRules.StatusTextProblem(text) is { } problem)
            {
                if (text != refusedText)
                    _logger.LogWarning("The status text MinerBot's database holds breaks the rules ({Problem}), so the bot shows its default status", problem);

                refusedText = text;
                text = null;
            }
            else
                refusedText = null;

            if (text != _presence.Wanted)
                await _presence.SetAsync(text);

            settingsRead = true;
        }

        // At the start of each tick, oldest first, until one fails
        async Task WriteKeptAsync()
        {
            while (TakeKept() is { } kept)
            {
                var (id, state, result, number) = kept;

                try
                {
                    if (!await _data.Finish(id, RunId, state, result, number, Now()))
                        _logger.LogWarning("Request {Id}'s kept result found no row this run took, so it wasn't written", id);

                    started.Remove(id);
                }
                catch
                {
                    PutBack(kept);
                    throw;
                }
            }

            while (TakeKeptDm() is { } dm)
            {
                try
                {
                    await _data.SetDm(dm.Id, dm.Dm);
                }
                catch
                {
                    KeepDm(dm.Id, dm.Dm, first: true);
                    throw;
                }
            }
        }

        // A later result of this run for the same request replaces the one kept, as a restart's correction must
        void Keep(ulong id, string state, string result, ulong? number)
        {
            lock (keptGate)
            {
                keptResults.RemoveAll(kept => kept.Id == id);
                keptResults.Add((id, state, result, number));

                if (keptResults.Count > KeptLimit)
                {
                    _logger.LogWarning("Request {Id}'s result was dropped, because the bot keeps only {Limit} that MinerBot's database didn't take. Its row stays running until the next run marks it unknown.",
                        keptResults[0].Id, KeptLimit);
                    keptResults.RemoveAt(0);
                }
            }
        }

        (ulong Id, string State, string Result, ulong? Number)? TakeKept()
        {
            lock (keptGate)
            {
                if (keptResults.Count == 0)
                    return null;

                var kept = keptResults[0];
                keptResults.RemoveAt(0);
                return kept;
            }
        }

        void PutBack((ulong Id, string State, string Result, ulong? Number) kept)
        {
            lock (keptGate)
            {
                if (!keptResults.Any(other => other.Id == kept.Id))
                    keptResults.Insert(0, kept);
            }
        }

        void KeepDm(ulong id, string dm, bool first = false)
        {
            lock (keptGate)
            {
                keptDms.RemoveAll(kept => kept.Id == id);
                keptDms.Insert(first ? 0 : keptDms.Count, (id, dm));

                if (keptDms.Count > KeptLimit)
                {
                    _logger.LogWarning("Request {Id}'s message state was dropped, because the bot keeps only {Limit} that MinerBot's database didn't take. Its row doesn't say whether the owner's message went.",
                        keptDms[0].Id, KeptLimit);
                    keptDms.RemoveAt(0);
                }
            }
        }

        (ulong Id, string Dm)? TakeKeptDm()
        {
            lock (keptGate)
            {
                if (keptDms.Count == 0)
                    return null;

                var kept = keptDms[0];
                keptDms.RemoveAt(0);
                return kept;
            }
        }

        // One last write when the bot stops, within StopLimit in all: a request being acted on may or may not have
        // happened, and what was kept. If it fails, the next run's sweep marks the request unknown. The forged rows' counts
        // still waiting for their minute's end go to the recorder, which the status writer writes as it stops, after this.
        internal async Task StopWriteAsync()
        {
            using var limit = new CancellationTokenSource(StopLimit);

            try
            {
                if (inFlight is { } id)
                {
                    await _data.Finish(id, RunId, RequestStates.Unknown, RequestTexts.Interrupted, null, Now()).WaitAsync(limit.Token);
                    inFlight = null;
                }

                await WriteKeptAsync().WaitAsync(limit.Token);
            }
            catch (Exception e)
            {
                _logger.LogWarning("The request worker's last results before the bot stopped weren't all written ({Problem})", FailureLog.Describe(e));
            }

            FlushRejections(Now(), all: true);
        }

        // MinerBot's database failed, not the row: MariaDB's own error, or DbBaseConnection's limit
        internal static bool IsDatabase(Exception e)
        {
            if (e is TimeoutException)
                return true;

            for (var inner = e; inner != null; inner = inner.InnerException)
            {
                if (inner is MySqlException)
                    return true;
            }

            return false;
        }

        void Failed(Exception e, DateTimeOffset now)
        {
            if (failingSince == null)
            {
                failingSince = now;
                retryDelay = FirstRetry;

                // Only the type and MariaDB's number, because MySql.Data's message can name the login. Before the settings were
                // read once in this run, the posts wait too, and while the database doesn't answer, only this line can say so.
                if (_pause.Read().Paused == null)
                    _logger.LogWarning("MinerBot's database didn't answer the request worker ({Problem}). Requests wait, and so do the bot's armor, map and lobby posts, until the bot has read whether they're paused. It tries again in 30 seconds, then less often, up to every 5 minutes.",
                        FailureLog.Describe(e));
                else
                    _logger.LogWarning("MinerBot's database didn't answer the request worker ({Problem}). Requests wait, and the bot tries again in 30 seconds, then less often, up to every 5 minutes.",
                        FailureLog.Describe(e));
            }
            else
            {
                retryDelay = retryDelay * 2 < LongestRetry ? retryDelay * 2 : LongestRetry;
            }

            retryAt = now + retryDelay;
        }

        void Succeeded()
        {
            if (failingSince == null)
                return;

            failingSince = null;
            _logger.LogInformation("MinerBot's database answers the request worker again");
        }
    }
}
