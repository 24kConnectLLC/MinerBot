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

        // A submission as the website stores it, whatever its status: each colour can be missing or outside a byte
        sealed class Submission : IArmorSubmission
        {
            public long ID { get; set; } = 1;
            public string? ArmorName { get; set; } = "Test Armor 1";
            public string? ArmorDescription { get; set; }
            public string? ArmorScreenshot { get; set; }
            public string Author { get; set; } = "Test Author";
            public string? ArmorChoice { get; set; }
            public string? ArmorSkin { get; set; }
            public string? ColorArray { get; set; }
            public int? Helmet { get; set; }
            public int? Visor { get; set; }
            public int? Torso { get; set; }
            public int? Shoulders { get; set; }
            public int? Biceps { get; set; }
            public int? Forearms { get; set; }
            public int? Hands { get; set; }
            public int? Pelvis { get; set; }
            public int? Thighs { get; set; }
            public int? Legs { get; set; }
            public int? Boots { get; set; }
            public int? Shields { get; set; }
            public int? Other { get; set; }
        }

        // The bot reads a colour the website left empty as 0, so the review page shows the string the bot will
        [Fact]
        public void SubmissionWithoutColorsIsAllZeros()
        {
            Assert.Equal("AAAAAAAAAAAAAAAAAA==", ArmorColors.CreateColorArray(new Submission()));
        }

        [Fact]
        public void SubmissionPartsGoInTheGamesOrder()
        {
            var armor = new Submission
            {
                Helmet = 1, Visor = 2, Torso = 3, Shoulders = 4, Biceps = 5, Forearms = 6, Hands = 7,
                Pelvis = 8, Thighs = 9, Legs = 10, Boots = 11, Shields = 12, Other = 13
            };

            Assert.Equal("AQIDBAUGBwgJCgsMDQ==", ArmorColors.CreateColorArray(armor));
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
        public void EachSubmissionPartHasItsOwnPlace(string part, string colorArray)
        {
            var armor = new Submission();
            typeof(Submission).GetProperty(part)!.SetValue(armor, 200);

            Assert.Equal(colorArray, ArmorColors.CreateColorArray(armor));
        }

        // The bot couldn't read such an armor at all, so there's no string it would show
        [Theory]
        [InlineData(256)]
        [InlineData(-1)]
        [InlineData(int.MaxValue)]
        public void SubmissionWithAColorOutsideAByteHasNoString(int color)
        {
            Assert.Null(ArmorColors.CreateColorArray(new Submission { Helmet = 1, Other = color }));
            Assert.Equal("/////////////////w==", ArmorColors.CreateColorArray(new Submission
            {
                Helmet = 255, Visor = 255, Torso = 255, Shoulders = 255, Biceps = 255, Forearms = 255, Hands = 255,
                Pelvis = 255, Thighs = 255, Legs = 255, Boots = 255, Shields = 255, Other = 255
            }));
        }
    }
}
