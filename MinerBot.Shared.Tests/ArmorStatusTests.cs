using MinerBot.Shared.Armor;

namespace MinerBot.Shared.Tests
{
    public class ArmorStatusTests
    {
        // The words the website's table holds
        [Fact]
        public void TheWordsAreTheWebsites()
        {
            Assert.Equal(["Pending Approval", "Approved", "Not Approved"], ArmorStatus.All);
        }

        [Theory]
        [InlineData(ArmorStatus.Pending)]
        [InlineData(ArmorStatus.Approved)]
        [InlineData(ArmorStatus.NotApproved)]
        public void EachWordIsKnown(string status)
        {
            Assert.True(ArmorStatus.IsKnown(status));
        }

        // Anything else would be a fourth word in the table
        [Theory]
        [InlineData("approved")]
        [InlineData("APPROVED")]
        [InlineData("Approved ")]
        [InlineData("Pending")]
        [InlineData("NotApproved")]
        [InlineData("")]
        [InlineData(null)]
        public void OtherSpellingsAreNot(string? status)
        {
            Assert.False(ArmorStatus.IsKnown(status));
        }
    }
}
