using Discord;
using MinerBot.Shared.Discord;

namespace MinerBot.Tests
{
    // The shared library has no Discord.Net, so its copies of Discord's limits are checked against Discord.Net here.
    // The approval rules use them to refuse an armor the bot couldn't show.
    public class DiscordLimitsTests
    {
        [Fact]
        public void EmbedLimitsAreDiscordNets()
        {
            Assert.Equal(EmbedBuilder.MaxTitleLength, DiscordLimits.EmbedTitle);
            Assert.Equal(EmbedBuilder.MaxDescriptionLength, DiscordLimits.EmbedDescription);
        }

        // /armor lookup offers each matching name as a suggestion, so one name over this fails every search that finds it
        [Fact]
        public void SuggestionHoldsAsManyCharactersAsTheLimit()
        {
            var longest = new string('a', DiscordLimits.AutocompleteChoiceName);
            var result = new AutocompleteResult(longest, longest);

            Assert.Equal(longest, result.Name);
            Assert.ThrowsAny<ArgumentException>(() => new AutocompleteResult(longest + "a", longest));
        }

        // Discord.Net counts UTF-16 characters, so 51 emoji are too many although the website's table holds 60
        [Fact]
        public void SuggestionCountsAnEmojiAsTwo()
        {
            var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 51));

            Assert.ThrowsAny<ArgumentException>(() => new AutocompleteResult(emoji, "value"));
        }
    }
}
