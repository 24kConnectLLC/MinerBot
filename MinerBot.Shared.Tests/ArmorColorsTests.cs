using MinerBot.Shared.Armor;

namespace MinerBot.Shared.Tests
{
    // The same strings the bot's own tests pin, so the copy both apps share gives what the bot always posted
    public class ArmorColorsTests
    {
        sealed class Colors : IArmorColors
        {
            public byte Helmet { get; set; }
            public byte Visor { get; set; }
            public byte Torso { get; set; }
            public byte Shoulders { get; set; }
            public byte Biceps { get; set; }
            public byte Forearms { get; set; }
            public byte Hands { get; set; }
            public byte Pelvis { get; set; }
            public byte Thighs { get; set; }
            public byte Legs { get; set; }
            public byte Boots { get; set; }
            public byte Shields { get; set; }
            public byte Other { get; set; }
        }

        [Fact]
        public void ArmorWithoutColorsIsAllZeros()
        {
            Assert.Equal("AAAAAAAAAAAAAAAAAA==", ArmorColors.CreateColorArray(new Colors()));
        }

        [Fact]
        public void PartsGoInTheGamesOrder()
        {
            var armor = new Colors
            {
                Helmet = 1, Visor = 2, Torso = 3, Shoulders = 4, Biceps = 5, Forearms = 6, Hands = 7,
                Pelvis = 8, Thighs = 9, Legs = 10, Boots = 11, Shields = 12, Other = 13
            };

            Assert.Equal("AQIDBAUGBwgJCgsMDQ==", ArmorColors.CreateColorArray(armor));
        }

        [Fact]
        public void HighestColorsAreKept()
        {
            var armor = new Colors
            {
                Helmet = 255, Visor = 255, Torso = 255, Shoulders = 255, Biceps = 255, Forearms = 255, Hands = 255,
                Pelvis = 255, Thighs = 255, Legs = 255, Boots = 255, Shields = 255, Other = 255
            };

            Assert.Equal("/////////////////w==", ArmorColors.CreateColorArray(armor));
        }

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
            var armor = new Colors();
            typeof(Colors).GetProperty(part)!.SetValue(armor, (byte)200);

            Assert.Equal(colorArray, ArmorColors.CreateColorArray(armor));
        }

        [Fact]
        public void ThereAreThirteenParts()
        {
            Assert.Equal(13, (int)ColorChoices.NUM_CHOICES);
            Assert.Equal(13, typeof(IArmorColors).GetProperties().Length);
        }
    }
}
