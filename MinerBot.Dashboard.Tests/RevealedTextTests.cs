using System.Text.Encodings.Web;
using MinerBot.Dashboard.Armor;
using MinerBot.Shared.Text;

namespace MinerBot.Dashboard.Tests
{
    // Submitter text on the armor pages: what it hides is written out where it is, mentions and links are marked, and all
    // of it is encoded inside a bdi
    public class RevealedTextTests
    {
        static string Html(string? text)
        {
            using var writer = new StringWriter();
            RevealedText.Html(text).WriteTo(writer, HtmlEncoder.Default);
            return writer.ToString();
        }

        // One look at the whole text, so a style selector after an emoji and a joiner between two stay as they are, as
        // Reveal leaves them. Fails if the pieces were revealed one at a time.
        [Theory]
        [InlineData("")]
        [InlineData("Plain")]
        [InlineData("Night​Raid")]
        [InlineData("evil‮txt.exe")]
        [InlineData("❤️ Pirate")]
        [InlineData("☠️‍\U0001F9DF")]
        [InlineData("Ping @everyone at https://example.com​ now")]
        [InlineData("Tag\U000E0041s\U000E007F")]
        [InlineData("[<@1000000000000000001>](https://example.com)")]
        [InlineData("Ends here```[click](https://example.com/`x)")]
        public void PiecesPutTogetherAreReveal(string text)
        {
            Assert.Equal(TextInspector.Reveal(text), string.Concat(RevealedText.Pieces(text).Select(piece => piece.Text)));
        }

        // Half of a broken pair is one hidden character, kept out of the theory's data, which has to be valid text
        [Fact]
        public void HalfOfABrokenPairIsShownToo()
        {
            var text = "Broken " + (char)0xD800 + " half";

            Assert.Equal(TextInspector.Reveal(text), string.Concat(RevealedText.Pieces(text).Select(piece => piece.Text)));
            Assert.Contains(new TextPiece("[U+D800]", TextPart.Hidden, "broken character"), RevealedText.Pieces(text));
        }

        [Fact]
        public void HiddenCharacterIsAPieceOfItsOwnWithItsName()
        {
            Assert.Equal(
            [
                new TextPiece("Night", TextPart.Plain),
                new TextPiece("[U+200B]", TextPart.Hidden, "zero-width space"),
                new TextPiece("Raid", TextPart.Plain)
            ], RevealedText.Pieces("Night​Raid"));

            // Outside the Basic Multilingual Plane it's still one character
            Assert.Equal(
            [
                new TextPiece("A", TextPart.Plain),
                new TextPiece("[U+E0001]", TextPart.Hidden, "language tag"),
                new TextPiece("B", TextPart.Plain)
            ], RevealedText.Pieces("A\U000E0001B"));
        }

        [Fact]
        public void MentionsAndLinksAreMarkedWhereTheyAre()
        {
            Assert.Equal(
            [
                new TextPiece("Ping ", TextPart.Plain),
                new TextPiece("@everyone", TextPart.Mention),
                new TextPiece(" at ", TextPart.Plain),
                new TextPiece("https://example.com", TextPart.Link),
                new TextPiece(" now", TextPart.Plain)
            ], RevealedText.Pieces("Ping @everyone at https://example.com now"));

            // A mention inside a masked link is marked as a mention
            Assert.Equal(
            [
                new TextPiece("[", TextPart.Link),
                new TextPiece("<@1>", TextPart.Mention),
                new TextPiece("](https://example.com)", TextPart.Link)
            ], RevealedText.Pieces("[<@1>](https://example.com)"));
        }

        // A backtick can end the code block the bot shows the text in, wherever it is, so it's marked inside a link too
        [Fact]
        public void BackticksAreMarkedWhereverTheyAre()
        {
            Assert.Equal(
            [
                new TextPiece("Ends here", TextPart.Plain),
                new TextPiece("```", TextPart.Backtick),
                new TextPiece("[click](https://example.com)", TextPart.Link)
            ], RevealedText.Pieces("Ends here```[click](https://example.com)"));

            Assert.Equal(
            [
                new TextPiece("https://example.com/", TextPart.Link),
                new TextPiece("`", TextPart.Backtick),
                new TextPiece("x", TextPart.Link)
            ], RevealedText.Pieces("https://example.com/`x"));

            Assert.StartsWith("<bdi class=\"revealed\">a<mark class=\"backtick\" title=\"Backtick\">", Html("a`b"));
        }

        // Fails if any of the text reaches the page as markup, or isn't isolated from the page's own words. The encoder
        // writes the plus of a code such as U+202E as &#x2B;, which a browser shows as +.
        [Fact]
        public void HtmlIsEncodedInsideABdi()
        {
            Assert.Equal(
                "<bdi class=\"revealed\">&lt;script&gt;alert(1)&lt;/script&gt;<mark class=\"hidden-char\" title=\"right-to-left override\">[U&#x2B;202E]</mark></bdi>",
                Html("<script>alert(1)</script>‮"));

            Assert.Equal(
                "<bdi class=\"revealed\"><mark class=\"mention\" title=\"Discord mention\">@here</mark> &amp; <mark class=\"link\" title=\"Link\">www.example.com/</mark>&lt;b&gt;</bdi>",
                Html("@here & www.example.com/<b>"));

            Assert.Equal("<bdi class=\"revealed\"></bdi>", Html(null));

            // A line end is encoded too, and the page's pre-wrap class shows it as one
            Assert.Equal("<bdi class=\"revealed\">Line&#xA;Next</bdi>", Html("Line\nNext"));
        }
    }
}
