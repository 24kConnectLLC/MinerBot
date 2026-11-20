using DataAccessLibrary;
using DataAccessLibrary.Armor;
using Microsoft.Extensions.Caching.Memory;
using MinerBot_2._0.Services;

namespace MinerBot.Tests
{
    // The dashboard's "clear caches": the cached armor names and latest maps are read again after it, and a read that was under
    // way when it came doesn't put its old list back for the 5 minutes the armor names are kept
    public class CacheResetTests : IDisposable
    {
        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);
        static readonly TimeSpan ArmorNamesKept = TimeSpan.FromMinutes(5);

        readonly MemoryCache cache = new(new MemoryCacheOptions());
        readonly CacheReset reset = new();

        public void Dispose() => cache.Dispose();

        [Fact]
        public async Task ClearEmptiesBothEntries()
        {
            var database = new FakeDatabase().Rows().Rows();
            var armor = new ArmorData(database, cache, reset);
            var steamCalls = new List<string>();
            var maps = new SteamAPIService(new FakeGetData(steamCalls.Add), cache, reset);

            for (var read = 0; read < 2; read++)
            {
                await armor.GetAllArmorNamesCache(ArmorNamesKept);
                await maps.GetLatestWorkshopItems();
            }

            Assert.Equal((1, 1), (database.Calls, steamCalls.Count));

            reset.Clear();

            Assert.Equal(FakeDatabase.ArmorNames, await armor.GetAllArmorNamesCache(ArmorNamesKept));
            Assert.Equal(5, (await maps.GetLatestWorkshopItems()).Count);
            Assert.Equal((2, 2), (database.Calls, steamCalls.Count));
        }

        // The read started before the clear and ends after it, so its list, from before, isn't kept
        [Fact]
        public async Task ClearDuringAReadKeepsTheOldListOut()
        {
            var database = new FakeDatabase().Block().Rows();
            var armor = new ArmorData(database, cache, reset);

            try
            {
                var reading = Task.Run(() => armor.GetAllArmorNamesCache(ArmorNamesKept), TestContext.Current.CancellationToken);
                await database.FirstCall.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

                reset.Clear();
                database.Gate.Set();

                Assert.Equal(FakeDatabase.ArmorNames, await reading.WaitAsync(WaitLimit, TestContext.Current.CancellationToken));
            }
            finally
            {
                database.Gate.Set();
            }

            await armor.GetAllArmorNamesCache(ArmorNamesKept);

            Assert.Equal(2, database.Calls);
        }
    }
}
