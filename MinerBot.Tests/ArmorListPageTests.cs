using MinerBot_2._0;
using MinerBot_2._0.Services;

namespace MinerBot.Tests
{
    // A page of /armor list shows 20 names, and its buttons carry the page that's shown.
    // The names are single words, because Discord hides one that starts a code block.
    [Collection("Discord")]
    public class ArmorListPageTests
    {
        static string Option(string name, string value) => $$"""{"name":"{{name}}","type":3,"value":"{{value}}"}""";

        static IEnumerable<string> Names(int first, int last) => Enumerable.Range(first, last - first + 1).Select(number => $"Armor{number}");

        static string Button(FakeDiscord discord, int page, string direction) => $"armorlist:{discord.UserId},{page},{direction},Bug";

        static List<string> NamesIn(Step message)
        {
            var description = Assert.Single(message.EmbedDescriptions);
            Assert.StartsWith("```\n", description);
            return ArmorEmbedTests.NamesIn(description);
        }

        [Theory]
        [InlineData(0, 1, 20)]
        [InlineData(1, 21, 40)]
        [InlineData(2, 41, 45)]
        public async Task PageHoldsTheTwentyNamesAfterThePageBefore(int page, int first, int last)
        {
            var service = new ArmorService(new FakeArmorData(_ => { }) { ArmorNames = Names(1, 45) });

            Assert.Equal(Names(first, last), await service.GetArmorListNames(HelmentType.Bug, page));
        }

        // Next is followed until it's disabled, the way someone reads the whole list
        [Theory]
        [InlineData(21)]
        [InlineData(40)]
        [InlineData(45)]
        [InlineData(60)]
        public async Task NextShowsEveryNameOnceAndStopsOnTheLastPage(int count)
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Armor.ArmorNames = Names(1, count);

            await discord.SendSlashCommandAsync("armor", "list", Option("helmet-type", "Bug"));

            var message = discord.Single("followup");
            var pages = new List<List<string>>();

            for (int page = 0; ; page++)
            {
                Assert.Equal([Button(discord, page, "previous"), Button(discord, page, "next")], message.ButtonIds);
                Assert.Equal(page == 0, message.DisabledButtonIds.Contains(Button(discord, page, "previous")));
                pages.Add(NamesIn(message));

                if (message.DisabledButtonIds.Contains(Button(discord, page, "next")) || pages.Count > count)
                    break;

                await discord.SendButtonAsync(Button(discord, page, "next"));
                message = discord.Steps.Last(step => step.Name == "edit original");
            }

            Assert.Equal((count + 19) / 20, pages.Count);
            Assert.All(pages, names => Assert.InRange(names.Count, 1, 20));
            Assert.Equal(Names(1, count), pages.SelectMany(names => names));
        }

        [Theory]
        [InlineData(2, 1, 21, 40)]
        [InlineData(1, 0, 1, 20)]
        public async Task PreviousShowsTheTwentyNamesBefore(int from, int shown, int first, int last)
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Armor.ArmorNames = Names(1, 45);

            await discord.SendButtonAsync(Button(discord, from, "previous"));

            var edit = discord.Single("edit original");
            Assert.Equal(Names(first, last), NamesIn(edit));
            Assert.Equal([Button(discord, shown, "previous"), Button(discord, shown, "next")], edit.ButtonIds);
            Assert.Equal(shown == 0 ? [Button(discord, 0, "previous")] : [], edit.DisabledButtonIds);
        }

        // Armor can be removed after a message is sent, and messages from before this fix let Next go past the end
        [Theory]
        [InlineData(2, "next", 2, 41, 45)]
        [InlineData(7, "previous", 2, 41, 45)]
        [InlineData(0, "previous", 0, 1, 20)]
        public async Task PageOutsideTheListShowsTheNearestPage(int from, string direction, int shown, int first, int last)
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Armor.ArmorNames = Names(1, 45);

            await discord.SendButtonAsync(Button(discord, from, direction));

            var edit = discord.Single("edit original");
            Assert.Equal(Names(first, last), NamesIn(edit));
            Assert.Equal([Button(discord, shown, "previous"), Button(discord, shown, "next")], edit.ButtonIds);
            Assert.Equal(shown == 0 ? [Button(discord, 0, "previous")] : [Button(discord, 2, "next")], edit.DisabledButtonIds);
        }

        [Fact]
        public async Task HelmetWithNoArmorSaysSo()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Armor.ArmorNames = [];

            await discord.SendSlashCommandAsync("armor", "list", Option("helmet-type", "Bug"));

            var reply = discord.Single("followup");
            Assert.Equal("No armor styles yet.", Assert.Single(reply.EmbedDescriptions));
            Assert.Empty(reply.ButtonIds);
        }

        [Fact]
        public async Task PageOfAListThatIsNowEmptySaysSo()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Armor.ArmorNames = [];

            await discord.SendButtonAsync(Button(discord, 0, "next"));

            var edit = discord.Single("edit original");
            Assert.Equal("No armor styles yet.", Assert.Single(edit.EmbedDescriptions));
            Assert.Equal([Button(discord, 0, "previous"), Button(discord, 0, "next")], edit.DisabledButtonIds);
        }

        [Fact]
        public async Task OneWordNameAtTheTopOfTheListIsShown()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Armor.ArmorNames = ["Ninja", "Samurai Warrior"];

            await discord.SendSlashCommandAsync("armor", "list");

            Assert.Equal("```\nNinja\nSamurai Warrior\n```", Assert.Single(discord.Single("followup").EmbedDescriptions));
        }
    }
}
