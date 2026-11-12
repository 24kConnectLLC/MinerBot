using System.Text.Encodings.Web;
using DataAccessLibrary.Armor.Models;
using Microsoft.AspNetCore.Html;
using MinerBot.Dashboard.Armor;
using MinerBot.Shared.Armor;

namespace MinerBot.Dashboard.Tests
{
    // The review page's previews are copies of the bot's messages, which no test can compare with the bot itself. So the
    // expected strings are written out here from what the bot sends, the same as its own tests pin, and never worked out by
    // the code under test.
    public class ArmorPreviewTests
    {
        static ArmorSubmission Armor() => new()
        {
            ID = 7,
            Status = ArmorStatus.Pending,
            Author = "Test Author",
            ArmorName = "Ninja",
            ArmorDescription = "A dark set for night raids.",
            ArmorScreenshot = "https://murderminershub.com/wp-content/uploads/ArmorPreviews/Ninja.png",
            ArmorChoice = "Bug",
            ArmorSkin = "Default",
            Helmet = 1, Visor = 2, Torso = 3, Shoulders = 4, Biceps = 5, Forearms = 6, Hands = 7,
            Pelvis = 8, Thighs = 9, Legs = 10, Boots = 11, Shields = 12, Other = 13
        };

        static string Html(IHtmlContent content)
        {
            using var writer = new StringWriter();
            content.WriteTo(writer, HtmlEncoder.Default);
            return writer.ToString();
        }

        // The bot's NamesAreListedInACodeBlockThatStartsWithAnEmptyLine and EveryMessageIsAFullAnnouncement
        [Fact]
        public void PostListsTheNameInACodeBlockThatStartsWithAnEmptyLine()
        {
            var post = BotMessages.NewArmorPost(Armor());

            Assert.Equal("New Armor Styles:", post.Title);
            Assert.Equal("https://murderminershub.com/upload-armor-style/", post.TitleLink);
            Assert.Equal("```\nNinja\n```", post.Description);
            Assert.Equal(new EmbedField("Upload your own Armor Style", "https://murderminershub.com/upload-armor-style/", false), Assert.Single(post.Fields));
            Assert.Null(post.Image);
        }

        // The bot's ArmorWithNoNameKeepsItsPlace
        [Fact]
        public void ArmorWithNoNameIsAnEmptyLineOfThePost()
        {
            var armor = Armor();
            armor.ArmorName = null;

            Assert.Equal("```\n\n```", BotMessages.NewArmorPost(armor).Description);
        }

        // ArmorService.ArmorEmbed and FormatArmorStyle, with colours 1 to 13. Fails if a label, its padding, an empty line or
        // the order changes.
        [Fact]
        public void LookupCardIsTheBotsCard()
        {
            var card = BotMessages.LookupCard(Armor());

            Assert.Equal("Ninja", card.Title);
            Assert.Null(card.TitleLink);
            Assert.Equal("```\nA dark set for night raids.\nMade by Test Author```", card.Description);
            Assert.Equal(
            [
                new EmbedField("Armor Style",
                    "```\nHelmet Type: Bug\n\nArmor Skin Type: Default\n\nArmor Colors:\n" +
                    "HELMET       1\nVISOR        2\nTORSO        3\nSHOULDERS    4\nBICEPS       5\nFOREARMS     6\nHANDS        7\n" +
                    "PELVIS       8\nTHIGHS       9\nLEGS         10\nBOOTS        11\nSHIELDS      12\nOTHER        13\n```", false),
                new EmbedField("For Save File (Colors)", "`AQIDBAUGBwgJCgsMDQ==`", true)
            ], card.Fields);
            Assert.Equal("https://murderminershub.com/wp-content/uploads/ArmorPreviews/Ninja.png", card.Image);
        }

        // The bot shows a stored string as it is, whatever the colours say, and reads an empty colour as 0
        [Fact]
        public void StoredSaveStringWinsAndEmptyColoursAreZero()
        {
            var armor = Armor();
            armor.ColorArray = "stored";
            Assert.Equal("`stored`", BotMessages.LookupCard(armor).Fields[1].Value);

            var empty = new ArmorSubmission { ID = 8, Author = "Test Author", ArmorName = "Test Armor 8" };
            var card = BotMessages.LookupCard(empty);

            Assert.Equal("`AAAAAAAAAAAAAAAAAA==`", card.Fields[1].Value);
            Assert.Contains("\nHELMET       0\n", card.Fields[0].Value);
            Assert.Contains("\nOTHER        0\n", card.Fields[0].Value);
            Assert.Equal("```\n\nMade by Test Author```", card.Description);
            Assert.StartsWith("```\nHelmet Type: \n\nArmor Skin Type: \n\n", card.Fields[0].Value);
        }

        // The bot attaches the screenshot only when it can fetch it, and the page loads only an upload on the website
        [Theory]
        [InlineData(null)]
        [InlineData("http://127.0.0.1/x.png")]
        [InlineData("https://murderminershub.com/wp-login.php")]
        public void ScreenshotThatIsntAnUploadIsNoPicture(string? address)
        {
            var armor = Armor();
            armor.ArmorScreenshot = address;

            Assert.Null(BotMessages.LookupCard(armor).Image);
        }

        // EmbedBuilderTemplate's, on every armor message. ArmorReviewPageTests checks the stylesheet draws this colour.
        [Fact]
        public void ColourAndFooterAreTheBots()
        {
            Assert.Equal("#206694", BotMessages.Color);
            Assert.Equal("MinerBot by 24kConnect - Version 2.0", BotMessages.Footer);
        }

        // A helmet or skin someone crafted reaches the Armor Style code block, which the approval rules now block, and the
        // preview shows it as the bot would send it
        [Fact]
        public void CraftedHelmetBreaksTheCardsCodeBlock()
        {
            var armor = Armor();
            armor.ArmorChoice = "Bug```[Free Nitro](https://phish.example)```";

            Assert.Equal(DiscordTextKind.Broken, DiscordText.Of(BotMessages.LookupCard(armor).Fields[0].Value).Kind);
        }

        // UIUtils.shortenTextForEmbed, which the table's 60 characters never reach, but the copy keeps it
        [Fact]
        public void LongNameIsShortenedAsTheBotShortensIt()
        {
            Assert.Equal(new string('a', 253) + "...", BotMessages.Shorten(new string('a', 300), 256));
            Assert.Equal(new string('a', 256), BotMessages.Shorten(new string('a', 256), 256));
            Assert.Equal(new string('a', 252) + "...", BotMessages.Shorten(new string('a', 252) + "\U0001F600" + new string('a', 50), 256));
            Assert.Equal("", BotMessages.Shorten(null, 256));
        }

        // Discord ends a code block only at the next three backticks, so one or two inside show as they are. Three, or one
        // at the end of the text, which makes three with the closing ones, end it early.
        [Theory]
        [InlineData("```\nNinja\n```", DiscordTextKind.CodeBlock, "Ninja")]
        [InlineData("```\nA dark set.\nMade by Test Author```", DiscordTextKind.CodeBlock, "A dark set.\nMade by Test Author")]
        [InlineData("```\n\n```", DiscordTextKind.CodeBlock, "")]
        [InlineData("```\nNinja`s night set\nMade by Test Author```", DiscordTextKind.CodeBlock, "Ninja`s night set\nMade by Test Author")]
        [InlineData("```\nA ``quiet`` set\nMade by `Test Author```", DiscordTextKind.CodeBlock, "A ``quiet`` set\nMade by `Test Author")]
        [InlineData("```\nNinja`\n```", DiscordTextKind.CodeBlock, "Ninja`")]
        [InlineData("`AQIDBAUGBwgJCgsMDQ==`", DiscordTextKind.InlineCode, "AQIDBAUGBwgJCgsMDQ==")]
        [InlineData("``", DiscordTextKind.InlineCode, "")]
        [InlineData("Upload your own Armor Style", DiscordTextKind.Plain, "Upload your own Armor Style")]
        [InlineData("```\nEnds here```[click](https://example.com)\nMade by Test Author```", DiscordTextKind.Broken, "```\nEnds here```[click](https://example.com)\nMade by Test Author```")]
        [InlineData("```\nA dark set.\nMade by Test Author``````", DiscordTextKind.Broken, "```\nA dark set.\nMade by Test Author``````")]
        [InlineData("```\nA dark set.\nMade by Test Author````", DiscordTextKind.Broken, "```\nA dark set.\nMade by Test Author````")]
        [InlineData("`a`b`", DiscordTextKind.Broken, "`a`b`")]
        public void DiscordTextIsReadAsTheBotMeantIt(string value, DiscordTextKind kind, string text)
        {
            Assert.Equal(new DiscordText(kind, text), DiscordText.Of(value));
        }

        // Raw, as Discord shows it, but encoded and isolated, so neither markup nor a right-to-left override can reach the page
        [Fact]
        public void PreviewTextIsEncodedAndIsolated()
        {
            var block = Html(DiscordText.Of("```\n<b>x</b>‮\n```").Html());
            Assert.Equal("<pre class=\"embed-code\"><bdi>&lt;b&gt;x&lt;/b&gt;&#x202E;</bdi></pre>", block);

            var broken = Html(DiscordText.Of("a`<i>").Html());
            Assert.StartsWith("<pre class=\"embed-raw\"><bdi>a", broken);
            Assert.Contains("&lt;i&gt;</bdi></pre><p class=\"embed-note\">", broken);
            Assert.DoesNotContain("<i>", broken);

            Assert.Equal("<code><bdi>&lt;s&gt;</bdi></code>", Html(DiscordText.Of("`<s>`").Html()));
            Assert.Equal("<bdi>&lt;u&gt;</bdi>", Html(DiscordText.Of("<u>").Html()));
        }
    }
}
