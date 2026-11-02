using DataAccessLibrary.Armor.Models;
using Discord;
using MinerBot_2._0.Jobs;
using MinerBot_2._0.Services;

namespace MinerBot.Tests
{
    public class ArmorEmbedTests
    {
        const int NameLimit = EmbedBuilder.MaxTitleLength;

        internal static ArmorStyle Armor(int id, string name) => new() { ID = id, ArmorName = name };

        // The names of a description, without the lines of the code block
        internal static List<string> NamesIn(string description) => description.Split('\n')[1..^1].ToList();

        static List<ArmorStyle> ArmorWithNamesOfLength(params int[] lengths) =>
            lengths.Select((length, index) => Armor(index + 1, new string('a', length))).ToList();

        static IEnumerable<int> MessageSizes(params int[] nameLengths) =>
            ArmorService.NewArmorListEmbeds(ArmorWithNamesOfLength(nameLengths)).Select(message => message.ArmorCount);

        // A description holds 4,096 characters and the code block takes 7 of them. A name takes its length and a line break,
        // so 15 of the longest names leave room for one more name of 233 characters.
        static int[] NameLengthsWithALastNameOf(int lastName) => [.. Enumerable.Repeat(NameLimit, 15), lastName];

        [Fact]
        public void NamesAreListedInACodeBlockThatStartsWithAnEmptyLine()
        {
            var message = Assert.Single(ArmorService.NewArmorListEmbeds([Armor(1, "Ninja"), Armor(2, "Samurai")]));

            Assert.Equal("```\nNinja\nSamurai\n```", message.Embed.Description);
            Assert.Equal(2, message.ArmorCount);
        }

        [Fact]
        public void NoNewArmorIsNoMessage()
        {
            Assert.Empty(ArmorService.NewArmorListEmbeds([]));
        }

        [Fact]
        public void NamesThatExactlyFillTheDescriptionStayInOneMessage()
        {
            var message = Assert.Single(ArmorService.NewArmorListEmbeds(ArmorWithNamesOfLength(NameLengthsWithALastNameOf(233))));

            Assert.Equal(EmbedBuilder.MaxDescriptionLength, message.Embed.Description.Length);
            Assert.Equal(16, message.ArmorCount);
        }

        [Fact]
        public void OneMoreCharacterStartsANewMessage()
        {
            Assert.Equal([15, 1], MessageSizes(NameLengthsWithALastNameOf(234)));
        }

        [Fact]
        public void EveryMessageIsAFullAnnouncement()
        {
            var messages = ArmorService.NewArmorListEmbeds(ArmorWithNamesOfLength([.. Enumerable.Repeat(NameLimit, 40)]));

            Assert.Equal(3, messages.Count);
            Assert.All(messages, message =>
            {
                Assert.Equal("New Armor Styles:", message.Embed.Title);
                Assert.Equal("https://murderminershub.com/upload-armor-style/", message.Embed.Url);
                Assert.Equal("Upload your own Armor Style", Assert.Single(message.Embed.Fields).Name);
            });
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void NamesOfAnySizeStayInOrderAndWithinTheLimit(int seed)
        {
            var random = new Random(seed);
            var armors = Enumerable.Range(1, 1000).Select(id => Armor(id, $"{id} ".PadRight(random.Next(1, NameLimit + 1), 'a'))).ToList();

            var messages = ArmorService.NewArmorListEmbeds(armors);

            Assert.True(messages.Count > 1);
            Assert.All(messages, message => Assert.InRange(message.Embed.Description.Length, 1, EmbedBuilder.MaxDescriptionLength));
            Assert.All(messages, message => Assert.Equal(message.ArmorCount, NamesIn(message.Embed.Description).Count));
            Assert.Equal(armors.Select(armor => armor.ArmorName), messages.SelectMany(message => NamesIn(message.Embed.Description)));
        }

        [Theory]
        [InlineData(NameLimit + 1)]
        [InlineData(EmbedBuilder.MaxDescriptionLength)]
        [InlineData(20000)]
        public void LongNameIsShortened(int length)
        {
            var message = Assert.Single(ArmorService.NewArmorListEmbeds(ArmorWithNamesOfLength(length)));

            Assert.Equal([new string('a', NameLimit - 3) + "..."], NamesIn(message.Embed.Description));
        }

        [Fact]
        public void NameAtTheLimitStaysTheSame()
        {
            var message = Assert.Single(ArmorService.NewArmorListEmbeds(ArmorWithNamesOfLength(NameLimit)));

            Assert.Equal([new string('a', NameLimit)], NamesIn(message.Embed.Description));
        }

        [Fact]
        public void ArmorWithNoNameKeepsItsPlace()
        {
            var message = Assert.Single(ArmorService.NewArmorListEmbeds([Armor(1, "First"), Armor(2, null!), Armor(3, "Last")]));

            Assert.Equal(["First", "", "Last"], NamesIn(message.Embed.Description));
            Assert.Equal(3, message.ArmorCount);
        }
    }

    // Each run is a new job object on the real Discord.Net pipeline, the same as JobHandler runs it
    [Collection("Discord")]
    public class ArmorNotificationTests
    {
        const string SendStep = "send message";
        const string SendFailure = "Notification to Discord Channel";
        const int NewId = 100;
        const int BatchSize = 200;

        // Names of 25 characters, so this passes the limit of one description
        static List<ArmorStyle> Batch() =>
            Enumerable.Range(NewId, BatchSize).Select(id => ArmorEmbedTests.Armor(id, $"Long armor style name {id}")).ToList();

        static async Task<FakeDiscord> StartWithBatchAsync()
        {
            var discord = await FakeDiscord.StartAsync();
            await discord.RunNotificationJobAsync();
            discord.Armor.AllArmor.AddRange(Batch());
            return discord;
        }

        static List<Step> Sent(FakeDiscord discord) => discord.Steps.Where(step => step.Name == SendStep).ToList();

        static List<string> NamesIn(Step step) => ArmorEmbedTests.NamesIn(step.EmbedDescriptions.Single());

        static IEnumerable<string> BatchNames() => Batch().Select(armor => armor.ArmorName);

        [Fact]
        public async Task ArmorThatPassesTheEmbedLimitIsAnnouncedInSeveralMessages()
        {
            await using var discord = await StartWithBatchAsync();

            await discord.RunNotificationJobAsync();

            var messages = Sent(discord);

            Assert.Equal(2, messages.Count);
            Assert.All(messages, message => Assert.InRange(message.EmbedDescriptions.Single().Length, 1, EmbedBuilder.MaxDescriptionLength));
            Assert.All(messages, message => Assert.InRange(message.EmbedsLength, 1, EmbedBuilder.MaxEmbedLength));
            Assert.Equal(BatchNames(), messages.SelectMany(NamesIn));
            Assert.DoesNotContain(SendFailure, discord.ConsoleText);

            await discord.RunNotificationJobAsync();

            Assert.Equal(messages.Count, Sent(discord).Count);
        }

        [Fact]
        public async Task ArmorOfAMessageThatFailedIsSentWithoutRepeatingTheOthers()
        {
            await using var discord = await StartWithBatchAsync();
            discord.FailStep = SendStep;
            discord.FailStepAfter = 1;

            await discord.RunNotificationJobAsync();

            var firstRun = Sent(discord);
            Assert.Equal(2, firstRun.Count);

            discord.FailStep = null;
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            var delivered = firstRun.Take(1).Concat(Sent(discord).Skip(2));

            Assert.Equal(BatchNames(), delivered.SelectMany(NamesIn));
        }

        [Fact]
        public async Task ArmorOfTheGuildThatGotTheMostIsRecorded()
        {
            await using var discord = await StartWithBatchAsync();
            discord.Guilds.AllGuilds.Add(FakeGuildData.CreateGuild(FakeDiscord.ChannelId));
            discord.FailStep = SendStep;
            discord.FailStepAfter = 3;

            // The first guild gets both messages, and the second one only the first
            await discord.RunNotificationJobAsync();

            var firstRun = discord.Steps.Count;
            discord.FailStep = null;
            await discord.RunNotificationJobAsync();

            Assert.DoesNotContain(discord.Steps.Skip(firstRun), step => step.Name == SendStep);
        }

        [Fact]
        public async Task OnlyArmorThatNeverGotThroughIsGivenUpOn()
        {
            await using var discord = await StartWithBatchAsync();
            discord.FailStep = SendStep;
            discord.FailStepAfter = 1;

            for (int run = 0; run < NotificationState.MaxFailedRuns; run++)
                await discord.RunNotificationJobAsync();

            var sentNames = NamesIn(Sent(discord)[0]).Count;

            Assert.InRange(sentNames, 1, BatchSize - 1);
            Assert.Contains($"Gave up on {BatchSize - sentNames} new armor styles", discord.ConsoleText);
        }

        [Fact]
        public async Task ArmorWithANameThatFitsNoMessageIsAnnouncedWithTheRest()
        {
            await using var discord = await FakeDiscord.StartAsync();
            await discord.RunNotificationJobAsync();
            discord.Armor.AllArmor.Add(ArmorEmbedTests.Armor(NewId, new string('a', 2 * EmbedBuilder.MaxDescriptionLength)));
            discord.Armor.AllArmor.Add(ArmorEmbedTests.Armor(NewId + 1, "After the long name"));

            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            var message = Assert.Single(Sent(discord));

            Assert.Equal(2, NamesIn(message).Count);
            Assert.EndsWith("...", NamesIn(message)[0]);
            Assert.Equal("After the long name", NamesIn(message)[1]);
        }
    }
}
