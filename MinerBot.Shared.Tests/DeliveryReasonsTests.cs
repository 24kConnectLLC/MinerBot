using MinerBot.Shared.Delivery;

namespace MinerBot.Shared.Tests
{
    // Why a post didn't go, read from Discord's numbers alone, and said in words a server's admin can act on
    public class DeliveryReasonsTests
    {
        // Discord's code decides a refusal's reason, by its number, whatever Discord.Net calls it: 50001 is Missing Access,
        // which Discord.Net names MissingPermissions
        [Theory]
        [InlineData(403, 50001, DeliveryReasons.MissingAccess)]
        [InlineData(403, 50013, DeliveryReasons.DiscordPermissions)]
        [InlineData(404, 10003, DeliveryReasons.UnknownChannel)]
        [InlineData(404, 10004, DeliveryReasons.UnknownServer)]
        [InlineData(400, 50008, DeliveryReasons.NotTextChannel)]
        [InlineData(413, 40005, DeliveryReasons.TooLarge)]
        [InlineData(400, 50035, DeliveryReasons.InvalidPost)]
        [InlineData(403, 20028, DeliveryReasons.ChannelWriteLimit)]
        [InlineData(429, 20016, DeliveryReasons.RateLimited)]
        [InlineData(400, 20016, DeliveryReasons.Slowmode)]
        [InlineData(403, 50007, DeliveryReasons.Refused)]
        [InlineData(403, null, DeliveryReasons.Refused)]
        [InlineData(404, 0, DeliveryReasons.Refused)]
        [InlineData(401, 0, DeliveryReasons.Unauthorized)]
        [InlineData(401, 50013, DeliveryReasons.Unauthorized)]
        [InlineData(429, null, DeliveryReasons.RateLimited)]
        [InlineData(500, 0, DeliveryReasons.NoAnswer)]
        [InlineData(502, null, DeliveryReasons.NoAnswer)]
        [InlineData(503, 50013, DeliveryReasons.NoAnswer)]
        public void AnswerIsReadByItsNumbers(int status, int? code, string reason)
        {
            Assert.Equal(reason, DeliveryReasons.ForAnswer(status, code));
        }

        // Discord's 5xx and 401 answers carry code 0, its "general error", which is no code of the post's
        [Fact]
        public void CodeOfZeroIsNoCode()
        {
            Assert.Null(DeliveryReasons.CodeOf(0));
            Assert.Null(DeliveryReasons.CodeOf(null));
            Assert.Equal(50013, DeliveryReasons.CodeOf(50013));
            Assert.Equal("Discord didn't answer in time (HTTP 500)", DeliveryReasons.Describe(DeliveryReasons.NoAnswer, null, 500, 0));
        }

        // The page's words with Discord's numbers after them, the ones there are
        [Fact]
        public void DescribeNamesTheStatusAndTheCode()
        {
            Assert.Equal("Discord says MinerBot is missing a permission there (Missing Permissions), though its copy showed it could post (HTTP 403, Discord 50013)",
                DeliveryReasons.Describe(DeliveryReasons.DiscordPermissions, null, 403, 50013));
            Assert.Equal("Discord refused it (HTTP 403)", DeliveryReasons.Describe(DeliveryReasons.Refused, null, 403, null));
            Assert.Equal("MinerBot is missing Send Messages there", DeliveryReasons.Describe(DeliveryReasons.MissingPermissions, DeliveryReasons.SendMessages, null, null));
            Assert.Equal("the server removed MinerBot", DeliveryReasons.Describe(DeliveryReasons.Left, null, null, null));
        }

        [Theory]
        [InlineData(DeliveryReasons.SendMessages, "Send Messages")]
        [InlineData(DeliveryReasons.ViewChannel | DeliveryReasons.EmbedLinks, "View Channel and Embed Links")]
        [InlineData(DeliveryReasons.ViewChannel | DeliveryReasons.SendMessages | DeliveryReasons.EmbedLinks, "View Channel, Send Messages and Embed Links")]
        [InlineData(15, "View Channel, Send Messages, Embed Links and Read Message History")]
        [InlineData(0, "a permission it needs")]
        public void MissingPermissionsAreNamedAsDiscordNamesThem(int mask, string names)
        {
            Assert.Equal(names, DeliveryReasons.MissingNames(mask));
        }

        // Every reason has words of its own, never a stored word such as not_text_channel, and an unknown one is said to be
        // one, never shown as it's stored
        [Fact]
        public void EveryReasonHasWordsOfItsOwn()
        {
            var words = DeliveryReasons.All.Select(reason => DeliveryReasons.Words(reason, DeliveryReasons.SendMessages)).ToList();

            Assert.Equal(DeliveryReasons.All.Count, words.Distinct().Count());
            Assert.All(words, text => Assert.Matches("^[a-zA-Z]", text));
            Assert.All(words, text => Assert.DoesNotContain("_", text));
            Assert.Equal("a reason this version of the dashboard doesn't know", DeliveryReasons.Words("new_reason"));
            Assert.Equal("a reason this version of the dashboard doesn't know", DeliveryReasons.Words(null));
        }

        // A message's reasons and a dropped item's are apart, and each a word in small letters that fits the columns
        [Fact]
        public void FailuresAndDropsAreApart()
        {
            Assert.Empty(DeliveryReasons.Failures.Intersect(DeliveryReasons.Drops));
            Assert.Equal(DeliveryReasons.Failures.Count + DeliveryReasons.Drops.Count, DeliveryReasons.All.Distinct().Count());
            Assert.All(DeliveryReasons.All, reason => Assert.Matches("^[a-z_]{1,30}$", reason));
            Assert.True(DeliveryReasons.IsFailure(DeliveryReasons.NotSent));
            Assert.False(DeliveryReasons.IsFailure(DeliveryReasons.TooOld));
            Assert.True(DeliveryReasons.IsDrop(DeliveryReasons.Left));
            Assert.False(DeliveryReasons.IsKnown("Left"));
            Assert.False(DeliveryReasons.IsKnown(null));
        }
    }
}
