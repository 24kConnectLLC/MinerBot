using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DataAccessLibrary.Armor.Models;
using MinerBot.Shared.Armor;

namespace MinerBot.Dashboard.Armor
{
    // Everything the review page shows of an armor's submission, as one short value its forms send back. An action applies
    // only while the row still gives the same value, so armor the website changed after the reviewer opened it is a
    // conflict, never a decision about something they didn't see. The status is checked on its own.
    internal static class ArmorVersion
    {
        public const int Length = 64;

        public static string Of(ArmorSubmission armor)
        {
            var shown = new StringBuilder();

            foreach (var text in new[] { armor.ArmorName, armor.ArmorDescription, armor.Author, armor.ArmorScreenshot, armor.ArmorChoice, armor.ArmorSkin })
                Add(shown, text);

            foreach (var color in new[] { armor.Helmet, armor.Visor, armor.Torso, armor.Shoulders, armor.Biceps, armor.Forearms, armor.Hands,
                armor.Pelvis, armor.Thighs, armor.Legs, armor.Boots, armor.Shields, armor.Other })
                Add(shown, color?.ToString(CultureInfo.InvariantCulture));

            // The save string as the page shows it, rather than the stored one, so the bot's hourly job filling in the same
            // string for approved armor isn't a change
            Add(shown, armor.ColorArray ?? ArmorColors.CreateColorArray(armor));

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(shown.ToString())));
        }

        // Exactly what Of makes: 64 hex digits in small letters
        public static bool IsWellFormed(string? version) => version is { Length: Length } && version.All(char.IsAsciiHexDigitLower);

        // Each value with its length in front, so no two rows run together into the same text
        static void Add(StringBuilder shown, string? value)
        {
            if (value == null)
                shown.Append("-;");
            else
                shown.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
        }
    }
}
