using System.Globalization;
using DataAccessLibrary;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Requests.Models;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Tests
{
    // The request command's looks at MinerBot's database when it stops answering part way, which the database tests can't
    // make happen: a queue that gives each look's answer in turn and moves the command's clock 10 seconds a look. A look
    // that fails keeps what the last one found, and a request no look found is never said to be unsaved unless a look said
    // so. The settings line says what the bot shows.
    public class RequestCommandLookTests
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        const string Key = "00112233445566778899aabbccddeeff";
        const ulong Owner = 1000000000000000001;

        static readonly RequestWaits NoPause = new() { Poll = TimeSpan.Zero };

        static readonly RequestKind Renew = RequestKinds.Find(RequestKinds.SteamRenew)!;

        // Each look's answer in turn, the last one again once they run out; a null answer is a look that found nothing
        sealed class ScriptedQueue(FakeClock clock, params Func<BotRequestRow?>[] looks) : IBotRequestQueue
        {
            int next;

            public BotSettingsRow? Settings { get; init; }

            public Task<BotRequestRow?> GetRequestByKey(string requestKey)
            {
                clock.Advance(TimeSpan.FromSeconds(10));
                return Task.FromResult(looks[Math.Min(next++, looks.Length - 1)]());
            }

            public Task<BotSettingsRow?> GetSettings() => Task.FromResult(Settings);

            public Task<ulong> AddRequest(RequestFields fields, byte[] signature, DataTransaction transaction) => throw new NotSupportedException();
            public Task<BotRequestRow?> GetRequest(ulong id) => throw new NotSupportedException();
            public Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work) => throw new NotSupportedException();
        }

        static string Clock(DateTime time) => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        static BotRequestRow? Unanswered() => throw new TimeoutException("The database didn't answer within 15 seconds");

        static BotRequestRow Row(ulong id, DateTime createdAt, DateTime? claimedAt = null) => new()
        {
            id = id,
            request_key = Key,
            kind = Renew.Word,
            created_at = createdAt,
            expires_at = createdAt + Renew.Expiry,
            state = claimedAt == null ? null : RequestStates.Running,
            claimed_at = claimedAt
        };

        static async Task<(BotRequestRow? Row, string Output)> WaitAsync(ScriptedQueue queue, QueuedRequest queued, FakeClock clock)
        {
            var output = new StringWriter();
            var row = await RequestCommand.WaitForTheBotAsync(queue, queued, Renew, Owner, output, clock, NoPause, Token).WaitAsync(TimeSpan.FromSeconds(30), Token);
            return (row, output.ToString());
        }

        // A request whose commit wasn't confirmed, seen queued and then running, after which no look is answered: it's still
        // the running request 7, waited for by its action limit, never request 0 "still queued" until its expiry
        [Fact]
        public async Task ALookThatFailsKeepsTheRunningRequestItSaw()
        {
            var clock = new FakeClock();
            var start = clock.GetUtcNow().UtcDateTime;
            var claimed = start.AddSeconds(15);
            var queue = new ScriptedQueue(clock, () => Row(7, start), () => Row(7, start, claimed), Unanswered);

            var (row, output) = await WaitAsync(queue, new QueuedRequest(0, Key, Renew.Word, start, start + Renew.Expiry, Confirmed: false, Repeated: false), clock);

            Assert.Null(row);
            Assert.Contains($"Running since {Clock(claimed)} UTC.", output);
            Assert.Contains($"request 7 has run since {Clock(claimed)} UTC, past its 45-second limit. The Bot page shows how it ends. MinerBot's database didn't answer the last look (TimeoutException, SQL state none).", output);
            Assert.DoesNotContain("request 0", output);
            Assert.DoesNotContain("still queued", output);
            Assert.True(clock.GetUtcNow().UtcDateTime < start + Renew.Expiry, "It waited for the queued request's expiry, not the running one's limit");
        }

        // A request seen queued, then not answered for: it was still queued at the last look that was answered
        [Fact]
        public async Task ALookThatFailsKeepsTheQueuedRequestItSaw()
        {
            var clock = new FakeClock();
            var start = clock.GetUtcNow().UtcDateTime;
            var queue = new ScriptedQueue(clock, () => Row(7, start), Unanswered);

            var (row, output) = await WaitAsync(queue, new QueuedRequest(0, Key, Renew.Word, start, start + Renew.Expiry, Confirmed: false, Repeated: false), clock);

            Assert.Null(row);
            Assert.Contains($"request 7 was still queued, past its expiry at {Clock(start + Renew.Expiry)} UTC.", output);
            Assert.DoesNotContain("request 0", output);
        }

        // A commit that wasn't confirmed, and no look answered: whether it was saved isn't known, so the command never says it
        // wasn't saved, nor that the bot never had it
        [Fact]
        public async Task NoLookAnsweredLeavesWhetherItWasSavedUnknown()
        {
            var clock = new FakeClock();
            var start = clock.GetUtcNow().UtcDateTime;
            var queue = new ScriptedQueue(clock, Unanswered);

            var (row, output) = await WaitAsync(queue, new QueuedRequest(0, Key, Renew.Word, start, start + Renew.Expiry, Confirmed: false, Repeated: false), clock);

            Assert.Null(row);
            Assert.Contains("Whether the request was saved isn't known: MinerBot's database answered none of the looks for it by", output);
            Assert.DoesNotContain("wasn't saved", output);
            Assert.DoesNotContain("never had it", output);

            // A look that was answered and found nothing still says so
            clock = new FakeClock();
            start = clock.GetUtcNow().UtcDateTime;
            (row, output) = await WaitAsync(new ScriptedQueue(clock, () => null), new QueuedRequest(0, Key, Renew.Word, start, start + Renew.Expiry, Confirmed: false, Repeated: false), clock);

            Assert.Null(row);
            Assert.Contains("past its expiry, so it wasn't saved and the bot never had it.", output);
        }

        // The bot doesn't show a stored status text that breaks the rules, so neither does the settings line, which gives the
        // rule and never the text
        [Fact]
        public async Task TheSettingsLineSaysTheBotShowsItsDefaultForAStoredTextThatBreaksTheRules()
        {
            var clock = new FakeClock();
            var settings = new BotSettingsRow { status_text = "Visit https://example.com", status_changed_at = clock.GetUtcNow().UtcDateTime };

            var line = await RequestCommand.SettingsAsync(new ScriptedQueue(clock, Unanswered) { Settings = settings });

            Assert.Equal($"The bot's settings now: posts aren't paused; the default status, since the stored status text breaks the rules ({RequestRules.StatusTextProblem(settings.status_text)}).", line);
            Assert.DoesNotContain("example.com", line);

            settings.status_text = "Back soon";
            Assert.Equal("The bot's settings now: posts aren't paused; status text \"Back soon\".", await RequestCommand.SettingsAsync(new ScriptedQueue(clock, Unanswered) { Settings = settings }));
        }
    }
}
