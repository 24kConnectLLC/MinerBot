using MinerBot_2._0.Jobs;
using Steam.Models;

namespace MinerBot.Tests
{
    // Steam's reader leaves a missing time as the default, year 1, which says nothing about when a map changed
    public class SeenMapsTests
    {
        static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

        static PublishedFileDetailsModel Undated(int id)
        {
            var map = FakeGetData.CreateItem(id);
            map.TimeUpdated = default;
            return map;
        }

        // The map updated again, on that day of Steam's clock
        static PublishedFileDetailsModel Updated(int id, int days)
        {
            var map = FakeGetData.CreateItem(id);
            map.TimeUpdated = DateTime.UnixEpoch.AddDays(days);
            return map;
        }

        // As the baseline, year 1 would make every later map look new
        [Fact]
        public void MapWithoutAnUpdateTimeDoesNotSetTheBaseline()
        {
            var seen = new SeenMaps();

            Assert.Empty(seen.FindNew([Undated(1)], Now));
            Assert.Null(seen.BaselineTime);

            Assert.Empty(seen.FindNew([FakeGetData.CreateItem(2), Undated(1)], Now));
            Assert.Equal(DateTime.UnixEpoch.AddDays(2), seen.BaselineTime);
        }

        // Its creation time is after the baseline, so without the guard it would be posted as new
        [Fact]
        public void MapWithoutAnUpdateTimeIsNotPosted()
        {
            var seen = new SeenMaps();
            seen.FindNew([FakeGetData.CreateItem(1)], Now);

            Assert.Empty(seen.FindNew([Undated(5), FakeGetData.CreateItem(1)], Now));
        }

        [Fact]
        public void MapIsPostedOnceSteamSendsItsUpdateTime()
        {
            var seen = new SeenMaps();
            seen.FindNew([FakeGetData.CreateItem(1)], Now);
            seen.FindNew([Undated(5), FakeGetData.CreateItem(1)], Now);

            var post = Assert.Single(seen.FindNew([FakeGetData.CreateItem(5), FakeGetData.CreateItem(1)], Now));
            Assert.Equal(5UL, post.Map.PublishedFileId);
            Assert.True(post.IsNew);
        }

        // A map found while posts were paused makes the read one map deeper, until a run that posts delivers it, gives up on
        // it or doesn't find it again, which that run counts, since such a map is never posted
        [Fact]
        public void MapsThatWaitDeepenTheReadUntilARunSettlesThem()
        {
            var seen = new SeenMaps();
            seen.FindNew([FakeGetData.CreateItem(1)], Now);
            seen.Hold(seen.FindNew([FakeGetData.CreateItem(3), FakeGetData.CreateItem(2), FakeGetData.CreateItem(1)], Now));

            Assert.Equal(SeenMaps.LatestCount + 2, seen.ReadCount);

            // Map 2 was hidden meanwhile, or pushed past the read, and map 3 reached no server
            Assert.Equal(1, seen.Commit(seen.FindNew([FakeGetData.CreateItem(3), FakeGetData.CreateItem(1)], Now), delivered: 0, postedAt: Now));

            Assert.Equal(SeenMaps.LatestCount + 1, seen.ReadCount);

            Assert.Equal(0, seen.Commit(seen.FindNew([FakeGetData.CreateItem(3), FakeGetData.CreateItem(1)], Now), delivered: 1, postedAt: Now));

            Assert.Equal(SeenMaps.LatestCount, seen.ReadCount);
        }

        // A map updated again in its cooldown is neither posted nor kept waiting, but it takes a place in Steam's list above the
        // maps that wait, so while maps wait, each map a read passes over that way makes the read one map deeper too, until a
        // run that posts settles the maps that waited
        [Fact]
        public void MapsPassedOverInTheirCooldownWhileMapsWaitDeepenTheRead()
        {
            var seen = new SeenMaps();
            seen.FindNew([FakeGetData.CreateItem(1)], Now);
            seen.Commit(seen.FindNew([FakeGetData.CreateItem(2), FakeGetData.CreateItem(1)], Now), delivered: 1, postedAt: Now);

            // Paused, with no map waiting: the read stays at five
            seen.Hold(seen.FindNew([Updated(2, days: 10), FakeGetData.CreateItem(1)], Now.AddHours(1)));

            Assert.Equal(SeenMaps.LatestCount, seen.ReadCount);

            // Map 3 waits, with map 2 updated again above it
            seen.Hold(seen.FindNew([Updated(2, days: 11), FakeGetData.CreateItem(3), FakeGetData.CreateItem(1)], Now.AddHours(2)));

            Assert.Equal(SeenMaps.LatestCount + 2, seen.ReadCount);

            Assert.Equal(0, seen.Commit(seen.FindNew([Updated(2, days: 11), FakeGetData.CreateItem(3), FakeGetData.CreateItem(1)], Now.AddHours(3)), delivered: 1,
                postedAt: Now.AddHours(3)));

            Assert.Equal(SeenMaps.LatestCount, seen.ReadCount);
        }

        // Maps updated after one that waits, which reach no server either, as in an outage after the resume, take places above
        // it without having waited themselves: each makes the read one map deeper too, so more of them can't push the map
        // that waits out of the read, until a run that posts delivers it
        [Fact]
        public void MapsThatReachedNoServerAboveAMapThatWaitsDeepenTheRead()
        {
            var seen = new SeenMaps();
            seen.FindNew([FakeGetData.CreateItem(1)], Now);
            seen.Hold(seen.FindNew([FakeGetData.CreateItem(2), FakeGetData.CreateItem(1)], Now));

            // Maps 3 to 7 are updated after map 2, and the run reaches no server
            var latest = Enumerable.Range(1, 7).Reverse().Select(FakeGetData.CreateItem).ToList();
            seen.Commit(seen.FindNew(latest.Take(seen.ReadCount).ToList(), Now), delivered: 0, postedAt: Now);

            Assert.Equal(SeenMaps.LatestCount + 6, seen.ReadCount);

            // Map 8 too, which leaves map 2 seventh
            latest.Insert(0, FakeGetData.CreateItem(8));

            Assert.Equal(0, seen.Commit(seen.FindNew(latest.Take(seen.ReadCount).ToList(), Now), delivered: 0, postedAt: Now));
            Assert.Equal(SeenMaps.LatestCount + 7, seen.ReadCount);

            Assert.Equal(0, seen.Commit(seen.FindNew(latest.Take(seen.ReadCount).ToList(), Now), delivered: 7, postedAt: Now));
            Assert.Equal(SeenMaps.LatestCount, seen.ReadCount);
        }

        // A map updated again in its cooldown before the maps that wait is below all of them. When a run that posts delivers
        // every map that waited but not that one, it waits in their place, so the read still reaches it, until a run delivers it.
        [Fact]
        public void AMapARunDidntDeliverBelowTheLastMapsThatWaitedWaitsInTheirPlace()
        {
            var seen = new SeenMaps();
            seen.FindNew([FakeGetData.CreateItem(1)], Now);
            seen.Commit(seen.FindNew([FakeGetData.CreateItem(2), FakeGetData.CreateItem(1)], Now), delivered: 1, postedAt: Now);

            // Paused: map 2 is updated again an hour later, and then maps 3 to 7 wait above it
            var update = FakeGetData.CreateItem(2);
            update.TimeUpdated = update.TimeUpdated.AddHours(1);
            List<PublishedFileDetailsModel> latest = [update, FakeGetData.CreateItem(1)];

            for (var id = 3; id <= 7; id++)
            {
                latest.Insert(0, FakeGetData.CreateItem(id));
                seen.Hold(seen.FindNew(latest.Take(seen.ReadCount).ToList(), Now.AddHours(1)));
            }

            // After its cooldown, maps 3 to 7 go out, and map 2's update reaches no server
            Assert.Equal(0, seen.Commit(seen.FindNew(latest.Take(seen.ReadCount).ToList(), Now.Add(SeenMaps.Cooldown)), delivered: 5,
                postedAt: Now.Add(SeenMaps.Cooldown)));

            var post = Assert.Single(seen.FindNew(latest.Take(seen.ReadCount).ToList(), Now.Add(SeenMaps.Cooldown)));
            Assert.Equal(2UL, post.Map.PublishedFileId);

            Assert.Equal(0, seen.Commit([post], delivered: 1, postedAt: Now.Add(SeenMaps.Cooldown)));
            Assert.Equal(SeenMaps.LatestCount, seen.ReadCount);
        }

        // Without maps that wait, as outside a pause, maps that reached no server don't wait either: the read stays at five, and
        // the next run finds them again among the latest
        [Fact]
        public void MapsThatReachedNoServerWhileNoneWaitLeaveTheReadAtFive()
        {
            var seen = new SeenMaps();
            seen.FindNew([FakeGetData.CreateItem(1)], Now);

            Assert.Equal(0, seen.Commit(seen.FindNew([FakeGetData.CreateItem(3), FakeGetData.CreateItem(2), FakeGetData.CreateItem(1)], Now), delivered: 1,
                postedAt: Now));
            Assert.Equal(SeenMaps.LatestCount, seen.ReadCount);
        }
    }
}
