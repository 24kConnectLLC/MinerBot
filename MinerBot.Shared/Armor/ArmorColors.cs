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
    }
}
