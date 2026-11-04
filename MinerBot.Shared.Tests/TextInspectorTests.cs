using MinerBot.Shared.Text;

namespace MinerBot.Shared.Tests
{
    public class TextInspectorTests
    {
        static HiddenCharacter OnlyHidden(string text) => Assert.Single(TextInspector.Inspect(text).Hidden);

        [Fact]
        public void ZeroWidthSpaceIsFoundWhereItIs()
        {
            var hidden = OnlyHidden("ab\u200Bc");

            Assert.Equal(2, hidden.Index);
            Assert.Equal(0x200B, hidden.CodePoint);
            Assert.Equal("zero-width space", hidden.Name);
            Assert.Equal("U+200B", hidden.Code);
            Assert.False(hidden.ChangesDirection);
        }

        [Theory]
        [InlineData(0x202E)]
        [InlineData(0x2066)]
        [InlineData(0x200F)]
        [InlineData(0x061C)]
        [InlineData(0x2069)]
        public void DirectionControlsAreMarked(int codePoint)
        {
            var findings = TextInspector.Inspect("abc" + char.ConvertFromUtf32(codePoint) + "def");

            Assert.True(Assert.Single(findings.Hidden).ChangesDirection);
            Assert.True(findings.HasDirectionChanges);
            Assert.True(findings.HasHidden);
        }

        // Tag characters lie outside the Basic Multilingual Plane, so each one takes two UTF-16 characters
        [Fact]
        public void TagCharacterIsOneCharacter()
        {
            var hidden = OnlyHidden("a\U000E0041b");

            Assert.Equal(1, hidden.Index);
            Assert.Equal(0xE0041, hidden.CodePoint);
            Assert.Equal("tag character", hidden.Name);
        }

        [Theory]
        [InlineData(0x00AD, "soft hyphen")]
        [InlineData(0xFEFF, "zero-width no-break space")]
        [InlineData(0x2060, "word joiner")]
        [InlineData(0x3164, "Hangul filler")]
        [InlineData(0x2800, "Braille pattern blank")]
        [InlineData(0x115F, "Hangul choseong filler")]
        [InlineData(0xFFA0, "halfwidth Hangul filler")]
        [InlineData(0x0007, "control character")]
        [InlineData(0x001B, "control character")]
        [InlineData(0x2061, "function application")]
        [InlineData(0x034F, "combining grapheme joiner")]
        [InlineData(0x17B4, "Khmer inherent vowel")]
        [InlineData(0x180B, "Mongolian variation selector")]
        [InlineData(0x180F, "Mongolian variation selector")]
        [InlineData(0xFE00, "variation selector")]
        [InlineData(0xFE0F, "emoji style selector")]
        [InlineData(0xE0100, "variation selector")]
        [InlineData(0xE01EF, "variation selector")]
        [InlineData(0x1D173, "invisible formatting character")]
        [InlineData(0x2065, "unassigned invisible character")]
        [InlineData(0xE0FFF, "unassigned invisible character")]
        public void InvisibleCharactersAreFound(int codePoint, string name)
        {
            var hidden = OnlyHidden("x" + char.ConvertFromUtf32(codePoint) + "y");

            Assert.Equal(codePoint, hidden.CodePoint);
            Assert.Equal(name, hidden.Name);
        }

        // U+FE0E and U+FE0F pick the text or emoji look of a symbol, a punctuation mark or a keycap's digit, so after one
        // of those they're part of what shows
        [Theory]
        [InlineData("\u2620\uFE0F Pirate")]
        [InlineData("Agent 0\uFE0F\u20E3")]
        [InlineData("#\uFE0F\u20E3 One")]
        [InlineData("Wow\u203C\uFE0F")]
        [InlineData("\u2139\uFE0F Info")]
        [InlineData("\u2764\uFE0E text heart")]
        [InlineData("\U0001F441\uFE0F Eye")]
        public void StyleSelectorAfterWhatItStylesIsNotHidden(string text)
        {
            Assert.Empty(TextInspector.Inspect(text).Hidden);
            Assert.Equal(text, TextInspector.Reveal(text));
        }

        // Anywhere else it changes nothing and shows nothing
        [Theory]
        [InlineData("\uFE0F", 0)]
        [InlineData("Knight\uFE0F", 6)]
        [InlineData("Knight \uFE0E", 7)]
        [InlineData("\u2764\uFE0F\uFE0F", 2)]
        public void StyleSelectorWithNothingToStyleIsHidden(string text, int index)
        {
            Assert.Equal(index, OnlyHidden(text).Index);
        }

        // U+FE0F, which makes the heart an emoji, is a mark rather than a format character, and styles the heart
        [Theory]
        [InlineData("I \u2764\uFE0F MM")]
        [InlineData("Tab\tand\r\nlines")]
        [InlineData("Plain text, with punctuation!")]
        [InlineData("Caf\u00E9 \u4E2D\u6587")]
        [InlineData("")]
        [InlineData(null)]
        public void VisibleTextHasNothingHidden(string? text)
        {
            var findings = TextInspector.Inspect(text);

            Assert.Empty(findings.Hidden);
            Assert.False(findings.HasHidden);
            Assert.False(findings.HasDirectionChanges);
        }

        // A half of a pair that lost the other half
        [Fact]
        public void BrokenSurrogateIsHidden()
        {
            Assert.Equal("broken character", OnlyHidden("a" + (char)0xD800 + "b").Name);
        }

        [Fact]
        public void RevealShowsEachHiddenCharacter()
        {
            Assert.Equal("a[U+200B]b", TextInspector.Reveal("a\u200Bb"));
            Assert.Equal("[U+202E]txt.exe[U+E0041]", TextInspector.Reveal("\u202Etxt.exe\U000E0041"));
            Assert.Equal("Test[U+E0100]Author[U+034F]", TextInspector.Reveal("Test\U000E0100Author\u034F"));
            Assert.Equal("No change", TextInspector.Reveal("No change"));
            Assert.Equal("", TextInspector.Reveal(null));
        }

        [Theory]
        [InlineData("@everyone")]
        [InlineData("@here")]
        [InlineData("@EVERYONE")]
        [InlineData("<@1000000000000000001>")]
        [InlineData("<@!1000000000000000001>")]
        [InlineData("<@&1000000000000000001>")]
        [InlineData("<#1000000000000000001>")]
        public void EachMentionIsFound(string mention)
        {
            var match = Assert.Single(TextInspector.Inspect($"Hello {mention} there").Mentions);

            Assert.Equal(6, match.Index);
            Assert.Equal(mention, match.Text);
        }

        [Theory]
        [InlineData("<@abc>")]
        [InlineData("@ everyone")]
        [InlineData("<@>")]
        [InlineData("<@\u0661\u0662\u0663>")]
        [InlineData("<#\u0661\u0662\u0663>")]
        [InlineData("email@example.com")]
        public void MentionLookalikesAreNot(string text)
        {
            Assert.Empty(TextInspector.Inspect(text).Mentions);
        }

        [Theory]
        [InlineData("https://example.com/x")]
        [InlineData("http://example.com")]
        [InlineData("HTTPS://EXAMPLE.COM")]
        [InlineData("ftp://files.example.com/a")]
        [InlineData("steam://run/274900")]
        [InlineData("www.example.com")]
        [InlineData("discord.gg/abc123")]
        [InlineData("discord.com/invite/abc123")]
        [InlineData("discordapp.com/invite/abc123")]
        [InlineData("[click here](https://example.com)")]
        public void EachLinkIsFound(string link)
        {
            var match = Assert.Single(TextInspector.Inspect($"See {link} now").Links);

            Assert.Equal(4, match.Index);
            Assert.Equal(link, match.Text);
        }

        [Theory]
        [InlineData("example.com")]
        [InlineData("Mr.Fancy")]
        [InlineData("no links here")]
        [InlineData("http://")]
        [InlineData("[just brackets]")]
        public void LinkLookalikesAreNot(string text)
        {
            Assert.Empty(TextInspector.Inspect(text).Links);
        }

        [Fact]
        public void EveryFindingIsListed()
        {
            var findings = TextInspector.Inspect("@here \u200B<@1000000000000000001> https://a.example www.b.example \u2066");

            Assert.Equal([6, 62], findings.Hidden.Select(hidden => hidden.Index));
            Assert.Equal(["@here", "<@1000000000000000001>"], findings.Mentions.Select(mention => mention.Text));
            Assert.Equal(["https://a.example", "www.b.example"], findings.Links.Select(link => link.Text));
        }
    }
}
