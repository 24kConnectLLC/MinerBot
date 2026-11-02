using Microsoft.Extensions.Caching.Memory;
using MinerBot_2._0.Services;

namespace MinerBot.Tests
{
    public class WorkshopCacheTests
    {
        const string QueryStep = nameof(FakeGetData.GetLatestWorkshopItems);

        readonly List<string> steps = new();
        readonly FakeClock clock = new();
        readonly FakeGetData steam;
        readonly SteamAPIService service;

        int Queries => steps.Count(step => step == QueryStep);

        public WorkshopCacheTests()
        {
            steam = new FakeGetData(steps.Add);
            service = new SteamAPIService(steam, new MemoryCache(new MemoryCacheOptions { Clock = clock }));
        }

        [Fact]
        public async Task RepeatedRequestsShareOneQuery()
        {
            var first = await service.GetLatestWorkshopItems();
            var second = await service.GetLatestWorkshopItems();

            Assert.Equal(1, Queries);
            Assert.Equal(5, first.Count);
            Assert.Equal(first, second);
        }

        [Fact]
        public async Task ItemsStayCachedForThirtySecondsAndExpireWithinAMinute()
        {
            await service.GetLatestWorkshopItems();

            clock.UtcNow += TimeSpan.FromSeconds(30);
            await service.GetLatestWorkshopItems();
            Assert.Equal(1, Queries);

            clock.UtcNow += TimeSpan.FromSeconds(31);
            await service.GetLatestWorkshopItems();
            Assert.Equal(2, Queries);
        }

        [Fact]
        public async Task EachCountHasItsOwnEntry()
        {
            var three = await service.GetLatestWorkshopItems(3);
            var five = await service.GetLatestWorkshopItems(5);

            Assert.Equal(2, Queries);
            Assert.Equal(3, three.Count);
            Assert.Equal(5, five.Count);
        }

        [Fact]
        public async Task EmptyResultIsNotCached()
        {
            // GetData.GetLatestWorkshopItems catches Steam errors and returns an empty list
            steam.LatestItems = new();
            Assert.Empty(await service.GetLatestWorkshopItems());

            steam.LatestItems = [FakeGetData.CreateItem(1)];
            Assert.Single(await service.GetLatestWorkshopItems());

            Assert.Equal(2, Queries);
        }

        [Fact]
        public async Task QueryThatThrowsIsNotCached()
        {
            steam.Failure = new InvalidOperationException("Steam failed");
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetLatestWorkshopItems());

            steam.Failure = null;
            Assert.Equal(5, (await service.GetLatestWorkshopItems()).Count);

            Assert.Equal(2, Queries);
        }
    }

    [Collection("Discord")]
    public class WorkshopCacheFlowTests
    {
        const string QueryStep = nameof(FakeGetData.GetLatestWorkshopItems);

        [Fact]
        public async Task PageButtonsReuseTheQueryOfLatestMaps()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("maps", "latest");
            await discord.SendButtonAsync($"maplist:{discord.UserId},0,next");
            await discord.SendButtonAsync($"maplist:{discord.UserId},1,next");

            Assert.Equal(["defer", QueryStep, "followup", "defer update", "edit original", "defer update", "edit original"], discord.StepNames);
            Assert.Equal(["Map 3"], discord.Steps[^1].EmbedTitles);
        }

        [Fact]
        public async Task NewMapsCommandAsksForItsOwnCount()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendMessageAsync("+newmaps");
            await discord.SendSlashCommandAsync("maps", "latest");

            Assert.Equal(2, discord.StepNames.Count(step => step == QueryStep));
            Assert.Equal(["Map 1", "Map 2", "Map 3"], discord.Single("send message").EmbedTitles);
        }
    }
}
