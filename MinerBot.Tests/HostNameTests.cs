using Microsoft.Extensions.Caching.Memory;
using MinerBot_2._0;
using MinerBot_2._0.Services;

namespace MinerBot.Tests
{
    // A host name is shown inside inline code in the lobby list. The names are built from code points, so the test source
    // stays plain text and a theory never carries half of a character.
    public class HostNameTests
    {
        static string Text(params int[] codePoints) => string.Concat(codePoints.Select(char.ConvertFromUtf32));

        static string Clean(string name) => UIUtils.cleanTextForEmbed(name);

        [Fact]
        public void HindiNameKeepsItsVowelSignsAndVirama()
        {
            // Arjun Sharma
            var name = Text(0x0905, 0x0930, 0x094D, 0x091C, 0x0941, 0x0928, 0x20, 0x0936, 0x0930, 0x094D, 0x092E, 0x093E);

            Assert.Equal(name, Clean(name));
        }

        [Fact]
        public void ArabicNameKeepsItsMarks()
        {
            // Muhammad with its harakat and shadda
            var name = Text(0x0645, 0x064F, 0x062D, 0x064E, 0x0645, 0x0651, 0x064E, 0x062F);

            Assert.Equal(name, Clean(name));
        }

        [Fact]
        public void LetterWithACombiningAccentStays()
        {
            var name = "Jose" + Text(0x0301);

            Assert.Equal(name, Clean(name));
        }

        [Fact]
        public void EmojiJoinedByZeroWidthJoinersStayWhole()
        {
            var family = Text(0x1F468, 0x200D, 0x1F469, 0x200D, 0x1F467, 0x200D, 0x1F466);

            Assert.Equal(family, Clean(family));
            Assert.Equal("Dad " + family, Clean("Dad " + family));
        }

        // Styled names from outside the basic plane once came out empty, so they showed as Host Unknown
        [Fact]
        public void StyledLettersOutsideTheBasicPlaneStay()
        {
            var name = Text(0x1D4D1, 0x1D4F8, 0x1D4F7, 0x1D4F7, 0x1D502, 0x20, 0x1D50A, 0x1D51E, 0x1D52A);

            Assert.Equal(name, Clean(name));
        }

        [Fact]
        public void PunctuationAndSymbolsStay()
        {
            Assert.Equal("[TAG] Player_1! (pro) #2 *x* ~y~ 100% <3", Clean("[TAG] Player_1! (pro) #2 *x* ~y~ 100% <3"));
        }

        // A backtick would end the inline code
        [Fact]
        public void BacktickBecomesAnApostrophe()
        {
            Assert.Equal("a'b'c", Clean("a`b`c"));
        }

        [Fact]
        public void ControlCharactersAreRemoved()
        {
            var name = "Bad" + Text(0x07) + "Na" + Text(0x00) + "me" + Text(0x1B) + "[31m" + Text(0x7F) + Text(0x85) + Text(0x9F);

            Assert.Equal("BadName[31m", Clean(name));
        }

        [Fact]
        public void LineAndParagraphSeparatorsAreRemoved()
        {
            Assert.Equal("LineBreakPara", Clean("Line" + Text(0x2028) + "Break" + Text(0x2029) + "Para"));
        }

        // These can reorder the rest of the row, such as the map's name after the host's
        [Theory]
        [InlineData(0x202A)]
        [InlineData(0x202B)]
        [InlineData(0x202C)]
        [InlineData(0x202D)]
        [InlineData(0x202E)]
        [InlineData(0x2066)]
        [InlineData(0x2067)]
        [InlineData(0x2068)]
        [InlineData(0x2069)]
        public void DirectionControlIsRemoved(int codePoint)
        {
            Assert.Equal("abcdef", Clean("abc" + Text(codePoint) + "def"));
        }

        [Fact]
        public void HalfOfASurrogatePairIsRemoved()
        {
            var lowHalf = new string((char)0xDE00, 1);
            var highHalf = new string((char)0xD83D, 1);

            Assert.Equal("xy", Clean("x" + lowHalf + "y" + highHalf));
            Assert.Equal("xy", Clean(highHalf + "x" + highHalf + "y"));
        }

        static string Repeat(int codePoint, int count) => string.Concat(Enumerable.Repeat(Text(codePoint), count));

        // Every script needs only a few marks on a letter, and a pile of them is drawn over the rows above and below
        [Fact]
        public void MarksOnALetterStopAtFour()
        {
            Assert.Equal("Z" + Repeat(0x0301, 4), Clean("Z" + Repeat(0x0301, 31)));
            Assert.Equal("a" + Repeat(0x0301, 4) + "b" + Repeat(0x0316, 4), Clean("a" + Repeat(0x0301, 4) + "b" + Repeat(0x0316, 4)));
        }

        // A character that isn't seen, or one that's removed, doesn't start a new pile
        [Fact]
        public void MarksAcrossAnInvisibleOrRemovedCharacterStillCount()
        {
            Assert.Equal("a" + Repeat(0x0301, 4) + Text(0x200D), Clean("a" + Repeat(0x0301, 4) + Text(0x200D) + Repeat(0x0301, 4)));
            Assert.Equal("a" + Repeat(0x0301, 4), Clean("a" + Repeat(0x0301, 4) + Text(0x07) + Repeat(0x0301, 4)));
            Assert.Equal("a" + Repeat(0x1D167, 4), Clean("a" + Repeat(0x1D167, 6)));
        }

        // Enclosing marks pile up the same way
        [Fact]
        public void EnclosingMarksStopAtFour()
        {
            Assert.Equal("a" + Repeat(0x20DD, 4), Clean("a" + Repeat(0x20DD, 6)));
            Assert.Equal("a" + Repeat(0x0489, 4), Clean("a" + Repeat(0x0489, 31)));
        }

        // A backtick is shown as an apostrophe, a character of its own, so the marks after it start a new pile
        [Fact]
        public void BacktickStartsANewPile()
        {
            Assert.Equal("a" + Repeat(0x0301, 4) + "'" + Repeat(0x0301, 4), Clean("a" + Repeat(0x0301, 4) + "`" + Repeat(0x0301, 4)));
        }

        // Hebrew with its points and a cantillation mark on one letter, and Vietnamese with two marks on one letter
        [Fact]
        public void LetterWithSeveralMarksKeepsThemAll()
        {
            var hebrew = Text(0x05D1, 0x05BC, 0x05B0, 0x0591, 0x05BD, 0x05E8);
            var vietnamese = Text(0x0065, 0x0323, 0x0302);

            Assert.Equal(hebrew, Clean(hebrew));
            Assert.Equal(vietnamese, Clean(vietnamese));
        }

        // The lobby list shows Host Unknown when nothing of the name is left
        [Fact]
        public async Task NameWithNothingLeftShowsHostUnknown()
        {
            var service = new SteamAPIService(new FakeGetData(_ => { }), new MemoryCache(new MemoryCacheOptions()));
            var lobby = LobbyListLimitTests.Lobby(1);
            lobby.HostName = Text(0x202E) + " " + Text(0x07);

            Assert.Contains("**Host:** `Host Unknown`", await service.GetLobbiesDisplay([lobby]));
        }

        [Fact]
        public async Task NameOfOnlyEmojiIsShown()
        {
            var service = new SteamAPIService(new FakeGetData(_ => { }), new MemoryCache(new MemoryCacheOptions()));
            var lobby = LobbyListLimitTests.Lobby(1);
            lobby.HostName = Text(0x1F600);

            Assert.Contains($"**Host:** `{Text(0x1F600)}`", await service.GetLobbiesDisplay([lobby]));
        }
    }
}
