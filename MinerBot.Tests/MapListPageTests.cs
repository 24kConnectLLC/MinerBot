using System.Text.RegularExpressions;

namespace MinerBot.Tests
{
    // /maps latest shows one map per page, and as many pages as Steam gave maps, which can be fewer than five
    [Collection("Discord")]
    public class MapListPageTests
    {
        static string Button(FakeDiscord discord, int page, string direction) => $"maplist:{discord.UserId},{page},{direction}";

        static List<Steam.Models.PublishedFileDetailsModel> Maps(int count) => Enumerable.Range(1, count).Select(FakeGetData.CreateItem).ToList();

        [Theory]
        [InlineData(5)]
        [InlineData(3)]
        [InlineData(2)]
        public async Task LatestMapsCountsItsPages(int count)
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.LatestItems = Maps(count);

            await discord.SendSlashCommandAsync("maps", "latest");

            var reply = discord.Single("followup");
            Assert.Equal($"__**Latest Maps: (1/{count})**__", reply.Content);
            Assert.Equal(["Map 1"], reply.EmbedTitles);
            Assert.Equal([Button(discord, 0, "previous"), Button(discord, 0, "next")], reply.ButtonIds);
            Assert.Equal([Button(discord, 0, "previous")], reply.DisabledButtonIds);
        }

        [Fact]
        public async Task OneMapHasNoPageToTurnTo()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.LatestItems = Maps(1);

            await discord.SendSlashCommandAsync("maps", "latest");

            var reply = discord.Single("followup");
            Assert.Equal("__**Latest Maps: (1/1)**__", reply.Content);
            Assert.Equal([Button(discord, 0, "previous"), Button(discord, 0, "next")], reply.DisabledButtonIds);
        }

        [Theory]
        [InlineData(5, 3, 5)]
        [InlineData(3, 1, 3)]
        public async Task NextStopsOnTheLastPage(int count, int from, int shownMap)
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.LatestItems = Maps(count);

            await discord.SendButtonAsync(Button(discord, from, "next"));

            var edit = discord.Single("edit original");
            Assert.Equal($"__**Latest Maps: ({shownMap}/{count})**__", edit.Content);
            Assert.Equal([$"Map {shownMap}"], edit.EmbedTitles);
            Assert.Equal([Button(discord, from + 1, "next")], edit.DisabledButtonIds);
        }

        // The list can get shorter after the buttons were made, and messages from before this fix said 5 pages
        [Theory]
        [InlineData(3, 4, "next", 2)]
        [InlineData(3, 9, "previous", 2)]
        [InlineData(1, 0, "next", 0)]
        [InlineData(5, 0, "previous", 0)]
        public async Task PageOutsideTheListShowsTheNearestPage(int count, int from, string direction, int shown)
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.LatestItems = Maps(count);

            await discord.SendButtonAsync(Button(discord, from, direction));

            Assert.Equal(["defer update", "GetLatestWorkshopItems", "edit original"], discord.StepNames);

            var edit = discord.Single("edit original");
            Assert.Equal($"__**Latest Maps: ({shown + 1}/{count})**__", edit.Content);
            Assert.Equal([$"Map {shown + 1}"], edit.EmbedTitles);
            Assert.Equal([Button(discord, shown, "previous"), Button(discord, shown, "next")], edit.ButtonIds);
        }

        // A click can ask Steam whenever the cache is empty
        [Fact]
        public async Task PageButtonsHaveACooldown()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendButtonAsync(Button(discord, 0, "next"));
            await discord.SendButtonAsync(Button(discord, 1, "next"));

            Assert.Equal(["defer update", "GetLatestWorkshopItems", "edit original", "respond"], discord.StepNames);

            var refusal = discord.Steps[^1];
            Assert.StartsWith("You can use these buttons again <t:", refusal.Content);
            Assert.True(refusal.IsEphemeral);
            Assert.True(refusal.AllowsNoMentions);
        }

        // Each person's clicks have their own cooldown
        [Fact]
        public async Task CooldownOfOnePersonDoesNotHoldUpAnother()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendButtonAsync(Button(discord, 0, "next"));
            await discord.SendButtonAsync($"maplist:{discord.OtherUserId},0,next", userId: discord.OtherUserId);

            Assert.Equal(["defer update", "GetLatestWorkshopItems", "edit original", "defer update", "edit original"], discord.StepNames);
        }

        // The cooldown is checked after DoUserCheck, so a click on someone else's buttons doesn't start one
        [Fact]
        public async Task ClickOnSomeoneElsesButtonsStartsNoCooldown()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendButtonAsync(Button(discord, 0, "next"), userId: discord.OtherUserId);
            await discord.SendButtonAsync($"maplist:{discord.OtherUserId},0,next", userId: discord.OtherUserId);

            Assert.Equal(["respond", "defer update", "GetLatestWorkshopItems", "edit original"], discord.StepNames);
            Assert.Equal("Only the person who ran this command can use these buttons.", discord.Steps[0].Content);
        }

        // Two seconds, so turning pages is never held up for long
        [Fact]
        public async Task PageButtonCooldownIsTwoSeconds()
        {
            await using var discord = await FakeDiscord.StartAsync();

            var before = DateTimeOffset.UtcNow;
            await discord.SendButtonAsync(Button(discord, 0, "next"));
            var after = DateTimeOffset.UtcNow;
            await discord.SendButtonAsync(Button(discord, 1, "next"));

            var usableAt = long.Parse(Regex.Match(discord.Steps[^1].Content!, @"<t:(\d+):R>").Groups[1].Value);
            Assert.InRange(usableAt, before.AddSeconds(2).ToUnixTimeSeconds(), after.AddSeconds(2).ToUnixTimeSeconds());
        }
    }
}
