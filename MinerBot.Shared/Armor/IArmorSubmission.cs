namespace MinerBot.Shared.Armor
{
    // What the approval rules read from a submission. The colours are whatever the website stored, which can be outside
    // a byte or missing.
    public interface IArmorSubmission
    {
        string? ArmorName { get; }
        string? ArmorDescription { get; }
        string? ArmorScreenshot { get; }
        string Author { get; }
        int? Helmet { get; }
        int? Visor { get; }
        int? Torso { get; }
        int? Shoulders { get; }
        int? Biceps { get; }
        int? Forearms { get; }
        int? Hands { get; }
        int? Pelvis { get; }
        int? Thighs { get; }
        int? Legs { get; }
        int? Boots { get; }
        int? Shields { get; }
        int? Other { get; }
    }
}
