using DataAccessLibrary.Armor.Models;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading.Tasks;

namespace MinerBot_2._0.Extensions
{
    public static class ArmorExtensions
    {
        public static string CreateColorArray(this ArmorStyle armor)
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
