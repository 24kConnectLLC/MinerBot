namespace MinerBot.Shared.Armor
{
    public static class ArmorColors
    {
        // The colours as the game's save file holds them, which /armor lookup shows and the colour job stores
        public static string CreateColorArray(IArmorColors armor)
        {
            // We map 13 armor parts
            //byte[] colorArray = { armor.Helmet, armor.Visor, armor.Torso, armor.Shoulders, armor.Biceps, armor.Forearms,
            //                      armor.Hands, armor.Pelvis, armor.Thighs, armor.Legs, armor.Boots, armor.Shields, armor.Other};

            // Better to visualize mapping. Less likely to mess up order of values.
            byte[] colorArray = new byte[(byte)ColorChoices.NUM_CHOICES];
            colorArray[(int)ColorChoices.Helmet] = armor.Helmet;
            colorArray[(int)ColorChoices.Visor] = armor.Visor;
            colorArray[(int)ColorChoices.Torso] = armor.Torso;
            colorArray[(int)ColorChoices.Shoulders] = armor.Shoulders;
            colorArray[(int)ColorChoices.Biceps] = armor.Biceps;
            colorArray[(int)ColorChoices.Forearms] = armor.Forearms;
            colorArray[(int)ColorChoices.Hands] = armor.Hands;
            colorArray[(int)ColorChoices.Pelvis] = armor.Pelvis;
            colorArray[(int)ColorChoices.Thighs] = armor.Thighs;
            colorArray[(int)ColorChoices.Legs] = armor.Legs;
            colorArray[(int)ColorChoices.Boots] = armor.Boots;
            colorArray[(int)ColorChoices.Shields] = armor.Shields;
            colorArray[(int)ColorChoices.Other] = armor.Other;

            return Convert.ToBase64String(colorArray);
        }

        // The same string for a submission, whatever its status, as the bot will show it once it's approved: a colour the
        // website left empty reads as 0, the way the bot reads the column. Null when a colour is outside a byte, because
        // the bot couldn't read that armor at all, and the approval rules block it.
        public static string? CreateColorArray(IArmorSubmission armor)
        {
            int?[] colors = [armor.Helmet, armor.Visor, armor.Torso, armor.Shoulders, armor.Biceps, armor.Forearms, armor.Hands,
                armor.Pelvis, armor.Thighs, armor.Legs, armor.Boots, armor.Shields, armor.Other];

            if (colors.Any(color => color is < 0 or > 255))
                return null;

            return CreateColorArray(new SubmissionColors(armor));
        }

        // A submission's colours as bytes, so the save file's order is set in one place, above
        sealed class SubmissionColors(IArmorSubmission armor) : IArmorColors
        {
            public byte Helmet => Byte(armor.Helmet);
            public byte Visor => Byte(armor.Visor);
            public byte Torso => Byte(armor.Torso);
            public byte Shoulders => Byte(armor.Shoulders);
            public byte Biceps => Byte(armor.Biceps);
            public byte Forearms => Byte(armor.Forearms);
            public byte Hands => Byte(armor.Hands);
            public byte Pelvis => Byte(armor.Pelvis);
            public byte Thighs => Byte(armor.Thighs);
            public byte Legs => Byte(armor.Legs);
            public byte Boots => Byte(armor.Boots);
            public byte Shields => Byte(armor.Shields);
            public byte Other => Byte(armor.Other);

            static byte Byte(int? color) => (byte)(color ?? 0);
        }
    }
}
