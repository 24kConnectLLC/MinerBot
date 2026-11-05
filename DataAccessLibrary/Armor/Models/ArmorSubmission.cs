using MinerBot.Shared.Armor;

namespace DataAccessLibrary.Armor.Models
{
    // Every column of a row in the website's armor table, whatever its status. Dapper converts with overflow checks, so
    // the ID is a long and the colours are int?, and a row that ArmorStyle couldn't read still opens for review.
    public class ArmorSubmission : IArmorSubmission
    {
        public long ID { get; set; }
        public DateTime CreatedAt { get; set; }
        public long? UserID { get; set; }
        public string? Status { get; set; }
        public string Author { get; set; } = "";
        public string? ArmorName { get; set; }
        public string? ArmorDescription { get; set; }

        // Whole from GetSubmission. A list cuts it one character past ArmorRules.ScreenshotLimit.
        public string? ArmorScreenshot { get; set; }
        public string? ArmorChoice { get; set; }
        public string? ArmorSkin { get; set; }
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
        public string? ColorArray { get; set; }
    }
}
