using DataAccessLibrary.Armor.Models;
using MinerBot_2._0.Extensions;

namespace MinerBot.Tests
{
    // The save-file string that /armor lookup shows and the colour job stores. The strings were worked out on their own
    // from the game's order of parts, so moving or changing the code that makes them can't change what the bot posts.
    public class ArmorColorArrayTests
    {
        [Fact]
        public void ArmorWithoutColorsIsAllZeros()
        {
            Assert.Equal("AAAAAAAAAAAAAAAAAA==", new ArmorStyle().CreateColorArray());
        }

        [Fact]
        public void PartsGoInTheGamesOrder()
        {
            var armor = new ArmorStyle
            {
                Helmet = 1, Visor = 2, Torso = 3, Shoulders = 4, Biceps = 5, Forearms = 6, Hands = 7,
                Pelvis = 8, Thighs = 9, Legs = 10, Boots = 11, Shields = 12, Other = 13
            };

            Assert.Equal("AQIDBAUGBwgJCgsMDQ==", armor.CreateColorArray());
        }

        [Fact]
        public void HighestColorsAreKept()
        {
            var armor = new ArmorStyle
            {
                Helmet = 255, Visor = 255, Torso = 255, Shoulders = 255, Biceps = 255, Forearms = 255, Hands = 255,
                Pelvis = 255, Thighs = 255, Legs = 255, Boots = 255, Shields = 255, Other = 255
            };

            Assert.Equal("/////////////////w==", armor.CreateColorArray());
        }

        // Two parts that swapped places would each land in the other's byte
        [Theory]
        [InlineData("Helmet", "yAAAAAAAAAAAAAAAAA==")]
        [InlineData("Visor", "AMgAAAAAAAAAAAAAAA==")]
        [InlineData("Torso", "AADIAAAAAAAAAAAAAA==")]
        [InlineData("Shoulders", "AAAAyAAAAAAAAAAAAA==")]
        [InlineData("Biceps", "AAAAAMgAAAAAAAAAAA==")]
        [InlineData("Forearms", "AAAAAADIAAAAAAAAAA==")]
        [InlineData("Hands", "AAAAAAAAyAAAAAAAAA==")]
        [InlineData("Pelvis", "AAAAAAAAAMgAAAAAAA==")]
        [InlineData("Thighs", "AAAAAAAAAADIAAAAAA==")]
        [InlineData("Legs", "AAAAAAAAAAAAyAAAAA==")]
        [InlineData("Boots", "AAAAAAAAAAAAAMgAAA==")]
        [InlineData("Shields", "AAAAAAAAAAAAAADIAA==")]
        [InlineData("Other", "AAAAAAAAAAAAAAAAyA==")]
        public void EachPartHasItsOwnPlace(string part, string colorArray)
        {
            var armor = new ArmorStyle();
            typeof(ArmorStyle).GetProperty(part)!.SetValue(armor, (byte)200);

            Assert.Equal(colorArray, armor.CreateColorArray());
        }
    }
}
