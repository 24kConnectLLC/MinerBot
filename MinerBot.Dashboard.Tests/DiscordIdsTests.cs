using MinerBot.Dashboard.Settings;

namespace MinerBot.Dashboard.Tests
{
    // The ID lists come from environment variables, so they're text. A user name, a mention or a typo is refused rather
    // than quietly matching nobody.
    public class DiscordIdsTests
    {
        static readonly HashSet<ulong> Two = [1000000000000000001, 1000000000000000002];

        [Theory]
        [InlineData("1000000000000000001,1000000000000000002")]
        [InlineData("1000000000000000001;1000000000000000002")]
        [InlineData("1000000000000000001 1000000000000000002")]
        [InlineData("1000000000000000001\t1000000000000000002")]
        [InlineData("1000000000000000001\n1000000000000000002")]
        [InlineData("1000000000000000001\r\n1000000000000000002")]
        [InlineData(" 1000000000000000001 , 1000000000000000002 ; ")]
        public void CommasSemicolonsAndWhiteSpaceSeparateIds(string text)
        {
            Assert.True(DiscordIds.TryParse(text, out var ids, out var problem));
            Assert.Null(problem);
            Assert.Equal(Two, ids.ToHashSet());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(",;")]
        public void EmptyListIsAnEmptySet(string? text)
        {
            Assert.True(DiscordIds.TryParse(text, out var ids, out _));
            Assert.Empty(ids);
        }

        [Fact]
        public void DuplicatesCollapse()
        {
            Assert.True(DiscordIds.TryParse("1000000000000000001,1000000000000000001 1000000000000000002", out var ids, out _));
            Assert.Equal(Two, ids.ToHashSet());
        }

        [Theory]
        [InlineData("10000000000000000")]
        [InlineData("18446744073709551615")]
        public void SeventeenToTwentyDigitsFitWithTheBiggestId(string text)
        {
            Assert.True(DiscordIds.TryParse(text, out var ids, out _));
            Assert.Equal(ulong.Parse(text), Assert.Single(ids));
        }

        [Theory]
        [InlineData("12ab")]
        [InlineData("123")]
        [InlineData("1000000000000000")]
        [InlineData("100000000000000000000")]
        [InlineData("18446744073709551616")]
        [InlineData("00000000000000000")]
        [InlineData("test_admin")]
        [InlineData("<@1000000000000000001>")]
        [InlineData("+1000000000000000001")]
        [InlineData("-1000000000000000001")]
        [InlineData("1000000000000000001,1000000000000000002x")]
        [InlineData("１００００００００００００００００")]
        public void AnythingButDigitsIsRefusedWithoutRepeatingIt(string text)
        {
            Assert.False(DiscordIds.TryParse(text, out var ids, out var problem));
            Assert.Empty(ids);
            Assert.NotNull(problem);
            Assert.DoesNotContain(text, problem);
        }

        [Fact]
        public void ProblemSaysWhichEntry()
        {
            Assert.False(DiscordIds.TryParse("1000000000000000001, 1000000000000000002, nobody", out _, out var problem));
            Assert.Contains("entry 3", problem);
        }
    }
}
