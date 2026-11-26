using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using MinerBot.Shared.Delivery;
using MinerBot_2._0.Posting;
using Steam.Models;

namespace MinerBot.Tests
{
    // The map rules of the notification run, on the Detector alone, with no Discord and no database: each test gives the
    // record as MinerBot's database would hold it and a read, and looks at what the run would save
    public class MapDetectorTests
    {
        const ulong GuildId = 2000, OtherGuildId = 2100, RunId = 7;

        static readonly DateTime Now = new(2026, 11, 24, 15, 0, 0, DateTimeKind.Utc);

        // Steam's time of day n of 1970, as the fixture's maps are dated
        static DateTime Day(double day) => DateTime.UnixEpoch.AddDays(day);

        static PublishedFileDetailsModel Map(int id, double created, double? updated = null)
        {
            var map = FakeGetData.CreateItem(id);
            map.TimeCreated = Day(created);
            map.TimeUpdated = Day(updated ?? created);
            return map;
        }

        static PublishedFileDetailsModel Undated(int id)
        {
            var map = FakeGetData.CreateItem(id);
            map.TimeUpdated = default;
            return map;
        }

        static BaselineRow MapsBaseline(double baselineDay, DateTime? checkedAt = null) => new()
        {
            source = PostSource.Maps,
            taken_at = Now.AddDays(-1),
            taken_by_run = 1,
            reason = BaselineReason.First,
            item_count = 5,
            baseline_time = Day(baselineDay),
            checked_at = checkedAt ?? Now.AddMinutes(-5),
            checked_by_run = 1
        };

        static SeenRow SeenMap(int id, double updated, DateTime? queuedAt = null, string state = SeenState.Queued) => new()
        {
            source = PostSource.Maps,
            item_id = (ulong)id,
            state = state,
            first_seen_at = Now.AddDays(-1),
            last_seen_at = Now.AddDays(-1),
            updated_at = Day(updated),
            queued_at = queuedAt
        };

        static DetectorResult Detect(IReadOnlyList<PublishedFileDetailsModel> read, BaselineRow? baseline, IReadOnlyList<SeenRow>? seen = null, bool owed = true,
            DateTime? olderBotSince = null, IReadOnlyList<QueueRow>? hidden = null) =>
            Detector.Detect(new DetectorInput
            {
                Now = Now,
                RunId = RunId,
                Reads = new PostingReads(null, read, null),
                Baselines = baseline == null ? [] : [baseline],
                Seen = seen ?? [],
                Hidden = hidden ?? [],
                Owed = new Dictionary<string, IReadOnlyList<ulong>> { [PostSource.Maps] = owed ? [GuildId] : [] },
                OlderBot = olderBotSince is { } since ? new Dictionary<string, DateTime> { [PostSource.Maps] = since } : new Dictionary<string, DateTime>()
            });

        // A map's row in the queue for a server, for the update of the day given
        static QueueRow MapRow(ulong id, ulong guildId, int map, double updated, string state = QueueState.Pending, string? reason = null) => new()
        {
            id = id,
            guild_id = guildId,
            source = PostSource.Maps,
            item_id = (ulong)map,
            revision = Detector.Revision(Map(map, updated)),
            is_new = true,
            queued_at = Now.AddHours(-1),
            state = state,
            reason = reason,
            settled_at = state == QueueState.Pending ? null : Now.AddMinutes(-30)
        };

        static List<(ulong Id, bool IsNew)> Queued(DetectorResult result) => result.Detection.Queue.Select(item => (item.item_id, item.is_new)).ToList();

        static SeenRow SeenOf(DetectorResult result, int id) => Assert.Single(result.Detection.Seen, row => row.item_id == (ulong)id);

        // The first good read

        // Every map of the first good read is taken as already posted, with the oldest update as the baseline's time
        [Fact]
        public void FirstGoodReadIsTheBaselineAndQueuesNothing()
        {
            var result = Detect([Map(3, 3), Map(2, 2), Map(1, 1)], baseline: null);

            Assert.Empty(result.Detection.Queue);
            Assert.All(result.Detection.Seen, row => Assert.Equal(SeenState.Baseline, row.state));
            Assert.Equal([1ul, 2, 3], result.Detection.Seen.Select(row => row.item_id).Order().ToList());

            var baseline = Assert.Single(result.Detection.Baselines);
            Assert.Equal((BaselineReason.First, 3u, Day(1), Now, RunId), (baseline.reason, baseline.item_count, baseline.baseline_time, baseline.checked_at, baseline.checked_by_run));
            Assert.Equal(new TakenBaseline(PostSource.Maps, BaselineReason.First, 3), Assert.Single(result.Baselines));
        }

        // As the baseline, year 1 would make every later map look new
        [Fact]
        public void MapWithoutAnUpdateTimeDoesNotSetTheBaseline()
        {
            Assert.True(Detect([Undated(1)], baseline: null).Detection.IsEmpty);

            var result = Detect([Map(2, 2), Undated(1)], baseline: null);

            Assert.Equal(Day(2), Assert.Single(result.Detection.Baselines).baseline_time);
            Assert.Equal([2ul], result.Detection.Seen.Select(row => row.item_id));
        }

        // Its creation time is after the baseline, so without the guard it would be posted as new
        [Fact]
        public void MapWithoutAnUpdateTimeIsNotPosted()
        {
            var undated = Undated(5);
            undated.TimeCreated = Day(5);

            Assert.Empty(Detect([undated, Map(1, 1)], MapsBaseline(1), [SeenMap(1, 1)]).Detection.Queue);
        }

        [Fact]
        public void MapIsPostedOnceSteamSendsItsUpdateTime()
        {
            Assert.Equal([(5ul, true)], Queued(Detect([Map(5, 5), Map(1, 1)], MapsBaseline(1), [SeenMap(1, 1)])));
        }

        // New and updated

        [Fact]
        public void MapMadeAfterTheBaselineIsNew()
        {
            var result = Detect([Map(6, 3)], MapsBaseline(1));

            Assert.Equal([(6ul, true)], Queued(result));
            Assert.Equal((SeenState.Queued, Day(3), Now), (SeenOf(result, 6).state, SeenOf(result, 6).updated_at, SeenOf(result, 6).queued_at));
        }

        // Made before the first read, hidden during it, and updated after it
        [Fact]
        public void MapUpdatedAfterTheBaselineIsAnUpdate()
        {
            Assert.Equal([(6ul, false)], Queued(Detect([Map(6, 0.5, updated: 3)], MapsBaseline(1))));
        }

        // Steam ranks maps by their last update, so a map that moves into the read from below, when one above it is hidden or
        // removed, is no newer than the oldest map of the first read: it isn't news, and it's recorded so later reads skip it
        [Theory]
        [InlineData(0.5)]
        [InlineData(1)]
        public void MapThatMovedUpFromBelowIsRecordedNotQueued(double updated)
        {
            var result = Detect([Map(6, 0.2, updated)], MapsBaseline(1));

            Assert.Empty(result.Detection.Queue);
            Assert.Equal((SeenState.Baseline, Day(updated)), (SeenOf(result, 6).state, SeenOf(result, 6).updated_at));
        }

        [Fact]
        public void SeenMapUpdatedLaterIsAnUpdate()
        {
            var result = Detect([Map(3, 3, updated: 10)], MapsBaseline(1), [SeenMap(3, 3, queuedAt: Now.AddDays(-2))]);

            Assert.Equal([(3ul, false)], Queued(result));
            Assert.Equal((Day(10), Now), (SeenOf(result, 3).updated_at, SeenOf(result, 3).queued_at));
            Assert.Equal(Detector.Revision(Map(3, 3, updated: 10)), Assert.Single(result.Detection.Queue).revision);
        }

        [Fact]
        public void SeenMapNotUpdatedIsNothingNew()
        {
            var result = Detect([Map(3, 3)], MapsBaseline(1), [SeenMap(3, 3)]);

            Assert.Empty(result.Detection.Queue);
            Assert.Empty(result.Detection.Seen);
        }

        // A read can hold one map twice, when the ranking moves between two page requests
        [Fact]
        public void MapFoundTwiceInOneReadIsQueuedOnce()
        {
            Assert.Equal([(6ul, true)], Queued(Detect([Map(6, 3), Map(6, 3)], MapsBaseline(1))));
        }

        // The cooldown

        // An update within a day of the map's last queueing is passed over, and not recorded, so the first read after the
        // cooldown posts it. The day is on the bot's clock, whatever Steam's times say.
        [Fact]
        public void UpdateWithinTheCooldownIsPassedOverAndNotRecorded()
        {
            var seen = SeenMap(3, 3, queuedAt: Now - PostingLimits.Cooldown + TimeSpan.FromMinutes(1));
            var result = Detect([Map(3, 3, updated: 3.0001)], MapsBaseline(1), [seen]);

            Assert.Empty(result.Detection.Queue);
            Assert.Empty(result.Detection.Seen);
        }

        [Fact]
        public void UpdateAfterTheCooldownIsPosted()
        {
            var seen = SeenMap(3, 3, queuedAt: Now - PostingLimits.Cooldown);

            Assert.Equal([(3ul, false)], Queued(Detect([Map(3, 3, updated: 3.0001)], MapsBaseline(1), [seen])));
        }

        // The cooldown counts from the queueing, whatever the map was then, so an update of a map the first read took counts
        // from its own queueing
        [Fact]
        public void BaselineMapStartsNoCooldown()
        {
            Assert.Equal([(3ul, false)], Queued(Detect([Map(3, 3, updated: 3.0001)], MapsBaseline(1), [SeenMap(3, 3, state: SeenState.Baseline)])));
        }

        // Nothing was posted then, so no cooldown starts, and the server that turns maps on gets the next update at once
        [Fact]
        public void MapNoServerIsOwedStartsNoCooldown()
        {
            var result = Detect([Map(3, 3, updated: 10)], MapsBaseline(1), [SeenMap(3, 3)], owed: false);

            Assert.Empty(result.Detection.Queue);
            Assert.Equal((SeenState.Queued, Day(10), (DateTime?)null), (SeenOf(result, 3).state, SeenOf(result, 3).updated_at, SeenOf(result, 3).queued_at));
        }

        // Old news

        // In a read after a gap, a map updated more than a day before the newest of the read is recorded and never posted
        [Fact]
        public void MapsOlderThanADayBeforeTheNewestAreRecordedNotQueued()
        {
            var result = Detect([Map(9, 9), Map(8, 8.5), Map(7, 7.9)], MapsBaseline(1, checkedAt: Now - PostingLimits.RecentRead));

            Assert.Equal([(9ul, true), (8ul, true)], Queued(result));
            Assert.Equal((SeenState.Baseline, Day(7.9)), (SeenOf(result, 7).state, SeenOf(result, 7).updated_at));
        }

        // A read minutes after the last posts what's new to it however long before the newest Steam dates it: a map made public a
        // day after its last update, and a posted map's update that Steam shows a day late. Only a read after a gap has old news.
        [Fact]
        public void OldNewsIsOnlyForAReadAfterAGap()
        {
            Assert.Equal([(9ul, true), (8ul, true), (7ul, true)], Queued(Detect([Map(9, 9), Map(8, 8.5), Map(7, 7.9)], MapsBaseline(1))));

            var posted = SeenMap(3, 3, queuedAt: Now - PostingLimits.Cooldown);

            Assert.Equal([(9ul, true), (3ul, false)], Queued(Detect([Map(9, 9), Map(3, 3, updated: 3.5)], MapsBaseline(1), [posted])));
            Assert.Equal([(9ul, true)], Queued(Detect([Map(9, 9), Map(3, 3, updated: 3.5)], MapsBaseline(1, checkedAt: Now - PostingLimits.RecentRead), [posted])));
        }

        // Shown again

        // A map Steam stopped showing while its posts waited, which dropped them as hidden, shown again with the update they
        // were for: those rows go back, so it goes where it never went. Not a row for an earlier update, which a later post
        // made old, and not when its record is a baseline.
        [Fact]
        public void MapShownAgainReopensItsRowsDroppedAsHidden()
        {
            QueueRow[] hidden = [MapRow(41, GuildId, 3, 3, QueueState.Dropped, DeliveryReasons.Hidden), MapRow(42, OtherGuildId, 3, 2, QueueState.Dropped, DeliveryReasons.Hidden)];
            var result = Detect([Map(3, 2, updated: 3)], MapsBaseline(1), [SeenMap(3, 3, queuedAt: Now.AddHours(-1))], hidden: hidden);

            Assert.Equal([41ul], result.Detection.ReopenMaps);
            Assert.Empty(result.Detection.Queue);
            Assert.Empty(result.Detection.Seen);

            Assert.Empty(Detect([Map(3, 2, updated: 3)], MapsBaseline(1), [SeenMap(3, 3, state: SeenState.Baseline)], hidden: hidden).Detection.ReopenMaps);
        }

        // Shown again with a newer update, such as its author's fix made while Steam didn't show it: the rows dropped as hidden
        // for the update it was last queued for go back too, at once, in the newer update's cooldown as after it, so it goes
        // where it never went, as it is then, rather than a day later as an update, or never once other maps push it out of
        // the read; and so do the rows of newer updates dropped with them, which a post that wasn't made showed, but never a
        // row for an earlier update, or another map's: the read's hidden rows are every map's in it, such as one an older bot's
        // baseline took as posted. The newer update itself waits for its cooldown, or is queued after it, as any map's does.
        [Fact]
        public void MapShownAgainWithANewerUpdateReopensItsRowsDroppedAsHidden()
        {
            QueueRow[] hidden = [MapRow(41, GuildId, 3, 3, QueueState.Dropped, DeliveryReasons.Hidden), MapRow(42, OtherGuildId, 3, 2, QueueState.Dropped, DeliveryReasons.Hidden),
                MapRow(43, GuildId, 3, 4, QueueState.Dropped, DeliveryReasons.Hidden), MapRow(44, GuildId, 5, 4, QueueState.Dropped, DeliveryReasons.Hidden)];
            var takenAsPosted = SeenMap(5, 4, state: SeenState.Baseline);
            var inCooldown = Detect([Map(3, 2, updated: 4), Map(5, 2, updated: 4)], MapsBaseline(1), [SeenMap(3, 3, queuedAt: Now.AddHours(-1)), takenAsPosted], hidden: hidden);

            Assert.Equal([41ul, 43ul], inCooldown.Detection.ReopenMaps);
            Assert.Empty(inCooldown.Detection.Queue);
            Assert.Empty(inCooldown.Detection.Seen);

            var afterIt = Detect([Map(3, 2, updated: 4), Map(5, 2, updated: 4)], MapsBaseline(1), [SeenMap(3, 3, queuedAt: Now.AddDays(-2)), takenAsPosted], hidden: hidden);

            Assert.Equal([41ul, 43ul], afterIt.Detection.ReopenMaps);
            Assert.Equal([(3ul, false)], Queued(afterIt));

            Assert.Empty(Detect([Map(3, 2, updated: 4)], MapsBaseline(1), [SeenMap(3, 3, state: SeenState.Baseline)], hidden: hidden).Detection.ReopenMaps);
        }

        // At a new baseline after an older bot, which may have posted the map while it was shown, its rows dropped as hidden
        // stay dropped: it's taken as already posted, and later runs don't reopen them either
        [Fact]
        public void OlderBotsBaselineNeverReopensAMapShownAgain()
        {
            QueueRow[] hidden = [MapRow(41, GuildId, 3, 3, QueueState.Dropped, DeliveryReasons.Hidden)];
            var result = Detect([Map(3, 2, updated: 3)], MapsBaseline(1), [SeenMap(3, 3, queuedAt: Now.AddDays(-3))], hidden: hidden, olderBotSince: Now.AddHours(-3));

            Assert.Empty(result.Detection.ReopenMaps);
            Assert.Equal((SeenState.Baseline, Day(3)), (SeenOf(result, 3).state, SeenOf(result, 3).updated_at));

            Assert.Empty(Detect([Map(3, 2, updated: 3)], MapsBaseline(1), [SeenOf(result, 3)], hidden: hidden).Detection.ReopenMaps);
        }

        // Read depth

        // Five while the maps' last good read is under 10 minutes old, twenty after a gap, at a first baseline, and at a new
        // one after an older bot
        [Theory]
        [InlineData(5, false, PostingLimits.LatestMaps)]
        [InlineData(9.99, false, PostingLimits.LatestMaps)]
        [InlineData(10, false, PostingLimits.DeepMaps)]
        [InlineData(60, false, PostingLimits.DeepMaps)]
        [InlineData(-1, false, PostingLimits.DeepMaps)]
        [InlineData(5, true, PostingLimits.DeepMaps)]
        public void ReadIsDeepAfterAGapAndAtABaseline(double minutesAgo, bool baseline, int depth)
        {
            Assert.Equal(depth, PostingLimits.MapReadDepth(Now, Now.AddMinutes(-minutesAgo), baseline));
        }

        [Fact]
        public void ReadIsDeepWithoutAGoodReadBefore()
        {
            Assert.Equal(PostingLimits.DeepMaps, PostingLimits.MapReadDepth(Now, null, baseline: false));
        }

        // A new baseline after an older bot

        // Each map of the read is taken as already posted, with a new baseline time, and what was queued stays queued
        [Fact]
        public void OlderBotsBaselineTakesTheReadWithANewTime()
        {
            var since = Now.AddHours(-3);
            var result = Detect([Map(9, 9), Map(3, 3, updated: 8), Map(2, 2)], MapsBaseline(1),
                [SeenMap(3, 3, queuedAt: Now.AddDays(-3)), SeenMap(2, 2, queuedAt: Now.AddDays(-3))], olderBotSince: since);

            Assert.Empty(result.Detection.Queue);

            var baseline = Assert.Single(result.Detection.Baselines);
            Assert.Equal((BaselineReason.OlderBot, Day(2), 3u, Now), (baseline.reason, baseline.baseline_time, baseline.item_count, baseline.taken_at));
            Assert.Equal(new TakenBaseline(PostSource.Maps, BaselineReason.OlderBot, 3, since), Assert.Single(result.Baselines));

            Assert.Equal((SeenState.Baseline, Day(9)), (SeenOf(result, 9).state, SeenOf(result, 9).updated_at));
            Assert.Equal((SeenState.Baseline, Day(8)), (SeenOf(result, 3).state, SeenOf(result, 3).updated_at));
            Assert.DoesNotContain(result.Detection.Seen, row => row.item_id == 2);
        }

        // Every run that read the maps records it, which the next run's depth goes by
        [Fact]
        public void RunThatReadTheMapsChecksItsBaseline()
        {
            var result = Detect([Map(3, 3)], MapsBaseline(1, checkedAt: Now.AddMinutes(-5)), [SeenMap(3, 3)]);

            var baseline = Assert.Single(result.Detection.Baselines);
            Assert.Equal((Now, RunId, BaselineReason.First, Day(1)), (baseline.checked_at, baseline.checked_by_run, baseline.reason, baseline.baseline_time));
            Assert.Empty(result.Baselines);
        }

        // A read that failed leaves the source alone, its baseline included
        [Fact]
        public void FailedReadLeavesTheMapsAlone()
        {
            var result = Detector.Detect(new DetectorInput { Now = Now, RunId = RunId, Baselines = [MapsBaseline(1)] });

            Assert.True(result.Detection.IsEmpty);
        }
    }
}
