using DataAccessLibrary.Feed;
using DataAccessLibrary.Feed.Models;
using MinerBot_2._0.Status;
using MySql.Data.MySqlClient;
using SteamWebAPIAccess;

namespace MinerBot_2._0.Feed
{
    // Publishes what the bot reads from Steam for the website's Lobbies and Maps pages, which can't ask Steam themselves:
    // the open public lobbies every 30 seconds and Steam's latest public maps every 5 minutes, into three tables of the
    // website's database that the website makes and only reads.
    // It's a loop of its own rather than a scheduled job: JobHandler records every run of a job, which would be 2,880
    // rows a day for the lobbies, counts in whole minutes, and runs the jobs that are due one after another, so a slow
    // feed would hold up the notification job. It shares nothing with that job either: it reads Steam itself, keeps no
    // list of what it has seen, and posts nothing, so a pause of the bot's posts doesn't pause it.
    // A read where Steam gave no answer changes no lobby or map, only when the bot last tried. A write that fails, as
    // when the website's database is down, the tables aren't made yet or the login lacks a right, costs only the feed:
    // one warning when a feed's failures start and one line when they end, and another try after 30 seconds, then
    // twice as long each time, up to 5 minutes, which for the maps is every read. Nothing leaves its loops, because an
    // exception there would stop the whole bot.
    public sealed class WebsiteFeedWriter : BackgroundService
    {
        // How many of Steam's latest public maps a read asks for, and how many pages of Steam's list it may take to find
        // them: a page of 20 also holds private and unlisted items
        public const int MapCount = 30;
        public const int MapPages = 10;

        static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(30);
        static readonly TimeSpan LongestRetry = TimeSpan.FromMinutes(5);

        // The waits are multiples of the lobbies' interval and the timer's ticks aren't exact, so a try that's due within
        // a second is made, rather than left for the tick after
        static readonly TimeSpan Early = TimeSpan.FromSeconds(1);

        // Shorter in tests, which call the ticks themselves on a clock they move
        internal TimeSpan LobbyInterval = TimeSpan.FromSeconds(30);
        internal TimeSpan MapInterval = TimeSpan.FromMinutes(5);

        // How long a read of Steam may take before the tick gives up on it, as a read that failed. Steam's own limits
        // are shorter, so these only end a read that hangs.
        internal TimeSpan LobbyReadLimit = TimeSpan.FromSeconds(25);
        internal TimeSpan MapReadLimit = TimeSpan.FromMinutes(4);

        readonly IGetData _steam;
        readonly IWebsiteFeedData _data;
        readonly TimeProvider _clock;
        readonly ILogger<WebsiteFeedWriter> _logger;
        readonly FailureLog _failures;

        // Each feed has its own tables, so one can fail while the other is written
        readonly Outage lobbies = new("the open lobbies", "Lobbies");
        readonly Outage maps = new("the latest maps", "Maps");

        public WebsiteFeedWriter(IGetData steam, IWebsiteFeedData data, TimeProvider clock, ILogger<WebsiteFeedWriter> logger)
        {
            _steam = steam;
            _data = data;
            _clock = clock;
            _logger = logger;
            _failures = new FailureLog(logger);
        }

        // The two feeds side by side, so a slow read of the maps doesn't hold up the lobbies
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
            LoopAsync(LobbyInterval, LobbyTickAsync, "The website feed's lobbies failed", stoppingToken),
            LoopAsync(MapInterval, MapTickAsync, "The website feed's maps failed", stoppingToken));

        // A tick as soon as the bot runs, then one each interval. A tick that takes longer than the interval is followed
        // by one more, not by every one it missed.
        async Task LoopAsync(TimeSpan interval, Func<CancellationToken, Task> tick, string failure, CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(interval, _clock);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await tick(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    _failures.Log(e, failure);
                }

                try
                {
                    await timer.WaitForNextTickAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        // One read of Steam's lobby list and its write, unless an earlier failure's wait isn't over. Every lobby the bot's
        // own list would show is written, on every version. The read says it's a frequent one, so Steam's reader writes
        // the line of a failed read once every 5 minutes, not at each of these.
        internal async Task LobbyTickAsync(CancellationToken stop)
        {
            var now = _clock.GetUtcNow();

            if (lobbies.Waits(now))
                return;

            var read = await ReadAsync(() => _steam.GetServerList(frequent: true), LobbyReadLimit, "The website feed couldn't read the open lobbies from Steam", stop);

            // Null when Steam gave no answer, which isn't the same as an answer with no lobbies in it
            var rows = read is { Available: true } ? read.Lobbies.Take(WebsiteFeedData.LobbyLimit).Select(FeedRows.Lobby).ToList() : null;

            try
            {
                if (rows != null)
                    await _data.ReplaceLobbies(rows, now.UtcDateTime).WaitAsync(stop);
                else
                    await _data.MarkChecked(FeedName.Lobbies, now.UtcDateTime).WaitAsync(stop);
            }
            catch (Exception e) when (!(e is OperationCanceledException && stop.IsCancellationRequested))
            {
                lobbies.Failed(e, now, _logger);
                return;
            }

            lobbies.Succeeded(_logger);
        }

        // One read of Steam's latest public maps and its write. Steam's reader gives an empty list when Steam fails, and
        // a read with no map that can be written is one too.
        internal async Task MapTickAsync(CancellationToken stop)
        {
            var now = _clock.GetUtcNow();

            if (maps.Waits(now))
                return;

            var read = await ReadAsync(() => _steam.GetLatestWorkshopItems(MapCount, MapPages), MapReadLimit, "The website feed couldn't read the latest maps from Steam", stop);
            var rows = read == null ? new List<FeedMap>() : FeedRows.Maps(read.Take(WebsiteFeedData.MapLimit));

            try
            {
                if (rows.Count > 0)
                    await _data.SaveMaps(rows, now.UtcDateTime).WaitAsync(stop);
                else
                    await _data.MarkChecked(FeedName.Maps, now.UtcDateTime).WaitAsync(stop);
            }
            catch (Exception e) when (!(e is OperationCanceledException && stop.IsCancellationRequested))
            {
                maps.Failed(e, now, _logger);
                return;
            }

            maps.Succeeded(_logger);
        }

        // Steam's answer, or null when the read threw or hung, which is written as a read that failed. Started on the
        // thread pool, because a reader that blocks before it hands back its task would hold the tick before the limit starts.
        async Task<T?> ReadAsync<T>(Func<Task<T>> read, TimeSpan limit, string failure, CancellationToken stop) where T : class
        {
            try
            {
                return await Task.Run(read, stop).WaitAsync(limit, stop);
            }
            catch (Exception e) when (!(e is OperationCanceledException && stop.IsCancellationRequested))
            {
                _failures.Log(e, failure);
                return null;
            }
        }

        // One feed's failed writes: when they started, and when the next try is due
        sealed class Outage(string rows, string page)
        {
            DateTimeOffset? failingSince;
            DateTimeOffset retryAt;
            TimeSpan retryDelay;

            public bool Waits(DateTimeOffset now) => failingSince != null && now < retryAt - Early;

            public void Failed(Exception e, DateTimeOffset now, ILogger logger)
            {
                if (failingSince == null)
                {
                    failingSince = now;
                    retryDelay = FirstRetry;

                    // Only the type and MariaDB's number, because MySql.Data's message can name the login and the server
                    logger.LogWarning("The website's database didn't take {Rows} for the website's {Page} page ({Problem}){Reason}. The page keeps what it last got, and the bot tries again at least once every 5 minutes. Nothing else the bot does depends on it.",
                        rows, page, FailureLog.Describe(e), Reason(e));
                }
                else
                {
                    retryDelay = retryDelay * 2 < LongestRetry ? retryDelay * 2 : LongestRetry;
                }

                retryAt = now + retryDelay;
            }

            public void Succeeded(ILogger logger)
            {
                if (failingSince == null)
                    return;

                failingSince = null;
                logger.LogInformation("The website's database takes {Rows} for the website's {Page} page again", rows, page);
            }
        }

        // What the errors a new feed is most likely to meet mean, in the bot's own words
        static string Reason(Exception e)
        {
            for (var inner = e; inner != null; inner = inner.InnerException)
            {
                if (inner is not MySqlException mySql)
                    continue;

                return mySql.Number switch
                {
                    (int)MySqlErrorCode.NoSuchTable => ": the website hasn't made the feed's tables yet",
                    (int)MySqlErrorCode.TableAccessDenied or (int)MySqlErrorCode.ColumnAccessDenied => ": the bot's website login has no right to write the feed's tables",
                    (int)MySqlErrorCode.AccessDenied or (int)MySqlErrorCode.DatabaseAccessDenied => ": the website's database refused the bot's login",
                    _ => ""
                };
            }

            return "";
        }
    }
}
