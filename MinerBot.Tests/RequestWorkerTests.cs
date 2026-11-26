using System.Diagnostics;
using System.Text.RegularExpressions;
using DataAccessLibrary.Requests.Models;
using Microsoft.Extensions.Logging;
using MinerBot.Shared.Requests;
using MinerBot_2._0.Requests;
using MinerBot_2._0.Status;

namespace MinerBot.Tests
{
    // The request worker's loop: its checks, claims, limits, results, restarts and stops, one tick at a time on a clock the
    // test moves, against FakeRequestData for MinerBot's database and FakeDiscord, except where the loop itself is tested
    [Collection("Discord")]
    public class RequestWorkerTests
    {
        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);
        static readonly byte[] OtherKey = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();

        static readonly TimeSpan TwoMinutes = TimeSpan.FromMinutes(2);

        // A restart sets the process's exit code, which the test puts back
        sealed class ExitCodeKept : IDisposable
        {
            readonly int code = Environment.ExitCode;

            public void Dispose() => Environment.ExitCode = code;
        }

        static async Task WaitUntilAsync(Func<bool> condition, string failure)
        {
            var clock = Stopwatch.StartNew();

            while (!condition())
            {
                Assert.True(clock.Elapsed < WaitLimit, failure);
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }

        static (string? State, string? Result) Outcome(RequestBot bot, ulong id) => (bot.Row(id).state, bot.Row(id).result);

        // A row of a kind not on the list, or a time the dashboard wouldn't sign, built by hand and signed
        static RequestFields Fields(string kind, DateTime created, TimeSpan lasts, string role = RequestRoles.Owner) =>
            new(RequestFields.NewKey(), kind, SignedRequests.OwnerId, "Owner", role, null, null, null, created, created + lasts);

        // Without BotConnection

        [Fact]
        public async Task WithoutBotConnectionNothingRuns()
        {
            await using var bot = await RequestBot.StartAsync(connection: false);
            bot.Add(RequestKinds.CachesClear);

            await bot.Worker.StartAsync(TestContext.Current.CancellationToken);
            await bot.Worker.ExecuteTask!.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
            await bot.Worker.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.Empty(bot.Data.Calls);
            Assert.False(bot.Pause.Read().Paused);
            Assert.True(bot.Logger.Logged(LogLevel.Information, "ConnectionStrings:BotConnection isn't set, so the bot takes no requests from the dashboard, and its posts are never paused"));
        }

        [Fact]
        public async Task WithoutAKeyRowsAreRefusedAndRequestsOffIsRecordedOnce()
        {
            await using var bot = await RequestBot.StartAsync(useKey: false);
            bot.Data.Settings = new BotSettingsRow { posts_paused = true, pause_reason = "Held", pause_changed_at = RequestBot.Noon.UtcDateTime, pause_request_id = 7 };
            var first = bot.Add(RequestKinds.CachesClear);
            var second = bot.Add(RequestKinds.PostsResume);

            await bot.TickAsync();
            await bot.TickAsync();

            const string noKey = "The bot has no usable request-signing key, so it takes no requests. Run ./setup-dashboard.sh --no-questions on the server.";
            Assert.Equal((RequestStates.Refused, noKey), Outcome(bot, first));
            Assert.Equal((RequestStates.Refused, noKey), Outcome(bot, second));
            Assert.Equal([(BotEvents.RequestsOff, (string?)"the request-signing key isn't set, so the bot acts on no request from the dashboard")], bot.Events(BotEvents.RequestsOff));
            Assert.Single(bot.Logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("REQUEST_SIGNING_KEY isn't set"));

            // The pause is read all the same, so it holds if the key is taken away later
            Assert.True(bot.Pause.Read().Paused);
            Assert.Empty(bot.Messages());
        }

        [Theory]
        [InlineData("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1")]
        [InlineData("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F")]
        public async Task AShortKeyTurnsRequestsOff(string key)
        {
            await using var bot = await RequestBot.StartAsync(key: key);
            var id = bot.Add(RequestKinds.CachesClear);

            await bot.TickAsync();

            // bot.env holds a key, so the row doesn't say there's none
            Assert.Equal((RequestStates.Refused, (string?)"The bot has no usable request-signing key, so it takes no requests. Run ./setup-dashboard.sh --no-questions on the server."),
                Outcome(bot, id));
            Assert.Equal("the request-signing key isn't 64 hexadecimal digits in small letters, so the bot acts on no request from the dashboard",
                Assert.Single(bot.Events(BotEvents.RequestsOff)).Detail);
            Assert.DoesNotContain(bot.Logger.Entries, entry => entry.Message.Contains(key, StringComparison.OrdinalIgnoreCase));
        }

        // Forged and malformed rows

        // Nothing in a row is trusted until its signature passed, so a forged row is refused as forged, whatever its kind
        [Fact]
        public async Task ForgedRowIsRefusedBeforeAnythingElse()
        {
            await using var bot = await RequestBot.StartAsync();
            var id = bot.Data.Add(SignedRequests.Row(Fields("bot.explode", bot.Clock, TwoMinutes), OtherKey));

            await bot.TickAsync();

            Assert.Equal((RequestStates.Refused, (string?)("This request's signature didn't match the bot's key, so the bot didn't act on it: the row was written or "
                + "changed without the key, or the two apps hold different keys. ./status.sh --check on the server says which.")), Outcome(bot, id));
            Assert.Equal([(BotEvents.RequestRejected, (string?)"request 1 refused: its signature didn't match")], bot.Events(BotEvents.RequestRejected));
            Assert.DoesNotContain(bot.Recorder.PeekEvents(1000), row => row.detail != null && row.detail.Contains("bot.explode"));
        }

        // Each of the ten signed fields changed after signing: refused for its signature, nothing reaches the server, and one
        // event, with the one message an hour to the owner
        [Theory]
        [InlineData("request_key")]
        [InlineData("kind")]
        [InlineData("actor_id")]
        [InlineData("actor_name")]
        [InlineData("actor_role")]
        [InlineData("guild_id")]
        [InlineData("channel_id")]
        [InlineData("argument")]
        [InlineData("created_at")]
        [InlineData("expires_at")]
        public async Task EachSignedFieldTamperedIsRefused(string field)
        {
            await using var bot = await RequestBot.StartAsync();
            var row = SignedRequests.Signed(RequestKinds.GuildTestMessage, bot.Clock, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.ChannelId);

            switch (field)
            {
                case "request_key": row.request_key = RequestFields.NewKey(); break;
                case "kind": row.kind = RequestKinds.GuildRefresh; break;
                case "actor_id": row.actor_id++; break;
                case "actor_name": row.actor_name += "x"; break;
                case "actor_role": row.actor_role = RequestRoles.Admin; break;
                case "guild_id": row.guild_id = FakeDiscord.JoinedGuildId; break;
                case "channel_id": row.channel_id = FakeDiscord.AnnouncementChannelId; break;
                case "argument": row.argument = "x"; break;
                case "created_at": row.created_at = row.created_at.AddMilliseconds(-1); break;
                case "expires_at": row.expires_at = row.expires_at.AddMilliseconds(-1); break;
            }

            var id = bot.Data.Add(row);

            await bot.TickAsync();

            Assert.Equal(RequestStates.Refused, bot.Row(id).state);
            Assert.StartsWith("This request's signature didn't match the bot's key", bot.Row(id).result);
            Assert.Single(bot.Events(BotEvents.RequestRejected));
            Assert.Empty(bot.Posts());
            Assert.Equal([RequestTexts.RejectionMessage(id, Rejection.Signature)], bot.Messages());
        }

        [Fact]
        public async Task UnknownKindRoleFutureAndTooLongAreRefused()
        {
            await using var bot = await RequestBot.StartAsync();
            var now = bot.Clock;

            var unknown = bot.Data.Add(SignedRequests.Row(Fields("bot.explode", now, TwoMinutes)));
            var role = bot.Add(RequestKinds.BotRestart, role: RequestRoles.Admin);
            var future = bot.Data.Add(SignedRequests.Row(Fields(RequestKinds.CachesClear, now.AddSeconds(31), TwoMinutes)));
            var tooLong = bot.Data.Add(SignedRequests.Row(Fields(RequestKinds.CachesClear, now, TimeSpan.FromMinutes(3))));
            var justInTime = bot.Data.Add(SignedRequests.Row(Fields(RequestKinds.CachesClear, now.AddSeconds(29), TwoMinutes)));

            await bot.TickAsync();

            Assert.Equal((RequestStates.Refused, (string?)"The bot doesn't know this kind of request, so it didn't act on it."), Outcome(bot, unknown));
            Assert.Equal((RequestStates.Refused, (string?)"Only the Owner can ask for this, so the bot didn't act on it."), Outcome(bot, role));
            Assert.Equal((RequestStates.Refused, (string?)"This request was made later than the bot's clock says it is now, so the bot didn't act on it."), Outcome(bot, future));
            Assert.Equal((RequestStates.Refused, (string?)"This request was made to last longer than this kind may (2 minutes), so the bot didn't act on it."), Outcome(bot, tooLong));
            Assert.Equal(RequestStates.Done, bot.Row(justInTime).state);

            Assert.Equal(
            [
                "request 1 (bot.explode) refused: its kind isn't on the bot's list",
                "request 2 (bot.restart) refused: its role may not ask for its kind",
                "request 3 (caches.clear) refused: it was made later than the bot's clock says it is now",
                "request 4 (caches.clear) refused: it was made to last longer than its kind may"
            ], bot.Events(BotEvents.RequestRejected).Select(row => row.Detail));
            Assert.Equal(0, bot.Lifetime.Stops);
        }

        [Fact]
        public async Task ExpiredAtTheBoundary()
        {
            await using var bot = await RequestBot.StartAsync();
            var expired = bot.Data.Add(SignedRequests.Signed(RequestKinds.CachesClear, bot.Clock - TwoMinutes));
            var stillGood = bot.Data.Add(SignedRequests.Signed(RequestKinds.CachesClear, bot.Clock - TwoMinutes + TimeSpan.FromMilliseconds(1)));

            await bot.TickAsync();

            Assert.Equal((RequestStates.Expired, (string?)"The bot saw this request only after it expired at 12:06:00 UTC, so it didn't act on it."), Outcome(bot, expired));
            Assert.Null(bot.Row(expired).claimed_at);
            Assert.Equal(RequestStates.Done, bot.Row(stillGood).state);
            Assert.Empty(bot.Events(BotEvents.RequestRejected));
        }

        // A done row deleted and written again with its key, by someone with MinerBot's own login
        [Fact]
        public async Task UsedRequestKeyIsRefused()
        {
            await using var bot = await RequestBot.StartAsync();
            var row = SignedRequests.Signed(RequestKinds.GuildTestMessage, bot.Clock, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.ChannelId);
            var first = bot.Data.Add(row);

            await bot.TickAsync();

            var again = bot.Data.Add(row);
            await bot.TickAsync();

            Assert.Equal(RequestStates.Done, bot.Row(first).state);
            Assert.Equal((RequestStates.Refused, (string?)"This request was already used, so the bot didn't act on it again."), Outcome(bot, again));
            Assert.Single(bot.Posts());
        }

        // The key is kept for as long as its request could still be taken: one of the kinds that wait longest, written again
        // just before it expires
        [Fact]
        public async Task AUsedKeyIsKeptUntilItsRequestExpires()
        {
            await using var bot = await RequestBot.StartAsync();
            var kind = RequestKinds.Find(RequestKinds.OwnerSigninAlert)!;
            Assert.Equal(RequestKinds.All.Max(other => other.Expiry), kind.Expiry);
            var row = SignedRequests.Signed(kind.Word, bot.Clock);
            var first = bot.Data.Add(row);

            await bot.TickAsync();

            bot.Move(kind.Expiry - TimeSpan.FromMilliseconds(1));
            var again = bot.Data.Add(row);
            await bot.TickAsync();

            Assert.Equal(RequestStates.Done, bot.Row(first).state);
            Assert.Equal((RequestStates.Refused, (string?)"This request was already used, so the bot didn't act on it again."), Outcome(bot, again));
            Assert.Single(bot.Messages());
        }

        // Claims and results

        [Fact]
        public async Task AClaimLostToAnotherProcessIsNotActedOn()
        {
            await using var bot = await RequestBot.StartAsync();
            var id = bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.ChannelId);
            bot.Data.BeforeClaim = claimed => bot.Data.ClaimFor(claimed, 999, bot.Clock);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Running, (ulong?)999), (bot.Row(id).state, bot.Row(id).run_id));
            Assert.Empty(bot.Posts());
        }

        // The claim holds the expiry test by the clock at the claim: a request that expires while its limit is counted, as
        // when MinerBot's database is slow to answer, isn't taken, and the next tick finds it expired
        [Fact]
        public async Task ARequestThatExpiresWhileItsLimitIsCountedIsntTaken()
        {
            await using var bot = await RequestBot.StartAsync();

            // A second of its 2 minutes left
            var id = bot.Data.Add(SignedRequests.Signed(RequestKinds.GuildTestMessage, bot.Clock - TwoMinutes + TimeSpan.FromSeconds(1), guildId: FakeDiscord.GuildId,
                channelId: FakeDiscord.ChannelId));
            bot.Data.BeforeCount = () => bot.Move(TimeSpan.FromSeconds(10));

            await bot.TickAsync();

            Assert.Equal((null, (DateTime?)null), (bot.Row(id).state, bot.Row(id).claimed_at));

            bot.Data.BeforeCount = null;
            await bot.TickAsync();

            Assert.Equal((RequestStates.Expired, (string?)"The bot saw this request only after it expired at 12:06:01 UTC, so it didn't act on it."), Outcome(bot, id));
            Assert.Null(bot.Row(id).claimed_at);
            Assert.Empty(bot.Posts());
        }

        // The claim landed, but its answer never came: the next tick finishes it, and it's never acted on
        [Fact]
        public async Task UnconfirmedClaimIsNeverActedOn()
        {
            await using var bot = await RequestBot.StartAsync();
            var id = bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.ChannelId);
            bot.Data.FailNext(nameof(FakeRequestData.Claim), new TimeoutException("The database didn't answer within 15 seconds"), lands: true);

            await bot.TickAsync();
            Assert.Equal(RequestStates.Running, bot.Row(id).state);

            bot.Move(TimeSpan.FromSeconds(30));
            await bot.TickAsync();

            Assert.Equal((RequestStates.Failed, (string?)"MinerBot's database didn't confirm that the bot took this request, so the bot didn't act on it. Ask again."), Outcome(bot, id));
            Assert.Empty(bot.Posts());
        }

        // Time passes while the limit is counted, as on a slow MariaDB. The claim goes by the clock as it is then, so a
        // request that expired meanwhile isn't taken, and expires at the next tick, and one that's still good is taken at the
        // time it really was, which the limits count from.
        [Fact]
        public async Task TheClaimGoesByTheClockAtTheClaim()
        {
            await using var bot = await RequestBot.StartAsync();
            var late = bot.Data.Add(SignedRequests.Signed(RequestKinds.CachesClear, bot.Clock - TwoMinutes + TimeSpan.FromSeconds(1)));
            bot.Data.BeforeCount = () => bot.Move(TimeSpan.FromSeconds(14));

            await bot.TickAsync();

            Assert.Null(bot.Row(late).state);

            bot.Data.BeforeCount = null;
            await bot.TickAsync();

            Assert.Equal((RequestStates.Expired, (string?)"The bot saw this request only after it expired at 12:06:01 UTC, so it didn't act on it."), Outcome(bot, late));
            Assert.Null(bot.Row(late).claimed_at);
            Assert.DoesNotContain(nameof(FakeRequestData.Finish), bot.Data.Calls);

            var good = bot.Add(RequestKinds.CachesClear);
            var checkedAt = bot.Clock;
            bot.Data.BeforeCount = () => bot.Move(TimeSpan.FromSeconds(14));

            await bot.TickAsync();

            Assert.Equal(RequestStates.Done, bot.Row(good).state);
            Assert.Equal((DateTime?)checkedAt.AddSeconds(14), bot.Row(good).claimed_at);
        }

        // Its claim counts toward the limit, though the bot never acted on it, so a kind taken at most once in its window says
        // when another can be taken, the time the limit's refusal gives
        [Fact]
        public async Task AnUnconfirmedClaimSaysWhenItCanBeAskedForAgain()
        {
            await using var bot = await RequestBot.StartAsync();
            var id = bot.Add(RequestKinds.SteamReconnect);
            bot.Data.FailNext(nameof(FakeRequestData.Claim), new TimeoutException("The database didn't answer within 15 seconds"), lands: true);

            await bot.TickAsync();
            bot.Move(TimeSpan.FromSeconds(30));
            await bot.TickAsync();

            Assert.Equal((RequestStates.Failed,
                (string?)"MinerBot's database didn't confirm that the bot took this request, so the bot didn't act on it. Ask again after 12:11:00 UTC."), Outcome(bot, id));

            var again = bot.Add(RequestKinds.SteamReconnect);
            await bot.TickAsync();

            Assert.Equal((RequestStates.Refused, (string?)"The bot takes this at most once every 5 minutes, and last took one at 12:06:00 UTC. Try again after 12:11:00 UTC."),
                Outcome(bot, again));
            Assert.Equal(0, bot.Steam.Reconnects);
        }

        // The result write fails twice, then lands: one action, one result
        [Fact]
        public async Task ResultWriteFailureNeverActsTwice()
        {
            await using var bot = await RequestBot.StartAsync();
            var id = bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.ChannelId);
            bot.Data.FailNext(nameof(FakeRequestData.Finish), FakeDatabase.MySqlError(2013));
            bot.Data.FailNext(nameof(FakeRequestData.Finish), FakeDatabase.MySqlError(2013));

            await bot.TickAsync();
            bot.Move(TimeSpan.FromSeconds(30));
            await bot.TickAsync();
            Assert.Equal(RequestStates.Running, bot.Row(id).state);

            bot.Move(TimeSpan.FromSeconds(60));
            await bot.TickAsync();

            Assert.Single(bot.Posts());
            Assert.Equal((RequestStates.Done, (string?)"Sent a test message.", (ulong?)Assert.Single(bot.Discord.MessagesIn(FakeDiscord.ChannelId))),
                (bot.Row(id).state, bot.Row(id).result, bot.Row(id).result_number));
            Assert.Equal(3, bot.Data.Calls.Count(call => call == nameof(FakeRequestData.Finish)));
        }

        // Message states MinerBot's database didn't take wait for a later tick, at most the limit of them, and past it the
        // oldest goes, with a log line, as a result does
        [Fact]
        public async Task KeptMessageStatesPastTheLimitDropTheOldest()
        {
            await using var bot = await RequestBot.StartAsync();
            bot.Worker.KeptLimit = 2;
            var ids = Enumerable.Range(1, 3).Select(i => bot.Add(RequestKinds.StatusSet, argument: $"Text {i}")).ToList();

            for (var i = 0; i < 3; i++)
                bot.Data.FailNext(nameof(FakeRequestData.SetDm), FakeDatabase.MySqlError(2013));

            await bot.TickAsync();

            Assert.All(ids, id => Assert.Equal((RequestStates.Done, (string?)null), (bot.Row(id).state, bot.Row(id).dm_state)));
            Assert.Single(bot.Logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.StartsWith($"Request {ids[0]}'s message state was dropped"));

            await bot.TickAsync();

            Assert.Equal([null, RequestDmStates.Sent, RequestDmStates.Sent], ids.Select(id => bot.Row(id).dm_state));
            Assert.Equal(3, bot.Messages().Count);
        }

        // A message to the owner that outlasts the worker's wait may still go, so its state is written once it ends, at a
        // later tick, and the row doesn't stay without one
        [Fact]
        public async Task AMessageThatOutlastsItsWaitIsRecordedWhenItEnds()
        {
            await using var bot = await RequestBot.StartAsync();
            var hold = new TaskCompletionSource();
            bot.Discord.SendHold = hold.Task;
            bot.Worker.MessageLimit = TimeSpan.FromMilliseconds(100);
            var id = bot.Add(RequestKinds.SteamRenew);

            try
            {
                await bot.TickAsync();

                Assert.Equal((RequestStates.Done, (string?)null), (bot.Row(id).state, bot.Row(id).dm_state));
            }
            finally
            {
                hold.SetResult();
            }

            var waited = Stopwatch.StartNew();

            while (bot.Row(id).dm_state == null)
            {
                Assert.True(waited.Elapsed < WaitLimit, "The message's state was never written once it went");
                await Task.Delay(10, TestContext.Current.CancellationToken);
                await bot.TickAsync();
            }

            Assert.Equal(RequestDmStates.Sent, bot.Row(id).dm_state);
            Assert.Single(bot.Messages());
        }

        // Another run's claim from before this run started was cut short by the stop, and one since belongs to a process that
        // runs now, such as a developer's bot on the same database, however long after its start this run's first tick comes
        [Fact]
        public async Task RunningRowsOfAnEarlierRunBecomeUnknownAndAreNeverActedOn()
        {
            await using var bot = await RequestBot.StartAsync();
            var earlier = bot.Add(RequestKinds.GuildLeave, guildId: FakeDiscord.GuildId);
            var other = bot.Add(RequestKinds.GuildLeave, guildId: FakeDiscord.GuildId);
            bot.Data.ClaimFor(earlier, 999, RequestBot.Noon.UtcDateTime.AddMinutes(-1));
            bot.Data.ClaimFor(other, 998, RequestBot.Noon.UtcDateTime.AddMinutes(1));

            await bot.TickAsync();
            await bot.TickAsync();

            Assert.Equal((RequestStates.Unknown, (string?)"The bot stopped while this ran, so it may or may not have happened."), Outcome(bot, earlier));
            Assert.Equal((RequestStates.Running, (ulong?)998), (bot.Row(other).state, bot.Row(other).run_id));
            Assert.Equal([(BotEvents.RequestInterrupted, (string?)"request 1 (guild.leave) was running when the bot stopped, so it may or may not have happened")],
                bot.Events(BotEvents.RequestInterrupted));
            Assert.Single(bot.Data.Calls, call => call == nameof(FakeRequestData.SweepOtherRuns));
            Assert.DoesNotContain("leave server", bot.Discord.StepNames);
        }

        // Database trouble: the status writer's backoff, with one warning

        [Fact]
        public async Task DatabaseDownBacksOffLikeTheStatusWriter()
        {
            await using var bot = await RequestBot.StartAsync();
            bot.Data.Failure = FakeDatabase.MySqlError(1042);
            var start = bot.Clock;
            var tries = new List<int>();

            for (var second = 0; second <= 1200; second += 5)
            {
                bot.Clock = start.AddSeconds(second);
                var before = bot.Data.Calls.Count;

                await bot.TickAsync();

                if (bot.Data.Calls.Count > before)
                    tries.Add(second);
            }

            Assert.Equal([0, 30, 90, 210, 450, 750, 1050], tries);
            Assert.Single(bot.Logger.Entries, entry => entry.Level == LogLevel.Warning);
            Assert.Contains("MinerBot's database didn't answer the request worker (MySqlException, MySQL error 1042)", bot.Logger.Entries.Single(entry => entry.Level == LogLevel.Warning).Message);

            bot.Data.Failure = null;
            bot.Clock = start.AddSeconds(1350);
            await bot.TickAsync();

            Assert.True(bot.Logger.Logged(LogLevel.Information, "MinerBot's database answers the request worker again"));
        }

        // MinerBot's database alone not answering at a start, as with a wrong password for the bot's login, holds the posts
        // too until the pause is read, and while it lasts, this warning is the only place that can say so. An outage once the
        // pause was read holds no post, and its warning says none.
        [Fact]
        public async Task AStartWhileTheDatabaseIsDownSaysThePostsWait()
        {
            await using var bot = await RequestBot.StartAsync();
            bot.Data.Failure = FakeDatabase.MySqlError(1045);

            await bot.TickAsync();

            Assert.True(bot.Pause.Holds(out var known));
            Assert.False(known);
            Assert.Contains("MinerBot's database didn't answer the request worker (MySqlException, MySQL error 1045). Requests wait, and so do the bot's armor, map and lobby "
                + "posts, until the bot has read whether they're paused.", bot.Logger.Entries.Single(entry => entry.Level == LogLevel.Warning).Message);

            bot.Data.Failure = null;
            bot.Move(TimeSpan.FromSeconds(30));
            await bot.TickAsync();

            Assert.False(bot.Pause.Holds(out _));

            bot.Data.Failure = FakeDatabase.MySqlError(1045);
            bot.Move(TimeSpan.FromSeconds(3));
            await bot.TickAsync();

            var warnings = bot.Logger.Entries.Where(entry => entry.Level == LogLevel.Warning).ToList();
            Assert.Equal(2, warnings.Count);
            Assert.Contains("Requests wait, and the bot tries again in 30 seconds", warnings[1].Message);
            Assert.DoesNotContain("posts", warnings[1].Message);
            Assert.False(bot.Pause.Holds(out _));
        }

        // Discord down

        [Fact]
        public async Task OnlyRestartIsActedOnWhileOffDiscord()
        {
            using var exitCode = new ExitCodeKept();
            await using var bot = await RequestBot.StartAsync(fakeStatus: true);
            bot.Status!.Reading = FakeDiscordStatus.Down;
            var clear = bot.Add(RequestKinds.CachesClear);
            var restart = bot.Add(RequestKinds.BotRestart);

            await bot.TickAsync();

            Assert.Null(bot.Row(clear).state);
            Assert.Equal((RequestStates.Done, (string?)"The bot is restarting. It's usually back within a minute."), Outcome(bot, restart));
            Assert.Equal(1, bot.Lifetime.Stops);
            Assert.Empty(bot.Messages());
            Assert.Contains(bot.Data.Calls, call => call == nameof(FakeRequestData.ListOpenIds));
        }

        [Fact]
        public async Task WhileOffDiscordOtherRequestsWaitAndStillExpire()
        {
            await using var bot = await RequestBot.StartAsync(fakeStatus: true);
            bot.Status!.Reading = FakeDiscordStatus.Down;
            var id = bot.Add(RequestKinds.CachesClear);

            await bot.TickAsync();
            Assert.Null(bot.Row(id).state);

            bot.Move(TimeSpan.FromMinutes(3));
            await bot.TickAsync();
            Assert.Null(bot.Row(id).state);

            bot.Status.Reading = FakeDiscordStatus.Up;
            await bot.TickAsync();

            Assert.Equal((RequestStates.Expired, (string?)"The bot saw this request only after it expired at 12:08:00 UTC, so it didn't act on it."), Outcome(bot, id));
        }

        [Fact]
        public async Task JobsNotStartedYetLeaveTheRowQueued()
        {
            await using var bot = await RequestBot.StartAsync(jobs: false);
            var id = bot.Add(RequestKinds.JobNotifications);

            await bot.TickAsync();
            Assert.Null(bot.Row(id).state);

            bot.Jobs.StartFailed = true;
            await bot.TickAsync();

            Assert.Equal((RequestStates.Refused, (string?)"The bot's scheduled jobs didn't start, so no job can run now. The bot's log says why, and a restart tries again."), Outcome(bot, id));
        }

        // Isolation and order

        // A row MySql.Data can't read, such as one with a time of 0000-00-00, is refused by its ID, and the rows behind it go on
        [Fact]
        public async Task APoisonRowDoesntStallTheQueue()
        {
            await using var bot = await RequestBot.StartAsync();
            var poison = bot.Add(RequestKinds.CachesClear);
            var next = bot.Add(RequestKinds.CachesClear);
            bot.Data.Unreadable.Add(poison);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Refused, (string?)"The bot couldn't read this request (DataException), so it didn't act on it."), Outcome(bot, poison));
            Assert.Equal(RequestStates.Done, bot.Row(next).state);
            Assert.DoesNotContain(bot.Logger.Entries, entry => entry.Message.Contains("0000-00-00"));
        }

        // Rows MySql.Data can't read, which the dashboard's code can't write, so they were written without it, as by someone
        // with the dashboard's login but not the key: refused by their IDs as rows in another form are, with that event each
        // minute and the owner's message once an hour, and one line in the log rather than one for each row. A row whose
        // handling throws once it was read is the bot's own trouble, and has no event.
        [Fact]
        public async Task UnreadableRowsAreRefusedAsRowsInAnotherForm()
        {
            await using var bot = await RequestBot.StartAsync();
            var unreadable = Enumerable.Range(0, 5).Select(_ => bot.Add(RequestKinds.CachesClear)).ToList();
            unreadable.ForEach(id => bot.Data.Unreadable.Add(id));
            var thrown = bot.Add(RequestKinds.CachesClear);
            bot.Data.FailNext(nameof(FakeRequestData.CountClaims), new InvalidOperationException("zz-marker"));

            await bot.TickAsync();
            bot.Move(TimeSpan.FromMinutes(1));
            await bot.TickAsync();

            Assert.All(unreadable, id => Assert.Equal((RequestStates.Refused, (string?)"The bot couldn't read this request (DataException), so it didn't act on it."), Outcome(bot, id)));
            Assert.Equal((RequestStates.Refused, (string?)"The bot couldn't read this request (InvalidOperationException), so it didn't act on it."), Outcome(bot, thrown));
            Assert.Equal(["request 1 refused: it isn't in the form the dashboard writes", "requests 2 to 5 refused: they aren't in the form the dashboard writes (4 in one minute)"],
                bot.Events(BotEvents.RequestRejected).Select(row => row.Detail));
            // The owner's message says what the event says: these rows were never checked against the key
            Assert.Equal([$"MinerBot refused dashboard request {unreadable[0]}: it isn't in the form the dashboard writes, so something other than the dashboard wrote it. "
                + "Further refusals within the hour aren't sent; the Bot page lists every one."], bot.Messages());
            Assert.Equal([$"request {unreadable[0]}: refused, not in the dashboard's form"], bot.Events(BotEvents.AlertSent).Select(row => row.Detail));
            Assert.Single(bot.Logger.Entries, entry => entry.Message.Contains("couldn't read a request"));
            Assert.DoesNotContain(bot.Logger.Entries, entry => entry.Message.Contains("0000-00-00") || entry.Message.Contains("zz-marker"));
        }

        // An action that throws, which no kind's action means to, after the request was taken: it ends failed with the
        // exception's type, never its message, which can quote anything, and the owner is told, as after any action
        [Fact]
        public async Task AnActionThatThrowsFailsWithItsTypeOnly()
        {
            await using var bot = await RequestBot.StartAsync();
            bot.Steam.RenewalHold = Task.FromException(new InvalidOperationException("zz-marker"));
            var id = bot.Add(RequestKinds.SteamRenew);
            var next = bot.Add(RequestKinds.CachesClear);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Failed, (string?)"The bot couldn't read this request (InvalidOperationException), so it didn't act on it."), Outcome(bot, id));
            Assert.Equal(RequestDmStates.Sent, bot.Row(id).dm_state);
            Assert.Equal(RequestStates.Done, bot.Row(next).state);
            Assert.DoesNotContain(bot.Logger.Entries, entry => entry.Message.Contains("zz-marker"));
            Assert.All(bot.Messages(), message => Assert.DoesNotContain("zz-marker", message));
        }

        // A pause before a resume, and a status text before its reset, as they were asked for
        [Fact]
        public async Task RequestsRunOneAtATimeInIdOrder()
        {
            await using var bot = await RequestBot.StartAsync();
            var ids = new[]
            {
                bot.Add(RequestKinds.PostsPause, argument: "Maintenance"),
                bot.Add(RequestKinds.PostsResume),
                bot.Add(RequestKinds.StatusSet, argument: "Back soon"),
                bot.Add(RequestKinds.StatusSet)
            };

            await bot.TickAsync();

            Assert.All(ids, id => Assert.Equal(RequestStates.Done, bot.Row(id).state));

            // Each one taken, acted on and finished before the next is taken
            Assert.Equal(["Claim", "SavePause", "Finish", "Claim", "SavePause", "Finish", "Claim", "SaveStatus", "Finish", "Claim", "SaveStatus", "Finish"],
                bot.Data.Calls.Where(call => call is "Claim" or "Finish" || call.StartsWith("Save")));
            Assert.False(bot.Pause.Read().Paused);
            Assert.Null(bot.Presence.Wanted);
        }

        // Events and words

        // A flood of forged rows in one minute is two events: the first at once, and the rest counted at the minute's end
        [Fact]
        public async Task ForgedRowsMakeOneEventAMinute()
        {
            await using var bot = await RequestBot.StartAsync();

            for (var i = 0; i < 60; i++)
                bot.Data.Add(SignedRequests.Row(Fields(RequestKinds.CachesClear, bot.Clock, TwoMinutes), OtherKey));

            for (var tick = 0; tick < 3; tick++)
            {
                await bot.TickAsync();
                bot.Move(TimeSpan.FromSeconds(3));
            }

            Assert.Single(bot.Events(BotEvents.RequestRejected));

            bot.Move(TimeSpan.FromMinutes(1));
            await bot.TickAsync();

            Assert.Equal(["request 1 refused: its signature didn't match", "requests 2 to 60 refused: their signature didn't match (59 in one minute)"],
                bot.Events(BotEvents.RequestRejected).Select(row => row.Detail));
            Assert.All(bot.Data.Rows, row => Assert.Equal(RequestStates.Refused, row.state));
            Assert.Single(bot.Messages());
        }

        // The rest of a minute's forged rows, still being counted when the bot stops cleanly, are recorded at the stop, and
        // the status writer, which stops after the request worker, writes them
        [Fact]
        public async Task ForgedRowsStillCountedAreRecordedAtAStop()
        {
            await using var bot = await RequestBot.StartAsync();

            for (var i = 0; i < 3; i++)
                bot.Data.Add(SignedRequests.Row(Fields(RequestKinds.CachesClear, bot.Clock, TwoMinutes), OtherKey));

            await bot.Worker.StartAsync(TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => bot.Data.Rows.All(row => row.state == RequestStates.Refused), "The forged rows were never refused");
            await bot.Worker.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.True(bot.Worker.ExecuteTask!.IsCompletedSuccessfully);
            Assert.Equal(["request 1 refused: its signature didn't match", "requests 2 to 3 refused: their signature didn't match (2 in one minute)"],
                bot.Events(BotEvents.RequestRejected).Select(row => row.Detail));
        }

        // What each refusal writes, word for word
        [Fact]
        public async Task EveryRefusalIsWrittenToItsRow()
        {
            await using var bot = await RequestBot.StartAsync();
            var now = bot.Clock;

            // A limit spent by another run a minute ago
            var spent = bot.Add(RequestKinds.SteamReconnect);
            bot.Data.ClaimFor(spent, 999, now.AddMinutes(-1));

            ulong Changed(Action<BotRequestRow> change)
            {
                var row = SignedRequests.Signed(RequestKinds.CachesClear, now);
                change(row);
                return bot.Data.Add(row);
            }

            var rows = new (ulong Id, string State, string Result)[]
            {
                (Changed(row => row.signature = new byte[31]), RequestStates.Refused,
                    "This request isn't in the form the dashboard writes, so the bot didn't act on it."),
                (Changed(row => row.request_key = row.request_key.ToUpperInvariant()), RequestStates.Refused,
                    "This request isn't in the form the dashboard writes, so the bot didn't act on it."),
                (bot.Data.Add(SignedRequests.Row(Fields(RequestKinds.CachesClear, now, TwoMinutes), OtherKey)), RequestStates.Refused,
                    "This request's signature didn't match the bot's key, so the bot didn't act on it: the row was written or changed without the key, or the two apps hold different keys. ./status.sh --check on the server says which."),
                (bot.Data.Add(SignedRequests.Row(Fields("bot.explode", now, TwoMinutes))), RequestStates.Refused,
                    "The bot doesn't know this kind of request, so it didn't act on it."),
                (bot.Add(RequestKinds.CachesClear, role: RequestRoles.ServerAdmin), RequestStates.Refused,
                    "Only the Owner or an Admin can ask for this, so the bot didn't act on it."),
                (bot.Data.Add(SignedRequests.Row(Fields(RequestKinds.PostsPause, now, TimeSpan.FromMinutes(11)))), RequestStates.Refused,
                    "This request was made to last longer than this kind may (10 minutes), so the bot didn't act on it."),
                (bot.Data.Add(SignedRequests.Row(Fields(RequestKinds.CachesClear, now.AddMinutes(1), TwoMinutes))), RequestStates.Refused,
                    "This request was made later than the bot's clock says it is now, so the bot didn't act on it."),
                (bot.Data.Add(SignedRequests.Signed(RequestKinds.CachesClear, now.AddMinutes(-5))), RequestStates.Expired,
                    "The bot saw this request only after it expired at 12:03:00 UTC, so it didn't act on it."),
                (bot.Add(RequestKinds.GuildRefresh), RequestStates.Refused,
                    "This request's details don't fit its kind: it needs a server. The bot didn't act on it."),
                (bot.Add(RequestKinds.StatusSet, argument: "See https://example.com"), RequestStates.Refused,
                    "This request's details don't fit its kind: the status text has a link. The bot didn't act on it."),
                (bot.Add(RequestKinds.SteamReconnect), RequestStates.Refused,
                    "The bot takes this at most once every 5 minutes, and last took one at 12:05:00 UTC. Try again after 12:10:00 UTC."),
                (bot.Add(RequestKinds.PostsResume), RequestStates.Refused, "Posts aren't paused."),
                (bot.Add(RequestKinds.GuildLeave, guildId: 9999), RequestStates.Refused, "The bot isn't in that server."),
                (bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.NoEmbedChannelId), RequestStates.Refused,
                    "MinerBot can't post in that channel: it's missing Embed Links."),
                (bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.JoinedChannelId), RequestStates.Refused,
                    "That channel isn't a text or announcement channel of that server.")
            };

            await bot.TickAsync();

            Assert.All(rows, row => Assert.Equal((row.State, (string?)row.Result), Outcome(bot, row.Id)));

            // Not one of them was taken
            Assert.Equal([spent], bot.Data.Rows.Where(row => row.claimed_at != null).Select(row => row.id));
        }

        // A result or an event never repeats a reason, a status text or a name, which only the owner's messages hold
        [Fact]
        public async Task ResultsNeverEchoTheRequestsText()
        {
            await using var bot = await RequestBot.StartAsync();
            bot.Add(RequestKinds.PostsPause, argument: "zz-marker reason", name: "zz-marker name");
            bot.Add(RequestKinds.StatusSet, argument: "zz-marker status", name: "zz-marker name");
            bot.Add(RequestKinds.StatusSet, argument: "zz-marker link https://x", name: "zz-marker name");

            await bot.TickAsync();

            Assert.All(bot.Data.Rows, row => Assert.DoesNotContain("zz-marker", row.result));
            Assert.All(bot.Recorder.PeekEvents(1000), row => Assert.DoesNotContain("zz-marker", row.detail ?? ""));
            Assert.Contains(bot.Messages(), message => message!.Contains("zz-marker reason"));
        }

        // Limits

        // Counted from the table, whichever run took them
        [Fact]
        public async Task LimitsCountClaimsOfEarlierRuns()
        {
            await using var bot = await RequestBot.StartAsync();
            var earlier = bot.Add(RequestKinds.SteamReconnect);
            bot.Data.FinishFor(earlier, 999, bot.Clock.AddMinutes(-2), RequestStates.Done);
            var id = bot.Add(RequestKinds.SteamReconnect);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Refused, (string?)"The bot takes this at most once every 5 minutes, and last took one at 12:04:00 UTC. Try again after 12:09:00 UTC."),
                Outcome(bot, id));
            Assert.Equal(0, bot.Steam.Reconnects);
        }

        // Three test messages to a server in one run; the bot restarts; the fourth is refused by the next run
        [Fact]
        public async Task ALimitSurvivesARestart()
        {
            await using var bot = await RequestBot.StartAsync();

            for (var i = 0; i < 3; i++)
                bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.ChannelId);

            await bot.TickAsync();
            Assert.Equal(3, bot.Posts().Count);

            bot.Move(TimeSpan.FromMinutes(1));
            bot.NewRun();
            var fourth = bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.ChannelId);
            await bot.TickAsync();

            Assert.Equal((RequestStates.Refused,
                (string?)"The bot takes this at most 3 times every 10 minutes, for each server, and has taken 3 since 12:06:00 UTC. Try again after 12:16:00 UTC."),
                Outcome(bot, fourth));
            Assert.Equal(3, bot.Posts().Count);
        }

        [Fact]
        public async Task SharedLimitsCountTogether()
        {
            await using var bot = await RequestBot.StartAsync();

            for (var i = 0; i < 10; i++)
                bot.Add(i % 2 == 0 ? RequestKinds.PostsPause : RequestKinds.PostsResume, argument: i % 2 == 0 ? "Maintenance" : null);

            var eleventh = bot.Add(RequestKinds.PostsPause, argument: "Maintenance");

            await bot.TickAsync();

            Assert.Equal(10, bot.Data.Rows.Count(row => row.state == RequestStates.Done));
            Assert.Equal((RequestStates.Refused,
                (string?)"The bot takes this at most 10 times an hour and has taken 10 since 12:06:00 UTC. Try again after 13:06:00 UTC."), Outcome(bot, eleventh));
        }

        [Fact]
        public async Task PerServerLimitsAreApart()
        {
            await using var bot = await RequestBot.StartAsync();
            await bot.Discord.JoinGuildAsync(members: $"[{FakeDiscord.BotMemberJson(FakeDiscord.BotRoleId)}]");

            // The welcome message
            var before = bot.Posts().Count;

            var first = Enumerable.Range(0, 4).Select(_ => bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.ChannelId)).ToList();
            var other = bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.JoinedGuildId, channelId: FakeDiscord.JoinedChannelId);

            await bot.TickAsync();

            Assert.Equal([RequestStates.Done, RequestStates.Done, RequestStates.Done, RequestStates.Refused], first.Select(id => bot.Row(id).state));
            Assert.Equal(RequestStates.Done, bot.Row(other).state);
            Assert.Equal([FakeDiscord.ChannelId, FakeDiscord.ChannelId, FakeDiscord.ChannelId, FakeDiscord.JoinedChannelId], bot.Posts().Skip(before).Select(step => step.SentTo));
        }

        // Only taken requests count: neither a refusal nor an expiry ever sets claimed_at
        [Fact]
        public async Task RefusedRowsDontUseUpALimit()
        {
            await using var bot = await RequestBot.StartAsync();

            for (var i = 0; i < 3; i++)
                bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.NoEmbedChannelId);

            bot.Data.Add(SignedRequests.Signed(RequestKinds.GuildTestMessage, bot.Clock.AddMinutes(-3), guildId: FakeDiscord.GuildId, channelId: FakeDiscord.ChannelId));
            var good = Enumerable.Range(0, 3).Select(_ => bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.ChannelId)).ToList();

            await bot.TickAsync();

            Assert.All(good, id => Assert.Equal(RequestStates.Done, bot.Row(id).state));
            Assert.Equal(3, bot.Posts().Count);
        }

        // A request is never taken without a count that came back
        [Fact]
        public async Task LimitCountFailureLeavesTheRowQueued()
        {
            await using var bot = await RequestBot.StartAsync();
            var id = bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.ChannelId);
            bot.Data.FailNext(nameof(FakeRequestData.CountClaims), FakeDatabase.MySqlError(1205));

            await bot.TickAsync();

            Assert.Null(bot.Row(id).state);
            Assert.Empty(bot.Posts());

            bot.Move(TimeSpan.FromSeconds(30));
            await bot.TickAsync();

            Assert.Equal(RequestStates.Done, bot.Row(id).state);
        }

        // Stopping

        // A clean stop, such as a deploy's, while a test message waits on Discord: it may or may not have been sent
        [Fact]
        public async Task StopFinishesTheActionInFlightAsUnknown()
        {
            await using var bot = await RequestBot.StartAsync();
            var hold = new TaskCompletionSource();
            bot.Discord.SendHold = hold.Task;
            var id = bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.ChannelId);

            try
            {
                await bot.Worker.StartAsync(TestContext.Current.CancellationToken);
                await WaitUntilAsync(() => bot.Posts().Count == 1, "The test message was never sent");
                await bot.Worker.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
            }
            finally
            {
                hold.SetResult();
            }

            Assert.True(bot.Worker.ExecuteTask!.IsCompletedSuccessfully);
            Assert.Equal((RequestStates.Unknown, (string?)"The bot stopped while this ran, so it may or may not have happened."), Outcome(bot, id));
        }

        // A stop asked for while a request's limit is counted, such as a deploy's, takes nothing, so nothing starts on the
        // bot's way out, and the request waits for the next run, which checks it afresh
        [Fact]
        public async Task AStopAskedForWhileALimitIsCountedTakesNothing()
        {
            await using var bot = await RequestBot.StartAsync();
            var id = bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.ChannelId);
            using var stopping = new CancellationTokenSource();
            bot.Data.BeforeCount = stopping.Cancel;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bot.Worker.TickAsync(stopping.Token));
            await bot.Worker.StopWriteAsync();

            Assert.Equal((null, (DateTime?)null), (bot.Row(id).state, bot.Row(id).claimed_at));
            Assert.DoesNotContain(nameof(FakeRequestData.Claim), bot.Data.Calls);
            Assert.Empty(bot.Posts());
        }

        // Restart

        // In one step log: the claim, the result, the owner's message, and only then the stop
        [Fact]
        public async Task RestartWritesItsResultAndDmsBeforeStopping()
        {
            using var exitCode = new ExitCodeKept();
            await using var bot = await RequestBot.StartAsync();
            var id = bot.Add(RequestKinds.BotRestart);
            var messagesAtStop = -1;
            bot.Lifetime.OnStop = () =>
            {
                bot.Data.Note("StopApplication");
                messagesAtStop = bot.Messages().Count;
            };

            await bot.TickAsync();

            var steps = bot.Data.Calls.Where(call => call is "Claim" or "Finish" or "SetDm" or "StopApplication").ToList();
            Assert.Equal(["Claim", "Finish", "SetDm", "StopApplication"], steps);
            Assert.Equal(1, messagesAtStop);
            Assert.Equal((RequestStates.Done, RequestDmStates.Sent), (bot.Row(id).state, bot.Row(id).dm_state));
            Assert.Equal("Restart the bot, asked for on the MinerBot dashboard by `Owner` (Discord ID 123456789012345678), request 1: The bot is restarting. It's usually back within a minute.",
                Assert.Single(bot.Messages()));
        }

        // The result couldn't be saved, though it landed: no stop, and the correction wins once the database answers
        [Fact]
        public async Task RestartThatCantBeRecordedDoesntHappen()
        {
            using var exitCode = new ExitCodeKept();
            await using var bot = await RequestBot.StartAsync();
            var id = bot.Add(RequestKinds.BotRestart);
            bot.Data.FailNext(nameof(FakeRequestData.Finish), new TimeoutException("The database didn't answer within 15 seconds"), lands: true);

            await bot.TickAsync();

            Assert.Equal(0, bot.Lifetime.Stops);
            Assert.Equal(RequestStates.Done, bot.Row(id).state);

            bot.Move(TimeSpan.FromSeconds(30));
            await bot.TickAsync();

            Assert.Equal((RequestStates.Failed, (string?)"The bot couldn't save the restart first, so it didn't restart. Ask again after 12:16:00 UTC."), Outcome(bot, id));
            Assert.Equal(0, bot.Lifetime.Stops);
            Assert.Empty(bot.Messages());
        }

        [Fact]
        public async Task RestartSetsExitCode3()
        {
            using var exitCode = new ExitCodeKept();
            await using var bot = await RequestBot.StartAsync();
            Environment.ExitCode = 0;
            bot.Add(RequestKinds.BotRestart);

            await bot.TickAsync();

            Assert.Equal(1, bot.Lifetime.Stops);
            Assert.Equal(3, Environment.ExitCode);
        }

        [Fact]
        public async Task RestartIsRefusedInTheFirstFiveMinutes()
        {
            using var exitCode = new ExitCodeKept();
            await using var bot = await RequestBot.StartAsync();

            bot.Clock = RequestBot.Noon.UtcDateTime.AddSeconds(299);
            var early = bot.Add(RequestKinds.BotRestart);
            await bot.TickAsync();

            Assert.Equal((RequestStates.Refused,
                (string?)"The bot started 299 seconds ago, and restarts only from 5 minutes after a start, so a restart can't upset a deploy's checks. Try again after 12:05:00 UTC."),
                Outcome(bot, early));
            Assert.Equal(0, bot.Lifetime.Stops);

            bot.Clock = RequestBot.Noon.UtcDateTime.AddSeconds(301);
            var later = bot.Add(RequestKinds.BotRestart);
            await bot.TickAsync();

            Assert.Equal(RequestStates.Done, bot.Row(later).state);
            Assert.Equal(1, bot.Lifetime.Stops);
        }

        // The bot's minimum uptime outlasts deploy.sh's readiness check: ready_bot's wait for healthy, a poll, its steady wait,
        // and its two inspects' limits, plus a minute, so a restart can't move the restart count a deploy reads
        [Fact]
        public void RestartMinimumUptimeOutlastsTheDeploysReadiness()
        {
            var common = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "bot-source", "common.sh")).Replace("\r\n", "\n");
            var readyBot = Regex.Match(common, @"^ready_bot\(\) \{.*?^\}", RegexOptions.Multiline | RegexOptions.Singleline).Value;
            int Setting(string name) => int.Parse(Regex.Match(readyBot, $@"\b{name}=(\d+)\b").Groups[1].Value);

            var inspects = Regex.Matches(readyBot, @"run_for (\d+) docker container inspect").Select(match => int.Parse(match.Groups[1].Value)).ToList();
            Assert.Equal(2, inspects.Count);

            var readiness = TimeSpan.FromSeconds(Setting("limit") + Setting("poll") + Setting("steady") + inspects.Sum() + 60);

            Assert.True(RequestActions.MinimumUptime > readiness, $"A restart from {RequestActions.MinimumUptime} after a start can land in a deploy's readiness check, which lasts {readiness} with a minute to spare");
        }

        // A stop doesn't wait for a running job, so a restart then could cut the notification job's posts in half
        [Fact]
        public async Task RestartIsRefusedWhileANotificationRunIsUnderWay()
        {
            using var exitCode = new ExitCodeKept();
            await using var bot = await RequestBot.StartAsync();
            var hold = await StartAHeldNotificationRunAsync(bot);

            try
            {
                var id = bot.Add(RequestKinds.BotRestart);

                // A restart taken now would wait for the held run, so the test fails rather than waits with it
                await bot.TickAsync().WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

                Assert.Equal((RequestStates.Refused, (string?)"The notification job is running, so MinerBot doesn't restart now. Try again in a minute."), Outcome(bot, id));
                Assert.Equal(0, bot.Lifetime.Stops);
            }
            finally
            {
                hold.SetResult();
            }
        }

        // A run past JobHandler's limit was cut off, and a restart is the cure for it
        [Fact]
        public async Task ACutOffRunDoesntBlockARestart()
        {
            using var exitCode = new ExitCodeKept();
            await using var bot = await RequestBot.StartAsync();
            var hold = await StartAHeldNotificationRunAsync(bot);
            bot.Worker.RestartMessageLimit = TimeSpan.FromMilliseconds(100);

            try
            {
                bot.Move(TimeSpan.FromSeconds(271));
                var id = bot.Add(RequestKinds.BotRestart);
                await bot.TickAsync();

                Assert.Equal(RequestStates.Done, bot.Row(id).state);
                Assert.Equal(1, bot.Lifetime.Stops);
            }
            finally
            {
                hold.SetResult();
            }
        }

        // The schedule can start a notification run after the restart's check, while its result and message are written: the
        // restart waits for the run to end, rather than cut its posts in half
        [Fact]
        public async Task ARunThatStartsAfterTheRestartsCheckEndsBeforeTheStop()
        {
            using var exitCode = new ExitCodeKept();
            await using var bot = await RequestBot.StartAsync();

            // The baseline, then the settings, so posts aren't held for the pause
            await bot.Discord.RunNotificationJobAsync();
            await bot.TickAsync();
            bot.Discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(100));

            var hold = new TaskCompletionSource();
            bot.Worker.RestartMessageLimit = TimeSpan.FromMilliseconds(100);
            bool? runsAtStop = null;
            bot.Lifetime.OnStop = () => runsAtStop = bot.Jobs.Runs(RequestActions.NotificationJob);
            var id = bot.Add(RequestKinds.BotRestart);

            // The schedule's run comes due once the restart's checks have passed, and is held at its first post
            bot.Data.BeforeClaim = claimed =>
            {
                bot.Discord.SendHold = hold.Task;
                _ = bot.Jobs.RunNowAsync(RequestActions.NotificationJob, 99, TimeSpan.FromMilliseconds(100), CancellationToken.None);
            };

            var tick = bot.TickAsync();

            try
            {
                await WaitUntilAsync(() => bot.Row(id).state == RequestStates.Done && bot.Posts().Count == 1, "The restart's result or the run's first post never came");

                // Well past the wait for the owner's message
                await Task.Delay(500, TestContext.Current.CancellationToken);
                Assert.Equal(0, bot.Lifetime.Stops);
            }
            finally
            {
                hold.SetResult();
            }

            await tick.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.Equal(1, bot.Lifetime.Stops);
            Assert.False(runsAtStop);
        }

        // A notification run asked for from the dashboard, held at its first post
        static async Task<TaskCompletionSource> StartAHeldNotificationRunAsync(RequestBot bot)
        {
            // The baseline, then the settings, so posts aren't held for the pause
            await bot.Discord.RunNotificationJobAsync();
            await bot.TickAsync();

            bot.Discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(100));

            var hold = new TaskCompletionSource();
            bot.Discord.SendHold = hold.Task;

            var run = await bot.Jobs.RunNowAsync("GuildNotificationJob.NotifyAllGuilds", 99, TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
            Assert.Equal(MinerBot_2._0.Handlers.RunNowState.StillRunning, run.State);
            await WaitUntilAsync(() => bot.Posts().Count == 1, "The notification run never reached its post");

            return hold;
        }

        // The next run counts the restart from the table: refused at once by the limit, which comes before the minutes a run
        // waits after its start, as the live check's second restart expects; refused 9 minutes later; taken at 10 minutes and
        // 1 ms
        [Fact]
        public async Task RestartLimitHoldsAcrossARestart()
        {
            using var exitCode = new ExitCodeKept();
            await using var bot = await RequestBot.StartAsync();
            bot.Add(RequestKinds.BotRestart);
            var restartedAt = bot.Clock;

            await bot.TickAsync();
            Assert.Equal(1, bot.Lifetime.Stops);

            bot.Move(TimeSpan.FromMinutes(1));
            bot.NewRun();

            // A minute into the next run, so inside its first 5 minutes too
            bot.Clock = restartedAt.AddMinutes(2);
            var atOnce = bot.Add(RequestKinds.BotRestart);
            await bot.TickAsync();

            Assert.Equal((RequestStates.Refused, (string?)"The bot takes this at most once every 10 minutes, and last took one at 12:06:00 UTC. Try again after 12:16:00 UTC."),
                Outcome(bot, atOnce));

            bot.Clock = restartedAt.AddMinutes(9);
            var tooSoon = bot.Add(RequestKinds.BotRestart);
            await bot.TickAsync();

            Assert.Equal((RequestStates.Refused, (string?)"The bot takes this at most once every 10 minutes, and last took one at 12:06:00 UTC. Try again after 12:16:00 UTC."),
                Outcome(bot, tooSoon));

            bot.Clock = restartedAt.AddMinutes(10).AddMilliseconds(1);
            var allowed = bot.Add(RequestKinds.BotRestart);
            await bot.TickAsync();

            Assert.Equal(RequestStates.Done, bot.Row(allowed).state);
            Assert.Equal(2, bot.Lifetime.Stops);
        }

        // A second restart asked for at once, in the next run's first minute, is refused by the limit, which comes before the
        // kind's own conditions: the words the live check looks for, not those of the 5 minutes since a start
        [Fact]
        public async Task ASecondRestartAtOnceIsRefusedByTheLimit()
        {
            using var exitCode = new ExitCodeKept();
            await using var bot = await RequestBot.StartAsync();
            bot.Add(RequestKinds.BotRestart);

            await bot.TickAsync();
            Assert.Equal(1, bot.Lifetime.Stops);

            bot.Move(TimeSpan.FromSeconds(30));
            bot.NewRun();
            bot.Move(TimeSpan.FromSeconds(30));

            var again = bot.Add(RequestKinds.BotRestart);
            await bot.TickAsync();

            Assert.Equal((RequestStates.Refused, (string?)"The bot takes this at most once every 10 minutes, and last took one at 12:06:00 UTC. Try again after 12:16:00 UTC."),
                Outcome(bot, again));
            Assert.Equal(1, bot.Lifetime.Stops);
        }

        // The next run never acts on a restart an earlier run took: done before the stop, or claimed and cut short
        [Fact]
        public async Task ARestartRowIsNeverActedOnByTheNextRun()
        {
            using var exitCode = new ExitCodeKept();
            await using var bot = await RequestBot.StartAsync();
            var done = bot.Add(RequestKinds.BotRestart);

            await bot.TickAsync();

            var cutShort = bot.Add(RequestKinds.BotRestart);
            bot.Data.ClaimFor(cutShort, bot.Recorder.RunId, bot.Clock);

            bot.Move(TimeSpan.FromSeconds(30));
            bot.NewRun();
            bot.Move(TimeSpan.FromMinutes(6));
            await bot.TickAsync();
            await bot.TickAsync();

            Assert.Equal(RequestStates.Done, bot.Row(done).state);
            Assert.Equal(RequestStates.Unknown, bot.Row(cutShort).state);
            Assert.Equal(1, bot.Lifetime.Stops);
        }

        // Docker never restarts an unhealthy container, so a bot stuck off Discord takes a restart, and its next run tells the
        // owner, once it's up
        [Fact]
        public async Task RestartIsTakenWhileOffDiscordAndTheNextRunTellsTheOwner()
        {
            using var exitCode = new ExitCodeKept();
            await using var bot = await RequestBot.StartAsync(fakeStatus: true);
            bot.Status!.Reading = FakeDiscordStatus.Down;
            var id = bot.Add(RequestKinds.BotRestart, name: "Jane");

            await bot.TickAsync();

            Assert.Equal((RequestStates.Done, (string?)null), (bot.Row(id).state, bot.Row(id).dm_state));
            Assert.Empty(bot.Messages());

            bot.Move(TimeSpan.FromSeconds(30));
            bot.NewRun();
            await bot.TickAsync();
            Assert.Empty(bot.Messages());

            bot.Status.Reading = FakeDiscordStatus.Up;
            await bot.TickAsync();

            Assert.Equal("MinerBot restarted, as `Jane` (Discord ID 123456789012345678) asked on the dashboard (request 1), and is back. It couldn't tell you before it stopped.",
                Assert.Single(bot.Messages()));
            Assert.Equal(RequestDmStates.Sent, bot.Row(id).dm_state);
            Assert.Equal("request 1: Restart the bot, told after the restart", Assert.Single(bot.Events(BotEvents.AlertSent)).Detail);

            await bot.TickAsync();
            Assert.Single(bot.Messages());
        }

        // Only the restarts of the last hour are told: the owner of an older one has long seen the bot back
        [Fact]
        public async Task OnlyTheLastHoursUntoldRestartsAreTold()
        {
            await using var bot = await RequestBot.StartAsync();
            var older = bot.Data.Add(SignedRequests.Signed(RequestKinds.BotRestart, bot.Clock.AddMinutes(-61)));
            var recent = bot.Data.Add(SignedRequests.Signed(RequestKinds.BotRestart, bot.Clock.AddMinutes(-59)));
            bot.Data.FinishFor(older, 999, bot.Clock.AddMinutes(-61), RequestStates.Done);
            bot.Data.FinishFor(recent, 999, bot.Clock.AddMinutes(-59), RequestStates.Done);

            await bot.TickAsync();

            Assert.StartsWith($"MinerBot restarted, as `Owner` (Discord ID 123456789012345678) asked on the dashboard (request {recent}), and is back.",
                Assert.Single(bot.Messages()));
            Assert.Equal(((string?)null, (string?)RequestDmStates.Sent), (bot.Row(older).dm_state, bot.Row(recent).dm_state));
        }

        // The restart works by Docker starting the same container again, whatever the exit code
        [Fact]
        public void ComposeKeepsTheBotRestarting()
        {
            var compose = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "bot-source", "compose.yml")).Replace("\r\n", "\n");
            var bot = Regex.Match(compose, @"^  bot:\n(?<body>(?:    .*\n|\s*\n)+)", RegexOptions.Multiline).Groups["body"].Value;

            Assert.Matches(@"(?m)^    restart: unless-stopped$", bot);
        }
    }
}
