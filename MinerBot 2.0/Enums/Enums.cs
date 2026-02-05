using Discord.Interactions;

namespace MinerBot_2._0;

public enum HelmentType
{
    All,
    Spaceman,
    Soldier,
    Grunt,
    Miner,
    Bug,
    Ninja,
    Vertical,
    Mohawk,
    [ChoiceDisplay("Tri-Clops")]
    Tri_Clops,
    Shades,
    [ChoiceDisplay("T-Visor")]
    T_Visor,
    Pumpkin,
    Snowman,
    Present,
    Rhino,
    Spiral,
    Sentry,
    Eyeball,
    Jegg,
    Goblin,
    Samurai,
    Fancy,
    [ChoiceDisplay("Bëta Taester / Fake Tester")]
    Beta_Taester,
    Assassin,
    Tester,
    Zombie
}

enum ColorChoices
{
    Helmet,
    Visor,
    Torso,
    Shoulders,
    Biceps,
    Forearms,
    Hands,
    Pelvis,
    Thighs,
    Legs,
    Boots,
    Shields,
    Other,
    NUM_CHOICES
}