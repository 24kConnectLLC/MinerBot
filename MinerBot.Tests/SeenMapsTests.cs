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
    }
}
