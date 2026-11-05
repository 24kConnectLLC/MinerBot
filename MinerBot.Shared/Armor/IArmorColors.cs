namespace MinerBot.Shared.Armor
{
    // The colour of each of an armor's 13 parts, as the game stores them
    public interface IArmorColors
    {
        byte Helmet { get; }
        byte Visor { get; }
        byte Torso { get; }
        byte Shoulders { get; }
        byte Biceps { get; }
        byte Forearms { get; }
        byte Hands { get; }
        byte Pelvis { get; }
        byte Thighs { get; }
        byte Legs { get; }
        byte Boots { get; }
        byte Shields { get; }
        byte Other { get; }
    }
}
