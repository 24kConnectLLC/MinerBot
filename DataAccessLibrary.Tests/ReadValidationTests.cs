using DataAccessLibrary.Guilds;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Requests.Models;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using MinerBot.Shared.Requests;

namespace DataAccessLibrary.Tests
{
    // What the dashboard's reads of the bot's tables are given is checked before any SQL: a filter word must be a word the
    // bot writes, a page and a list must be within their limits, and every time must be UTC, a day's start where a day is
    // asked for. No database is needed: reaching one fails the test.
    public class ReadValidationTests
    {
        readonly NoDatabase database = new();

        static readonly DateTime Day = new(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);

        async Task RefusedAsync(Func<Task> call)
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(call);
            Assert.Equal(0, database.Created);
        }

        BotStatusReads Status() => new(database, NoDatabase.Names);
        BotGuildReads Guilds() => new(database, NoDatabase.Names);

        [Theory]
        [InlineData(-1, 50, null, null)]
        [InlineData(0, 0, null, null)]
        [InlineData(0, 101, null, null)]
        [InlineData(0, 50, "loud", null)]
        [InlineData(0, 50, "Warning", null)]
        [InlineData(0, 50, null, "Bot.Started")]
        [InlineData(0, 50, null, "bot")]
        [InlineData(0, 50, null, "bot.started.again")]
        [InlineData(0, 50, null, "bot.started; DROP TABLE bot_events")]
        public async Task EventPagesOutsideTheirLimitsAreRefused(int page, int pageSize, string? level, string? kind)
        {
            await RefusedAsync(() => Status().ListEvents(new EventQuery { Page = page, PageSize = pageSize, Level = level, Kind = kind }));
        }

        [Fact]
        public async Task EventReadsTakeOnlyTheBotsKindsAndUtc()
        {
            // A read model's time is UTC whatever it's given, so an event read back can't hold a local one
            Assert.Equal(DateTimeKind.Utc, new BotEventRow { at = DateTime.SpecifyKind(Day, DateTimeKind.Local) }.at.Kind);

            await RefusedAsync(() => Status().ListEvents(new EventQuery { Kind = "discord." + new string('c', 33) }));
            await RefusedAsync(() => Status().LatestEvent("Discord.Connected"));
            await RefusedAsync(() => Status().LatestEvent(null!));
            await RefusedAsync(() => Status().LatestEventWith("Request.Rejected", ["signature didn't match"], Day));
            await RefusedAsync(() => Status().LatestEventWith("request.rejected", null!, Day));
            await RefusedAsync(() => Status().LatestEventWith("request.rejected", [], Day));
            await RefusedAsync(() => Status().LatestEventWith("request.rejected", ["signature didn't match", ""], Day));
            await RefusedAsync(() => Status().LatestEventWith("request.rejected", Enumerable.Repeat("didn't match", BotStatusReads.PhraseLimit + 1).ToList(), Day));
            await RefusedAsync(() => Status().LatestEventWith("request.rejected", ["signature didn't match"], DateTime.Now));
            await RefusedAsync(() => Status().FirstEventAfter("discord", null));
            await RefusedAsync(() => Status().CountEvents([], Day, Day));
            await RefusedAsync(() => Status().CountEvents(Enumerable.Repeat("bot.started", 21).ToList(), Day, Day));
            await RefusedAsync(() => Status().CountEvents(["bot.started", "not a kind"], Day, Day));
            await RefusedAsync(() => Status().CountEvents(["bot.started"], DateTime.Now, Day));
            await RefusedAsync(() => Status().CountEvents(["bot.started"], Day, DateTime.Now));
        }

        [Fact]
        public async Task JobRunsOutsideTheirLimitsAreRefused()
        {
            await RefusedAsync(() => Status().ListJobRuns(["ArmorJob.AddColorArrays"], 0));
            await RefusedAsync(() => Status().ListJobRuns(["ArmorJob.AddColorArrays"], 101));
            await RefusedAsync(() => Status().ListJobRuns(Enumerable.Repeat("ArmorJob.AddColorArrays", 101).ToList(), 50));
            await RefusedAsync(() => Status().ListJobRuns([null!], 50));
        }

        // A read of days covers up to 100 days, from a day's start, in UTC, and never ends before it starts
        [Fact]
        public async Task UptimeReadsOutsideTheirLimitsAreRefused()
        {
            await RefusedAsync(() => Status().ListUptimeDays(Day.AddHours(1), Day.AddDays(1)));
            await RefusedAsync(() => Status().ListUptimeDays(Day, Day.AddDays(1).AddHours(1)));
            await RefusedAsync(() => Status().ListUptimeDays(DateTime.SpecifyKind(Day, DateTimeKind.Local), Day.AddDays(1)));
            await RefusedAsync(() => Status().ListUptimeDays(Day, Day.AddDays(-1)));
            await RefusedAsync(() => Status().ListUptimeDays(Day, Day.AddDays(101)));
            await RefusedAsync(() => Status().CountSampleDays(Day.AddMinutes(1), Day.AddDays(1)));
            await RefusedAsync(() => Status().CountSampleDays(Day, Day.AddDays(100).AddMinutes(1)));
            await RefusedAsync(() => Status().CountSampleDays(Day, DateTime.SpecifyKind(Day.AddDays(1), DateTimeKind.Local)));
            await RefusedAsync(() => Status().CountSamples(Day.AddDays(1), Day));
            await RefusedAsync(() => Status().CountSamples(Day, Day.AddDays(100).AddMinutes(1)));
            await RefusedAsync(() => Status().CountSamples(DateTime.Now.AddDays(-1), DateTime.UtcNow));
        }

        [Fact]
        public async Task ServerReadsOutsideTheirLimitsAreRefused()
        {
            await RefusedAsync(() => Guilds().FindChannels(Enumerable.Range(1, BotGuildReads.ChannelLookupLimit + 1).Select(id => (ulong)id).ToList()));
            await RefusedAsync(() => Guilds().ListGuildEvents(1, 0));
            await RefusedAsync(() => Guilds().ListGuildEvents(1, 101));
            await RefusedAsync(() => Guilds().ListMemberDays(1, Day.AddHours(1), Day.AddDays(1)));
            await RefusedAsync(() => Guilds().ListMemberDays(1, Day, Day.AddDays(101)));
            await RefusedAsync(() => Guilds().ListMemberDays(1, Day, Day.AddDays(-1)));
            await RefusedAsync(() => Guilds().MemberTrends(Day.AddMinutes(1)));
            await RefusedAsync(() => Guilds().MemberTrends(DateTime.SpecifyKind(Day, DateTimeKind.Local)));
        }

        // Each limit itself passes: the calls get as far as building their SQL, which needs MinerBot's database
        [Fact]
        public async Task ReadsAtTheirLimitsPass()
        {
            var names = new DatabaseNames(new Dictionary<DBConnectionName, string>());
            var status = new BotStatusReads(database, names);
            var guilds = new BotGuildReads(database, names);
            var kind = "discord." + new string('c', 32);

            Assert.Equal(40, kind.Length);

            await Assert.ThrowsAsync<InvalidOperationException>(() => status.ListEvents(new EventQuery { Page = int.MaxValue, PageSize = 100, Level = EventLevel.Error, Kind = kind }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => status.LatestEvent(kind));
            await Assert.ThrowsAsync<InvalidOperationException>(() => status.LatestEventWith(kind, Enumerable.Repeat("didn't match", BotStatusReads.PhraseLimit).ToList(), Day));
            await Assert.ThrowsAsync<InvalidOperationException>(() => status.CountEvents(Enumerable.Repeat(kind, 20).ToList(), Day, Day.AddDays(1)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => status.ListJobRuns(Enumerable.Repeat("ArmorJob.AddColorArrays", 100).ToList(), 100));
            await Assert.ThrowsAsync<InvalidOperationException>(() => status.ListUptimeDays(Day, Day.AddDays(100)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => status.CountSampleDays(Day, Day.AddDays(100)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => status.CountSamples(Day.AddMinutes(7), Day.AddDays(100).AddMinutes(7)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => guilds.FindChannels(Enumerable.Range(1, BotGuildReads.ChannelLookupLimit).Select(id => (ulong)id).ToList()));
            await Assert.ThrowsAsync<InvalidOperationException>(() => guilds.ListGuildEvents(1, 100));
            await Assert.ThrowsAsync<InvalidOperationException>(() => guilds.ListMemberDays(1, Day, Day.AddDays(100)));
            Assert.Equal(0, database.Created);
        }

        // The bot's reads of the requests, and the dashboard's: a list within its limit, kinds on the request list, UTC times,
        // and a key in the form the dashboard makes, since the column's collation ignores case
        [Fact]
        public async Task RequestReadsOutsideTheirLimitsAreRefused()
        {
            var requests = new BotRequestData(database, NoDatabase.Names);
            var queue = new BotRequestQueue(database, NoDatabase.Names);

            // A read model's times are UTC whatever they're given
            var local = DateTime.SpecifyKind(Day, DateTimeKind.Local);
            Assert.Equal(DateTimeKind.Utc, new BotRequestRow { created_at = local }.created_at.Kind);
            Assert.Equal(DateTimeKind.Utc, new BotRequestRow { claimed_at = local }.claimed_at!.Value.Kind);
            Assert.Equal(DateTimeKind.Utc, new BotSettingsRow { pause_changed_at = local }.pause_changed_at!.Value.Kind);
            Assert.Equal(DateTimeKind.Utc, new ClaimCountRow { oldest = local }.oldest!.Value.Kind);

            await RefusedAsync(() => requests.ListOpenIds(1, 0, restartOnly: false));
            await RefusedAsync(() => requests.ListOpenIds(1, 101, restartOnly: true));
            await RefusedAsync(() => requests.CountClaims([], Day));
            await RefusedAsync(() => requests.CountClaims(null!, Day));
            await RefusedAsync(() => requests.CountClaims(["posts.stop"], Day));
            await RefusedAsync(() => requests.CountClaims([RequestKinds.PostsPause, "Posts.Resume"], Day));
            await RefusedAsync(() => requests.CountClaims([RequestKinds.PostsPause], DateTime.Now));
            await RefusedAsync(() => requests.ListUntoldRestarts(1, local));
            await RefusedAsync(() => queue.GetRequestByKey("00112233445566778899AABBCCDDEEFF"));
            await RefusedAsync(() => queue.GetRequestByKey("00112233445566778899aabbccddeef"));
            await RefusedAsync(() => queue.GetRequestByKey("00112233445566778899aabbccddeef "));
            await RefusedAsync(() => queue.GetRequestByKey(null!));
        }

        // Each limit itself passes
        [Fact]
        public async Task RequestReadsAtTheirLimitsPass()
        {
            var names = new DatabaseNames(new Dictionary<DBConnectionName, string>());
            var requests = new BotRequestData(database, names);
            var queue = new BotRequestQueue(database, names);

            await Assert.ThrowsAsync<InvalidOperationException>(() => requests.ListOpenIds(1, 100, restartOnly: false));
            await Assert.ThrowsAsync<InvalidOperationException>(() => requests.ListOpenIds(1, 1, restartOnly: true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => requests.CountClaims(RequestKinds.All.Select(kind => kind.Word).ToList(), Day, 1));
            await Assert.ThrowsAsync<InvalidOperationException>(() => queue.GetRequestByKey("00112233445566778899aabbccddeeff"));
            Assert.Equal(0, database.Created);
        }
    }
}
