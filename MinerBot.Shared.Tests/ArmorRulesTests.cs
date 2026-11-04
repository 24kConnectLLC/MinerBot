using MinerBot.Shared.Armor;

namespace MinerBot.Shared.Tests
{
    public class ArmorRulesTests
    {
        // A submission as the website saves a good one. Each test changes one thing.
        sealed class Submission : IArmorSubmission
        {
            public long ID { get; set; } = 3;
            public string? ArmorName { get; set; } = "Test Armor 3";
            public string? ArmorDescription { get; set; } = "A dark set for night raids, with a red visor.";
            public string? ArmorScreenshot { get; set; } = "https://murderminershub.com/wp-content/uploads/ArmorPreviews/Test-Armor-3.png";
            public string Author { get; set; } = "Test Author";
            public int? Helmet { get; set; } = 1;
            public int? Visor { get; set; } = 2;
            public int? Torso { get; set; } = 3;
            public int? Shoulders { get; set; } = 4;
            public int? Biceps { get; set; } = 5;
            public int? Forearms { get; set; } = 6;
            public int? Hands { get; set; } = 7;
            public int? Pelvis { get; set; } = 8;
            public int? Thighs { get; set; } = 9;
            public int? Legs { get; set; } = 10;
            public int? Boots { get; set; } = 11;
            public int? Shields { get; set; } = 12;
            public int? Other { get; set; } = 13;
        }

        // An emoji outside the Basic Multilingual Plane, which Discord counts as two characters and MariaDB as one
        const string Emoji = "\U0001F600";

        static ArmorCheck Check(Submission armor, bool nameTaken = false)
        {
            var check = ArmorRules.Check(armor, nameTaken);

            // Every problem has to say what's wrong, whichever test found it
            Assert.All(check.Blocking.Concat(check.Warnings), problem => Assert.False(string.IsNullOrWhiteSpace(problem.Message)));
            return check;
        }

        static IEnumerable<ArmorProblemKind> Kinds(IEnumerable<ArmorProblem> problems) => problems.Select(problem => problem.Kind);

        static ArmorProblem Blocks(Submission armor, ArmorProblemKind kind, bool nameTaken = false)
        {
            var check = Check(armor, nameTaken);

            Assert.False(check.CanApprove);
            return Assert.Single(check.Blocking, problem => problem.Kind == kind);
        }

        static ArmorProblem Warns(Submission armor, ArmorProblemKind kind)
        {
            var check = Check(armor);

            Assert.True(check.CanApprove);
            return Assert.Single(check.Warnings, problem => problem.Kind == kind);
        }

        // Catches a rule that flags too much
        [Fact]
        public void CleanSubmissionHasNoProblems()
        {
            var check = Check(new Submission());

            Assert.Empty(check.Blocking);
            Assert.Empty(check.Warnings);
            Assert.True(check.CanApprove);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\u200B")]
        [InlineData(" \u200B\u3164 ")]
        [InlineData("\t")]
        [InlineData("\u034F")]
        [InlineData("\uFE0F\uFE0F")]
        [InlineData(" \uFE0E")]
        [InlineData("\U000E0100\U000E0101")]
        [InlineData("\u180B\u17B4")]
        public void NameWithNothingToShowIsEmpty(string? name)
        {
            Assert.Equal(ArmorField.Name, Blocks(new Submission { ArmorName = name }, ArmorProblemKind.NameEmpty).Field);
        }

        // An emoji styled as one still shows, so it isn't empty, and its style selector is no hidden character
        [Fact]
        public void NameOfAStyledEmojiIsNotEmpty()
        {
            Assert.True(Check(new Submission { ArmorName = "\u2764\uFE0F" }).CanApprove);
            Assert.Empty(Check(new Submission { ArmorName = "\u2620\uFE0F Pirate" }).Warnings);
        }

        // Variation selectors show nothing, wherever they are
        [Fact]
        public void AuthorWithAVariationSelectorWarns()
        {
            var problem = Warns(new Submission { Author = "Test\U000E0100Author" }, ArmorProblemKind.HiddenCharacters);

            Assert.Equal(ArmorField.Author, problem.Field);
            Assert.Contains("variation selector (U+E0100)", problem.Message);
        }

        // It isn't empty, but the hidden character still needs a look
        [Fact]
        public void NameWithLettersAndAHiddenCharacterIsNotEmpty()
        {
            var check = Check(new Submission { ArmorName = "Ninja\u200B" });

            Assert.True(check.CanApprove);
            Assert.Equal([ArmorProblemKind.HiddenCharacters], Kinds(check.Warnings));
        }

        [Theory]
        [InlineData("Night`Raid")]
        [InlineData("```")]
        public void NameWithABacktickIsBlocked(string name)
        {
            Blocks(new Submission { ArmorName = name }, ArmorProblemKind.NameHasBacktick);
        }

        [Theory]
        [InlineData("Night\nRaid")]
        [InlineData("Night\rRaid")]
        [InlineData("Night\r\nRaid")]
        [InlineData("Night\u0085Raid")]
        [InlineData("Night\u2028Raid")]
        [InlineData("Night\u2029Raid")]
        [InlineData("Night\vRaid")]
        [InlineData("Night\fRaid")]
        public void NameWithALineBreakIsBlocked(string name)
        {
            Blocks(new Submission { ArmorName = name }, ArmorProblemKind.NameHasLineBreak);
        }

        [Fact]
        public void NameThatIsTakenIsBlocked()
        {
            var problem = Blocks(new Submission(), ArmorProblemKind.NameTaken, nameTaken: true);

            Assert.Equal(ArmorField.Name, problem.Field);
            Assert.Single(Check(new Submission(), nameTaken: true).Blocking);
        }

        [Fact]
        public void NameUpToTheTablesLimitPasses()
        {
            Assert.True(Check(new Submission { ArmorName = new string('a', ArmorRules.NameLimit) }).CanApprove);
        }

        [Fact]
        public void NameOverTheTablesLimitIsBlocked()
        {
            var problem = Blocks(new Submission { ArmorName = new string('a', ArmorRules.NameLimit + 1) }, ArmorProblemKind.NameTooLong);

            Assert.Contains("61", problem.Message);
        }

        // 60 emoji fit the table, which counts code points, but take 120 of Discord's characters
        [Fact]
        public void NameThatFitsTheTableButNotDiscordIsBlocked()
        {
            var check = Check(new Submission { ArmorName = string.Concat(Enumerable.Repeat(Emoji, ArmorRules.NameLimit)) });

            Assert.Equal([ArmorProblemKind.NameTooLongForDiscord], Kinds(check.Blocking));
        }

        [Fact]
        public void NameOfExactlyDiscordsLimitPasses()
        {
            // 50 emoji are 100 UTF-16 characters, and one more letter makes 101
            Assert.True(Check(new Submission { ArmorName = string.Concat(Enumerable.Repeat(Emoji, 50)) }).CanApprove);
            Assert.Equal([ArmorProblemKind.NameTooLongForDiscord], Kinds(Check(new Submission { ArmorName = string.Concat(Enumerable.Repeat(Emoji, 50)) + "a" }).Blocking));
            Assert.False(Check(new Submission { ArmorName = string.Concat(Enumerable.Repeat(Emoji, 51)) }).CanApprove);
        }

        [Fact]
        public void AuthorOverTheTablesLimitIsBlocked()
        {
            Assert.True(Check(new Submission { Author = new string('a', ArmorRules.AuthorLimit) }).CanApprove);
            Assert.Equal(ArmorField.Author, Blocks(new Submission { Author = new string('a', ArmorRules.AuthorLimit + 1) }, ArmorProblemKind.AuthorTooLong).Field);
        }

        [Fact]
        public void DescriptionOverTheTablesLimitIsBlocked()
        {
            Assert.True(Check(new Submission { ArmorDescription = new string('a', ArmorRules.DescriptionLimit) }).CanApprove);
            Blocks(new Submission { ArmorDescription = new string('a', ArmorRules.DescriptionLimit + 1) }, ArmorProblemKind.DescriptionTooLong);
        }

        // MariaDB counts each emoji once, so 256 of them still fit
        [Fact]
        public void DescriptionLimitCountsCodePoints()
        {
            Assert.True(Check(new Submission { ArmorDescription = string.Concat(Enumerable.Repeat(Emoji, ArmorRules.DescriptionLimit)) }).CanApprove);
        }

        // Only a description that can't be saved anyway gets this long, but the card's own limit is checked too
        [Fact]
        public void LookupCardOverDiscordsLimitIsBlocked()
        {
            var check = Check(new Submission { ArmorDescription = new string('a', 4090) });

            Assert.Contains(ArmorProblemKind.LookupTooLongForDiscord, Kinds(check.Blocking));
            Assert.DoesNotContain(ArmorProblemKind.LookupTooLongForDiscord,
                Kinds(Check(new Submission { ArmorDescription = new string('a', 256), Author = new string('a', 60) }).Blocking));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(255)]
        [InlineData(null)]
        public void ColorInsideAByteIsFine(int? color)
        {
            Assert.True(Check(new Submission { Helmet = color, Other = color }).CanApprove);
        }

        [Theory]
        [InlineData(256)]
        [InlineData(300)]
        [InlineData(-1)]
        public void ColorOutsideAByteIsBlocked(int color)
        {
            var problem = Blocks(new Submission { Visor = color }, ArmorProblemKind.ColorOutOfRange);

            Assert.Equal(ArmorField.Colors, problem.Field);
            Assert.Contains($"visor {color}", problem.Message);
        }

        // Each of the 13 parts is checked
        [Theory]
        [InlineData("Helmet")]
        [InlineData("Visor")]
        [InlineData("Torso")]
        [InlineData("Shoulders")]
        [InlineData("Biceps")]
        [InlineData("Forearms")]
        [InlineData("Hands")]
        [InlineData("Pelvis")]
        [InlineData("Thighs")]
        [InlineData("Legs")]
        [InlineData("Boots")]
        [InlineData("Shields")]
        [InlineData("Other")]
        public void EveryColorIsChecked(string part)
        {
            var armor = new Submission();
            typeof(Submission).GetProperty(part)!.SetValue(armor, 300);

            var problem = Blocks(armor, ArmorProblemKind.ColorOutOfRange);

            Assert.Contains($"{part.ToLowerInvariant()} 300", problem.Message);
        }

        [Theory]
        [InlineData("https://murderminershub.com/wp-content/uploads/ArmorPreviews/x.png")]
        [InlineData("https://www.murderminershub.com/wp-content/uploads/ArmorPreviews/x.png")]
        [InlineData("https://MURDERMINERSHUB.COM/wp-content/uploads/ArmorPreviews/x.png")]
        [InlineData("HTTPS://murderminershub.com/x.png")]
        [InlineData("https://murderminershub.com:443/x.png")]
        [InlineData("https://murderminershub.com/x.png?size=large")]
        [InlineData("https://murderminershub.com/wp-content/uploads/Café-Knight.png")]
        public void ScreenshotOnTheWebsiteIsAllowed(string address)
        {
            Assert.True(ArmorRules.IsAllowedScreenshot(address));
            Assert.Empty(Check(new Submission { ArmorScreenshot = address }).Warnings);
        }

        [Theory]
        [InlineData("http://murderminershub.com/x.png")]
        [InlineData("https://example.com/x.png")]
        [InlineData("https://murderminershub.com.example.com/x.png")]
        [InlineData("https://murderminershub.com@example.com/x.png")]
        [InlineData("https://user@murderminershub.com/x.png")]
        [InlineData("https://murderminershub.com:8443/x.png")]
        [InlineData("https://murderminershub.com./x.png")]
        [InlineData("javascript:alert(1)")]
        [InlineData("data:image/png;base64,AAAA")]
        [InlineData("/wp-content/uploads/x.png")]
        [InlineData("ftp://murderminershub.com/x.png")]
        [InlineData(" https://murderminershub.com/x.png")]
        [InlineData("https://murderminershub.com/x y.png")]
        [InlineData("https://murderminershub.com/x\u200B.png")]
        // Hosts outside ASCII, some of which .NET's IDN conversion throws on, and look-alikes it would turn into the real host
        [InlineData("https://\u0301x.com/x.png")]
        [InlineData("https://murderminershub\uFE52com/x.png")]
        [InlineData("https://murderminershub.com\u2024/x.png")]
        [InlineData("https://evil.example\uFF20murderminershub.com/x.png")]
        [InlineData("https://murderminershub.com\uFF1A8443/x.png")]
        [InlineData("https://\uFF4Durderminershub.com/x.png")]
        [InlineData("https://murderminer\u017Fhub.com/x.png")]
        public void ScreenshotAnywhereElseWarns(string address)
        {
            Assert.False(ArmorRules.IsAllowedScreenshot(address));
            Assert.Equal(ArmorField.Screenshot, Warns(new Submission { ArmorScreenshot = address }, ArmorProblemKind.ScreenshotNotAllowed).Field);
        }

        // The website's column has no limit, so an address longer than any upload's is refused before it's read through
        [Fact]
        public void ScreenshotOverTheLimitWarns()
        {
            const string start = "https://murderminershub.com/";
            var longest = start + new string('a', ArmorRules.ScreenshotLimit - start.Length);

            Assert.True(ArmorRules.IsAllowedScreenshot(longest));
            Assert.False(ArmorRules.IsAllowedScreenshot(longest + "a"));

            var problem = Warns(new Submission { ArmorScreenshot = longest + new string('​', 1000000) }, ArmorProblemKind.ScreenshotNotAllowed);
            Assert.Contains($"over {ArmorRules.ScreenshotLimit} characters", problem.Message);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void NoScreenshotWarns(string? address)
        {
            Assert.False(ArmorRules.IsAllowedScreenshot(address));
            Warns(new Submission { ArmorScreenshot = address }, ArmorProblemKind.NoScreenshot);
        }

        [Fact]
        public void HiddenCharactersAreNamedForEachField()
        {
            var check = Check(new Submission { ArmorName = "Night\u200BRaid", ArmorDescription = "Soft\u00ADhyphen", Author = "Test\u2060Author" });

            Assert.Equal([ArmorField.Name, ArmorField.Description, ArmorField.Author],
                check.Warnings.Where(problem => problem.Kind == ArmorProblemKind.HiddenCharacters).Select(problem => problem.Field));
            Assert.Contains("zero-width space (U+200B)", check.Warnings[0].Message);
        }

        [Fact]
        public void DirectionCharactersHaveTheirOwnWarning()
        {
            var problem = Warns(new Submission { ArmorDescription = "evil\u202Etxt.exe" }, ArmorProblemKind.DirectionCharacters);

            Assert.Equal(ArmorField.Description, problem.Field);
            Assert.Contains("right-to-left override (U+202E)", problem.Message);
            Assert.DoesNotContain(ArmorProblemKind.HiddenCharacters, Kinds(Check(new Submission { ArmorDescription = "evil\u202Etxt.exe" }).Warnings));
        }

        [Theory]
        [InlineData("Ping @everyone now")]
        [InlineData("Made for <@1000000000000000001>")]
        [InlineData("See <#1000000000000000002>")]
        public void MentionSyntaxWarns(string description)
        {
            Assert.Equal(ArmorField.Description, Warns(new Submission { ArmorDescription = description }, ArmorProblemKind.MentionSyntax).Field);
        }

        [Theory]
        [InlineData("Get it at https://example.com/free")]
        [InlineData("Join discord.gg/abc123")]
        [InlineData("[My armor](https://example.com)")]
        public void LinkSyntaxWarns(string author)
        {
            Assert.Equal(ArmorField.Author, Warns(new Submission { Author = author }, ArmorProblemKind.LinkSyntax).Field);
        }

        // /armor lookup shows the description and the author in a code block, which a backtick can end
        [Fact]
        public void BacktickInTheDescriptionOrAuthorWarns()
        {
            Warns(new Submission { ArmorDescription = "Ends here```[click](https://example.com)" }, ArmorProblemKind.Backtick);
            Warns(new Submission { Author = "Test`Author" }, ArmorProblemKind.Backtick);
        }

        [Fact]
        public void WarningsNeverBlock()
        {
            var check = Check(new Submission
            {
                ArmorName = "Night\u200BRaid\u202E @here",
                ArmorDescription = "Visit https://example.com, ping @everyone and <@&1000000000000000003> ``` ",
                Author = "www.example.com\u2066",
                ArmorScreenshot = "https://example.com/x.png"
            });

            Assert.Empty(check.Blocking);
            Assert.True(check.CanApprove);
            Assert.Equal(
                [ArmorProblemKind.ScreenshotNotAllowed, ArmorProblemKind.HiddenCharacters, ArmorProblemKind.DirectionCharacters,
                 ArmorProblemKind.MentionSyntax, ArmorProblemKind.MentionSyntax, ArmorProblemKind.LinkSyntax, ArmorProblemKind.Backtick,
                 ArmorProblemKind.DirectionCharacters, ArmorProblemKind.LinkSyntax],
                Kinds(check.Warnings));
        }

        [Fact]
        public void EveryBlockingProblemAtOnceIsReported()
        {
            var check = Check(new Submission
            {
                ID = 3000000000,
                ArmorName = string.Concat(Enumerable.Repeat("`", 30)) + "\n" + string.Concat(Enumerable.Repeat(Emoji, 40)),
                Author = new string('a', 61),
                ArmorDescription = new string('a', 4100),
                Helmet = 256
            }, nameTaken: true);

            Assert.Equal(
                [ArmorProblemKind.IdTooBigForTheBot, ArmorProblemKind.NameTaken, ArmorProblemKind.NameHasBacktick, ArmorProblemKind.NameHasLineBreak,
                 ArmorProblemKind.NameTooLongForDiscord, ArmorProblemKind.NameTooLong, ArmorProblemKind.AuthorTooLong, ArmorProblemKind.DescriptionTooLong,
                 ArmorProblemKind.LookupTooLongForDiscord, ArmorProblemKind.ColorOutOfRange],
                Kinds(check.Blocking));
        }

        // The bot reads an armor's ID as an int, so one past that would stop its armor posts in every server once approved
        [Fact]
        public void IdTheBotCantReadBlocks()
        {
            Assert.True(Check(new Submission { ID = int.MaxValue }).CanApprove);

            var check = Check(new Submission { ID = (long)int.MaxValue + 1 });

            Assert.Equal([ArmorProblemKind.IdTooBigForTheBot], Kinds(check.Blocking));
            Assert.Equal(ArmorField.Id, check.Blocking[0].Field);
        }
    }
}
