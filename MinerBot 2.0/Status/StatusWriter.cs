using DataAccessLibrary;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using System.Globalization;

namespace MinerBot_2._0.Status
{
    // Writes what BotRecorder holds to MinerBot's database every 5 seconds: the status row when 30 seconds have passed or a
    // state changed, then the events, the job runs, the job status, the samples and the lobby samples. An item leaves the
    // recorder only once its write went through. A write that fails keeps everything and waits 30 seconds, then twice as
    // long each time, up to 5 minutes, with one warning when the failures start and one line when they end. Without
    // BotConnection it writes nothing at all, and the bot and its health check work as before. Nothing leaves its loop,
    // because an exception there would stop the whole bot.
    public sealed class StatusWriter : BackgroundService
    {
        static readonly TimeSpan StatusEvery = TimeSpan.FromSeconds(30);
        static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(30);
        static readonly TimeSpan LongestRetry = TimeSpan.FromMinutes(5);

        // Shorter in tests, which move the clock themselves
        internal TimeSpan Interval = TimeSpan.FromSeconds(5);

        // For the last write when the bot stops, in all, well inside the host's 30 seconds for stopping everything
        internal TimeSpan StopLimit = TimeSpan.FromSeconds(5);

        readonly BotRecorder _recorder;
        readonly IBotStatusData _data;
        readonly TimeProvider _clock;
        readonly ILogger<StatusWriter> _logger;
        readonly FailureLog _failures;

        // Why nothing is written, or null when BotConnection can be used
        readonly string? _unusable;

        DateTimeOffset? failingSince, lastStatusWrite;
        DateTimeOffset retryAt;
        TimeSpan retryDelay;
        long writtenStatusVersion = -1;
        bool previousRunChecked, otherJobsDeleted;

        public StatusWriter(BotRecorder recorder, IBotStatusData data, IConfiguration configuration, TimeProvider clock, ILogger<StatusWriter> logger)
        {
            _recorder = recorder;
            _data = data;
            _clock = clock;
            _logger = logger;
            _failures = new FailureLog(logger);
            _unusable = BotConnectionProblem(configuration);
        }

        // What stops ConnectionStrings:BotConnection from being used, in a few words that never quote it, or null when
        // nothing does. The daily clean-up asks too, so it does nothing without it rather than fail every night.
        internal static string? BotConnectionProblem(IConfiguration configuration)
        {
            var connection = configuration.GetConnectionString("BotConnection");
            return string.IsNullOrEmpty(connection) ? "isn't set" : DatabaseNames.Problem(connection);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_recorder.Version != null)
                _logger.LogInformation("MinerBot's version is {Version}", _recorder.Version);
            else
                _logger.LogInformation("MinerBot has no version, because its image wasn't built from a commit");

            if (_unusable != null)
            {
                _logger.LogWarning("ConnectionStrings:BotConnection {Problem}, so the bot's status and history aren't recorded, and its daily clean-up does nothing. The bot and its health check work without it.", _unusable);
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
                    _failures.Log(e, "The status writer failed");
                }
            }

            await StopWriteAsync();
        }

        // One tick: everything that's due, unless an earlier failure's wait isn't over
        internal async Task TickAsync(CancellationToken stop)
        {
            var now = _clock.GetUtcNow();

            if (failingSince != null && now < retryAt)
                return;

            try
            {
                await WriteAsync(now, null, stop);
            }
            catch (Exception e) when (!(e is OperationCanceledException && stop.IsCancellationRequested))
            {
                Failed(e, now);
                return;
            }

            Succeeded(now);
        }

        // One last write when the bot stops, within StopLimit in all, failing or not: the status row with the time it
        // stopped, then whatever is queued. A write cut off here may still land, which is why every write is safe to repeat.
        // No write comes after it, so an outage it may end, and what the limits dropped, go with it: they're lost with the
        // rest if it fails too.
        internal async Task StopWriteAsync()
        {
            using var limit = new CancellationTokenSource(StopLimit);
            var now = _clock.GetUtcNow();
            var failing = failingSince != null;

            try
            {
                RecordCatchUp(now);
                await WriteAsync(now, now.UtcDateTime, limit.Token);

                if (failing)
                    _logger.LogInformation("MinerBot's database takes the bot's status and history again");
            }
            catch (Exception e)
            {
                _logger.LogWarning("The bot's last status and history before it stopped weren't all written ({Problem})", FailureLog.Describe(e));
            }
        }

        async Task WriteAsync(DateTimeOffset now, DateTime? stoppedAt, CancellationToken stop)
        {
            var (status, version) = _recorder.StatusToWrite();

            if (stoppedAt != null || version != writtenStatusVersion || lastStatusWrite == null || now - lastStatusWrite >= StatusEvery)
                await WriteStatusAsync(status, version, now, stoppedAt, stop);

            await WriteBatchesAsync<BotEvent>(_recorder.PeekEvents, batch => _data.AddEvents(batch), _recorder.EventsWritten, BotRecorder.EventLimit, stop);
            await WriteBatchesAsync<JobRun>(_recorder.PeekJobRuns, batch => _data.SaveJobRuns(batch), _recorder.JobRunsWritten, BotRecorder.JobRunLimit, stop);
            await WriteBatchesAsync<(JobStatus Row, long Version)>(_recorder.PeekJobStatus, batch => _data.SaveJobStatus(batch.Select(job => job.Row).ToList()),
                _recorder.JobStatusWritten, BotStatusData.BatchLimit * 10, stop);

            // Once a run, after JobHandler found its jobs: the rows of jobs that no longer exist
            if (!otherJobsDeleted && _recorder.JobNames is { Count: > 0 } jobs)
            {
                await _data.DeleteOtherJobs(jobs).WaitAsync(stop);
                otherJobsDeleted = true;
            }

            await WriteBatchesAsync<UptimeSample>(_recorder.PeekSamples, batch => _data.SaveSamples(batch), _recorder.SamplesWritten, BotRecorder.SampleLimit, stop);
            await WriteBatchesAsync<LobbySample>(_recorder.PeekLobbySamples, batch => _data.SaveLobbySamples(batch), _recorder.LobbySamplesWritten, BotRecorder.LobbySampleLimit, stop);
        }

        // The first write of a run reads the row before it writes its own: a row of another run that never stopped cleanly
        // is a run that crashed, was killed, or went with a reboot
        async Task WriteStatusAsync(BotStatus row, long version, DateTimeOffset now, DateTime? stoppedAt, CancellationToken stop)
        {
            if (!previousRunChecked)
            {
                var before = await _data.GetStatus().WaitAsync(stop);

                if (before != null && before.run_id != _recorder.RunId && before.stopped_at == null)
                    _recorder.Record(BotEvents.UncleanEnd, string.Create(CultureInfo.InvariantCulture,
                        $"the run that started at {before.started_at:yyyy-MM-dd HH:mm:ss} UTC never stopped cleanly; its last heartbeat was at {before.heartbeat_at:yyyy-MM-dd HH:mm:ss} UTC"));

                previousRunChecked = true;
            }

            row.heartbeat_at = now.UtcDateTime;
            row.stopped_at = stoppedAt;

            await _data.SaveStatus(row).WaitAsync(stop);

            lastStatusWrite = now;
            writtenStatusVersion = version;
        }

        // Batch after batch, at most as many as the queue holds, so one tick can't go on while more arrives
        async Task WriteBatchesAsync<T>(Func<int, List<T>> peek, Func<List<T>, Task> write, Action<List<T>> written, int limit, CancellationToken stop)
        {
            for (var rows = 0; rows < limit; rows += BotStatusData.BatchLimit)
            {
                var batch = peek(BotStatusData.BatchLimit);

                if (batch.Count == 0)
                    return;

                Task writing;

                try
                {
                    writing = write(batch);
                }
                catch (ArgumentException e)
                {
                    // The repository checks every row before any SQL. A row it refuses is the bot's own mistake, and would
                    // fail every write after it, so the batch goes.
                    _failures.Log(e, "MinerBot's database refused rows the bot recorded, so they were dropped");
                    written(batch);
                    continue;
                }

                await writing.WaitAsync(stop);
                written(batch);
            }
        }

        void Failed(Exception e, DateTimeOffset now)
        {
            if (failingSince == null)
            {
                failingSince = now;
                retryDelay = FirstRetry;

                // Only the type and MariaDB's number, because MySql.Data's message can name the login
                _logger.LogWarning("MinerBot's database didn't take the bot's status and history ({Problem}). The bot keeps them and tries again in 30 seconds, then less often, up to every 5 minutes.",
                    FailureLog.Describe(e));
            }
            else
            {
                retryDelay = retryDelay * 2 < LongestRetry ? retryDelay * 2 : LongestRetry;
            }

            retryAt = now + retryDelay;
        }

        void Succeeded(DateTimeOffset now)
        {
            if (failingSince != null)
                _logger.LogInformation("MinerBot's database takes the bot's status and history again");

            RecordCatchUp(now);
        }

        // The outage that a write at this time ends, and what the limits dropped since this was last recorded
        void RecordCatchUp(DateTimeOffset now)
        {
            if (failingSince is { } since)
            {
                failingSince = null;

                _recorder.Record(BotEvents.DatabaseUnreachable, string.Create(CultureInfo.InvariantCulture,
                    $"from {since.UtcDateTime:yyyy-MM-dd HH:mm:ss} to {now.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC"));
            }

            _recorder.RecordDropped();
        }
    }
}
