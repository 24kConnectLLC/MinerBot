using System.Text;
using Discord;
using Microsoft.Extensions.Caching.Memory;
using MinerBot_2._0.Services;
using Steam.Models;

namespace MinerBot.Tests
{
    public class MapEmbedTests
    {
        const int DescriptionLimit = 650;

        readonly SteamAPIService service = new(new FakeGetData(_ => { }), new MemoryCache(new MemoryCacheOptions()));

        Task<Embed> FormatWithDescription(string? description)
        {
            var map = FakeGetData.CreateItem(1);
            map.Description = description!;

            return service.FormatMapEmbed(map);
        }

        static Embed EmbedOfLength(int length) => new EmbedBuilder().WithDescription(new string('a', length)).Build();

        static IEnumerable<int> GroupSizes(params int[] lengths) =>
            SteamAPIService.GroupEmbedsByLength(lengths.Select(EmbedOfLength)).Select(group => group.Length);

        [Theory]
        [InlineData(DescriptionLimit + 1)]
        [InlineData(4097)]
        [InlineData(8000)]
        public async Task LongDescriptionIsShortened(int length)
        {
            var embed = await FormatWithDescription(new string('a', length));

            Assert.Equal(new string('a', DescriptionLimit - 3) + "...", embed.Description);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(10)]
        [InlineData(DescriptionLimit)]
        public async Task ShortDescriptionStaysTheSame(int length)
        {
            var description = new string('a', length);

            var embed = await FormatWithDescription(description);

            Assert.Equal(description, embed.Description);
        }

        [Fact]
        public async Task MapWithNoDescriptionStillWorks()
        {
            var embed = await FormatWithDescription(null);

            Assert.Null(embed.Description);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public async Task ShorteningNeverCutsACharacterInHalf(int lettersBefore)
        {
            const string smiley = "\U0001F600";
            var description = new string('a', lettersBefore) + string.Concat(Enumerable.Repeat(smiley, DescriptionLimit));
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

            var embed = await FormatWithDescription(description);

            Assert.InRange(embed.Description.Length, DescriptionLimit - 1, DescriptionLimit);
            Assert.EndsWith(smiley + "...", embed.Description);
            strictUtf8.GetByteCount(embed.Description);
        }

        [Fact]
        public void EmbedsThatFitStayInOneGroup()
        {
            Assert.Equal([3], GroupSizes(2000, 2000, 2000));
        }

        [Fact]
        public void EmbedThatPassesTheLimitStartsANewGroup()
        {
            Assert.Equal([2, 1], GroupSizes(2000, 2000, 2001));
        }

        [Fact]
        public void EveryGroupStaysWithinTheLimit()
        {
            Assert.Equal([2, 2, 1], GroupSizes(4000, 1500, 1500, 3000, 3000));
        }

        [Fact]
        public void GroupsKeepTheOrderOfTheEmbeds()
        {
            var embeds = new[] { 4000, 3000, 2000 }.Select(EmbedOfLength).ToList();

            Assert.Equal(embeds, SteamAPIService.GroupEmbedsByLength(embeds).SelectMany(group => group));
        }
    }

    [Collection("Discord")]
    public class MapEmbedFlowTests
    {
        const string MapUrl = "https://steamcommunity.com/sharedfiles/filedetails/?id=1234567890";

        // The description is at its longest after shortening, and the tags take up most of what a field can hold
        internal static PublishedFileDetailsModel LongMap(int id)
        {
            var map = FakeGetData.CreateItem(id);
            map.Description = new string('a', 5000);
            map.Tags = Enumerable.Range(1, 120).Select(number => $"Tag {number}").ToList();

            return map;
        }

        static string ShortDescription(Step step) => Assert.Single(step.EmbedDescriptions);

        [Fact]
        public async Task MapLookupShowsAMapWithALongDescription()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.WorkshopItem = LongMap(1);

            await discord.SendSlashCommandAsync("maps", "lookup", $$"""{"name":"map-url","type":3,"value":"{{MapUrl}}"}""");

            Assert.EndsWith("...", ShortDescription(discord.Single("followup")));
        }

        [Fact]
        public async Task LatestMapsShowsAMapWithALongDescription()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.LatestItems[0] = LongMap(1);

            await discord.SendSlashCommandAsync("maps", "latest");

            Assert.EndsWith("...", ShortDescription(discord.Single("followup")));
        }
    }
}
