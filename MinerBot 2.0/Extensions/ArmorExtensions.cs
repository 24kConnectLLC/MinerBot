using DataAccessLibrary.Armor.Models;
using MinerBot.Shared.Armor;

namespace MinerBot_2._0.Extensions
{
    public static class ArmorExtensions
    {
        // The code lives in MinerBot.Shared, so the dashboard shows the same save-file string the bot posts
        public static string CreateColorArray(this ArmorStyle armor) => ArmorColors.CreateColorArray(armor);
    }
}
