using System.Globalization;
using System.Security.Cryptography;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Requests.Models;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using DataAccessLibrary.Tests.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Tests
{
    // The request command against the test database, as request.sh runs it on the server, with the test playing the bot
    // through the bot's own repository: it sends one request, waits for the bot's answer and prints it, and after a restart
    // waits for the bot to be back on Discord. The command's clock is a fake one the tests move, so its limits pass at once.
    [Collection("Database")]
    public class RequestCommandTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        // Made up, as setup-dashboard.sh makes one
        static readonly string Key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        const ulong FirstOwner = 1000000000000000001;

        // The bot's runs, as random numbers
        const ulong BotRun = 4242;
        const ulong NextRun = 4343;
        const ulong StoppedRun = 4444;

        static readonly RequestWaits Quick = new() { Poll = TimeSpan.FromMilliseconds(50), RestartPoll = TimeSpan.FromMilliseconds(50), RestartLimit = TimeSpan.FromMinutes(3) };

        async Task<MariaDbFixture> ReadyAsync()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.RequestTable, MariaDbFixture.AuditTable, MariaDbFixture.StatusTable, MariaDbFixture.SettingsTable);
            return db;
        }

        sealed record Run(Task<int> Exit, StringWriter Output)
        {
            // The command's output once it has ended, which only then is read from another thread
            public async Task<(int Exit, string Output)> EndAsync()
            {
                var exit = await Exit.WaitAsync(TimeSpan.FromSeconds(60), Token);
                return (exit, Output.ToString());
            }
        }

        // The command in the background, with the settings request.sh passes it in its environment
        static Run Start(MariaDbFixture db, FakeClock clock, string? text, params string[] args) => Start(db, clock, text, NullLoggerFactory.Instance, args);

        static Run Start(MariaDbFixture db, FakeClock clock, string? text, ILoggerFactory loggers, params string[] args)
        {
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:BotConnection"] = db.BotConnection,
                ["Dashboard:RequestSigningKey"] = Key,
                ["Dashboard:Owners"] = $"{FirstOwner}, 1000000000000000002"
            };

            if (text != null)
                settings[RequestCommand.TextVariable] = text;

            var output = new StringWriter();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

            // Its waits are on the fake clock, which only the test moves, so a test that fails before the command ends
            // would leave it waiting: it's stopped after 90 seconds whatever happens
            var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
            stop.CancelAfter(TimeSpan.FromSeconds(90));
            return new Run(Task.Run(() => RequestCommand.RunAsync(configuration, args, output, loggers, clock, Quick, stop.Token)), output);
        }

        // The request the command wrote, as the bot finds it, once it's there
        static async Task<BotRequestRow> RequestAsync(MariaDbFixture db, ulong after = 0)
        {
            for (var tries = 0; tries < 300; tries++)
            {
                var ids = await db.RootQueryAsync<ulong>($"SELECT id FROM {MariaDbFixture.RequestTable} WHERE id > @after ORDER BY id", new { after });

                if (ids.Count > 0 && await db.Requests().GetRequest(ids[^1]) is { } row)
                    return row;

                await Task.Delay(100, Token);
            }

            throw new TimeoutException("The command wrote no request within 30 seconds");
        }

        // Moves the command's clock on, 10 seconds at a time, until the command ends
        static async Task StepUntilEndedAsync(FakeClock clock, Run run)
        {
            for (var steps = 0; steps < 600 && !run.Exit.IsCompleted; steps++)
            {
                clock.Advance(TimeSpan.FromSeconds(10));
                await Task.Delay(20, Token);
            }
        }

        static DateTime Now(FakeClock clock) => clock.GetUtcNow().UtcDateTime;

        static string Clock(DateTime time) => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        static Task Connected(MariaDbFixture db, ulong run, DateTime startedAt) => Status(db, run, startedAt, DiscordState.Connected);

        static Task Status(MariaDbFixture db, ulong run, DateTime startedAt, string discordState, DateTime? stoppedAt = null) => db.Status().SaveStatus(new BotStatus
        {
            run_id = run,
            started_at = startedAt,
            heartbeat_at = startedAt,
            stopped_at = stoppedAt,
            discord_state = discordState,
            discord_since = startedAt,
            steam_state = SteamState.LoggedOn
        });

        // The row is signed with the dashboard's key and says the first Owner asked, as request.sh on the server, and its
        // audit row went in with it, without an address. The bot takes it and the command prints its answer.
        [Fact]
        public async Task RequestCommandWritesTheRowAndItsAuditRow()
        {
            var db = await ReadyAsync();
            var clock = new FakeClock();

            var run = Start(db, clock, null, "caches.clear");
            var row = await RequestAsync(db);

            Assert.Equal(RequestKinds.CachesClear, row.kind);
            Assert.Equal(FirstOwner, row.actor_id);
            Assert.Equal(RequestCommand.ActorName, row.actor_name);
            Assert.Equal(RequestRoles.Owner, row.actor_role);
            Assert.Null(row.argument);
            Assert.True(RequestSigning.Verify(Convert.FromHexString(Key), row.Fields(), row.signature));

            var audit = Assert.Single(await db.AuditAsync());
            Assert.Equal(RequestKinds.CachesClear, audit.action);
            Assert.Equal(AuditWords.RequestTarget, audit.target_type);
            Assert.Equal(row.id.ToString(CultureInfo.InvariantCulture), audit.target_id);
            Assert.Equal(FirstOwner, audit.actor_id);
            Assert.Equal(RequestCommand.ActorName, audit.actor_name);
            Assert.Equal(AuditRole.Owner, audit.actor_role);
            Assert.Null(audit.ip);

            Assert.True(await db.Requests().Claim(row.id, BotRun, Now(clock)));
            Assert.True(await db.Requests().Finish(row.id, BotRun, RequestStates.Done, "Cleared the armor-name and latest-maps caches.", null, Now(clock)));

            var (exit, output) = await run.EndAsync();

            Assert.Equal(0, exit);
            Assert.Contains($"Request {row.id} (Clear caches) is queued, asked by request.sh on the server (Owner {FirstOwner}). It expires at {Clock(row.expires_at)} UTC.", output);
            Assert.Contains($"Done at {Clock(Now(clock))} UTC: Cleared the armor-name and latest-maps caches.", output);
            Assert.DoesNotContain(Key, output);
        }

        // The command's log is for the database's warnings and failures, which its lines point to: a request that goes well
        // logs nothing, so BotRequests' information line, which would read "(request (null))" without a web request, never
        // shows among the command's own lines
        [Fact]
        public async Task RequestCommandLogsOnlyWarningsAndFailures()
        {
            var db = await ReadyAsync();
            var clock = new FakeClock();
            var recorder = new RecordingLoggerProvider();
            using var loggers = LoggerFactory.Create(logging => RequestCommand.LogLevels(logging.AddProvider(recorder)));

            var run = Start(db, clock, null, loggers, "caches.clear");
            var row = await RequestAsync(db);
            Assert.True(await db.Requests().Claim(row.id, BotRun, Now(clock)));
            Assert.True(await db.Requests().Finish(row.id, BotRun, RequestStates.Done, "Cleared the armor-name and latest-maps caches.", null, Now(clock)));
            var (exit, _) = await run.EndAsync();

            Assert.Equal(0, exit);
            Assert.Empty(recorder.Entries);
            Assert.True(loggers.CreateLogger<BotRequests>().IsEnabled(LogLevel.Warning));
        }

        // Each way a request ends, as the bot writes it: the result in the bot's words, a lookup's account number, a test
        // message's ID, the settings after a pause, and exit code 1 for anything but done. A control character in a result
        // is shown as ?.
        [Fact]
        public async Task RequestCommandPrintsTheBotsAnswer()
        {
            var db = await ReadyAsync();
            var clock = new FakeClock();

            var run = Start(db, clock, "Live check of PR 9", "posts.pause");
            var row = await RequestAsync(db);
            Assert.Equal("Live check of PR 9", row.argument);
            Assert.True(await db.Requests().Claim(row.id, BotRun, Now(clock)));
            await db.Requests().SavePause(true, "Live check of PR 9", Now(clock), FirstOwner, row.id, Now(clock));
            Assert.True(await db.Requests().Finish(row.id, BotRun, RequestStates.Done, "Posts are paused.", null, Now(clock)));
            var (exit, output) = await run.EndAsync();
            Assert.Equal(0, exit);
            Assert.Contains($"Done at {Clock(Now(clock))} UTC: Posts are paused.", output);
            Assert.Contains($"The bot's settings now: posts paused since {Now(clock):yyyy-MM-dd HH:mm:ss} UTC; the default status.", output);

            // A whole profile link: only its name is sent
            run = Start(db, clock, "https://steamcommunity.com/id/made_up_name/", "steam.lookup");
            row = await RequestAsync(db, row.id);
            Assert.Equal("made_up_name", row.argument);
            Assert.True(await db.Requests().Claim(row.id, BotRun, Now(clock)));
            Assert.True(await db.Requests().Finish(row.id, BotRun, RequestStates.Done, "That link is account [U:1:12345].", 12345, Now(clock)));
            (exit, output) = await run.EndAsync();
            Assert.Equal(0, exit);
            Assert.Contains("Account number: 12345", output);
            Assert.DoesNotContain("settings now", output);

            run = Start(db, clock, null, "guild.test_message", "--guild", "1000000000000000005", "--channel", "1000000000000000006");
            row = await RequestAsync(db, row.id);
            Assert.Equal((ulong?)1000000000000000005, row.guild_id);
            Assert.Equal((ulong?)1000000000000000006, row.channel_id);
            Assert.True(await db.Requests().Claim(row.id, BotRun, Now(clock)));
            Assert.True(await db.Requests().Finish(row.id, BotRun, RequestStates.Done, "Sent a test message.", 1000000000000000099, Now(clock)));
            (exit, output) = await run.EndAsync();
            Assert.Equal(0, exit);
            Assert.Contains("Message ID: 1000000000000000099", output);

            // Refused, without a claim, with the settings after it
            run = Start(db, clock, "Live check", "status.set");
            row = await RequestAsync(db, row.id);
            Assert.True(await db.Requests().Decide(row.id, BotRun, RequestStates.Refused, "The bot takes this at most 6 times an hour.", Now(clock)));
            (exit, output) = await run.EndAsync();
            Assert.Equal(1, exit);
            Assert.Contains($"Refused at {Clock(Now(clock))} UTC: The bot takes this at most 6 times an hour.", output);
            Assert.Contains("posts paused since", output);

            run = Start(db, clock, null, "job.notifications");
            row = await RequestAsync(db, row.id);
            Assert.True(await db.Requests().Claim(row.id, BotRun, Now(clock)));
            Assert.True(await db.Requests().Finish(row.id, BotRun, RequestStates.Failed, "The notification job failed" + (char)27 + "[31m.", null, Now(clock)));
            (exit, output) = await run.EndAsync();
            Assert.Equal(1, exit);
            Assert.Contains($"Failed at {Clock(Now(clock))} UTC: The notification job failed?[31m.", output);
            Assert.DoesNotContain((char)27, output);
        }

        // After a restart the bot took, the command waits for a new run of the bot to be up on Discord and says how long it
        // took, then the settings as the new run keeps them: a new run that isn't on Discord yet, or that stopped again, isn't
        // back. A bot that isn't back within the limit is exit code 1.
        [Fact]
        public async Task RequestCommandWaitsForTheRestart()
        {
            var db = await ReadyAsync();
            var clock = new FakeClock();
            await Connected(db, BotRun, Now(clock).AddHours(-1));

            var run = Start(db, clock, null, "bot.restart");
            var row = await RequestAsync(db);
            Assert.True(await db.Requests().Claim(row.id, BotRun, Now(clock)));
            Assert.True(await db.Requests().Finish(row.id, BotRun, RequestStates.Done, "The bot is restarting. It's usually back within a minute.", null, Now(clock)));

            // The old run's row stays a while, then a new run writes its own before it's on Discord, then stops once it's on
            // it, and only the run after that is back
            await Task.Delay(300, Token);
            Assert.False(run.Exit.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(38));
            await Status(db, StoppedRun, Now(clock).AddSeconds(-20), DiscordState.Disconnected);
            await Task.Delay(500, Token);
            Assert.False(run.Exit.IsCompleted, "A run that isn't on Discord yet was taken as back");
            await Status(db, StoppedRun, Now(clock).AddSeconds(-20), DiscordState.Connected, stoppedAt: Now(clock).AddSeconds(-10));
            await Task.Delay(500, Token);
            Assert.False(run.Exit.IsCompleted, "A run that stopped again was taken as back");
            await Connected(db, NextRun, Now(clock).AddSeconds(-5));

            var (exit, output) = await run.EndAsync();
            Assert.Equal(0, exit);
            Assert.Contains("Waiting for the bot to be back on Discord, for up to 3 minutes.", output);
            Assert.Contains($"The bot is back on Discord 38 s after the request: its new run started at {Clock(Now(clock).AddSeconds(-5))} UTC.", output);
            Assert.Contains("The bot's settings now: posts aren't paused; the default status.", output);

            // The next run takes another restart, and never comes back
            run = Start(db, clock, null, "bot.restart");
            row = await RequestAsync(db, row.id);
            Assert.True(await db.Requests().Claim(row.id, NextRun, Now(clock)));
            Assert.True(await db.Requests().Finish(row.id, NextRun, RequestStates.Done, "The bot is restarting. It's usually back within a minute.", null, Now(clock)));
            await StepUntilEndedAsync(clock, run);

            (exit, output) = await run.EndAsync();
            Assert.Equal(1, exit);
            Assert.Contains("The bot isn't back on Discord 3 minutes after the restart.", output);
        }

        // No answer within the limits: a request still queued 20 seconds past its expiry, or one the bot took and hasn't
        // ended 30 seconds past its kind's action limit. Each is exit code 1, with what it waited for.
        [Fact]
        public async Task RequestCommandGivesUpAtTheLimit()
        {
            var db = await ReadyAsync();
            var clock = new FakeClock();

            var run = Start(db, clock, null, "caches.clear");
            var row = await RequestAsync(db);
            await StepUntilEndedAsync(clock, run);
            var (exit, output) = await run.EndAsync();

            Assert.Equal(1, exit);
            Assert.Contains($"request {row.id} is still queued, past its expiry at {Clock(row.expires_at)} UTC. ./status.sh says whether the bot runs.", output);
            Assert.Null((await db.Requests().GetRequest(row.id))?.state);

            run = Start(db, clock, null, "steam.renew");
            row = await RequestAsync(db, row.id);
            var claimed = Now(clock);
            Assert.True(await db.Requests().Claim(row.id, BotRun, claimed));
            await StepUntilEndedAsync(clock, run);
            (exit, output) = await run.EndAsync();

            Assert.Equal(1, exit);
            Assert.Contains($"Running since {Clock(claimed)} UTC.", output);
            Assert.Contains($"request {row.id} has run since {Clock(claimed)} UTC, past its 45-second limit. The Bot page shows how it ends.", output);
        }
    }
}
