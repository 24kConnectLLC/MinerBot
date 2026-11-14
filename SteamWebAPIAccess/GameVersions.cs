namespace SteamWebAPIAccess
{
    // How a build lays out its lobby metadata: which bools, which property indexes and which map name
    public enum LobbyLayout
    {
        Update39,              // NetSettingType1, older property indexes, short map name
        Update40,              // NetSettingType2, older property indexes, short map name
        Update41BetaShortName, // NetSettingType3, older property indexes, short map name
        Update41Beta,          // NetSettingType3, older property indexes, long map name
        Latest                 // NetSettingType, Mission, Invasion and BTS properties, long map name
    }

    // A version the bot knows. Branch is set on a branch's own version, which gets its own heading in the lobby list.
    // ModesTrusted is off where the layout isn't known to be exact for that build, because its modes would be guesses.
    // Notify is on for every version, because every lobby the list shows is announced.
    public sealed record GameVersion(uint Number, LobbyLayout Layout, bool ModesTrusted, string? Branch = null,
        bool HasChristmasFlag = false, bool Notify = true)
    {
        // The game refuses a version mismatch, so a heading says which version its Join links are for
        public string? Heading => Branch == null ? null : $"{Branch} (v{Number})";
    }

    // Everything the bot knows about the game's versions, and every version rule reads it. A lobby on a version that
    // isn't here is listed under Other versions with Modes Unknown, and the owner is told, so the table can be updated.
    public static class GameVersions
    {
        // A const, so it can be a parameter's default value
        public const uint Current = 105;

        public const string OtherVersionsHeading = "Other versions";

        // The branches first, in the order of the lobby list, then the other versions the bot knows.
        // The layouts are exact for 87, 92, 101, 102, 105 and 200, whose lobbies were generated from the game's source.
        public static IReadOnlyList<GameVersion> All { get; } =
        [
            new(105, LobbyLayout.Latest, ModesTrusted: true, Branch: "Update 41"),
            new(92, LobbyLayout.Update40, ModesTrusted: true, Branch: "Update 40"),

            // update39 branch
            new(87, LobbyLayout.Update39, ModesTrusted: true, Branch: "Update 39", HasChristmasFlag: true),

            // Frozen branches. They decode with the Update 39 layout, which is already wrong at the closest build known, 82.
            // classic_xboxone branch
            new(80, LobbyLayout.Update39, ModesTrusted: false, Branch: "Update 35 (Xbox One Branch)"),
            // update29 branch, only exists for legacy speedrun.com leaderboard
            new(61, LobbyLayout.Update39, ModesTrusted: false, Branch: "Update 29"),
            // classic_xbox360 branch
            new(53, LobbyLayout.Update39, ModesTrusted: false, Branch: "Update 19 (Xbox 360 Branch)"),
            // anniversary_atest2 branch
            new(0, LobbyLayout.Update39, ModesTrusted: false, Branch: "2012 Alpha"),

            // egghunt branch, which has Update 39's layout
            new(39, LobbyLayout.Update39, ModesTrusted: true, Branch: "Legacy Egg Hunt Branch", HasChristmasFlag: true),

            // The first builds of 104 don't have this layout exactly
            new(104, LobbyLayout.Latest, ModesTrusted: false),

            // Update 41 betas with the long map name, exact for 101 and 102. The audit puts 103 in the same range, so it's
            // trusted too.
            new(103, LobbyLayout.Update41Beta, ModesTrusted: true),
            new(102, LobbyLayout.Update41Beta, ModesTrusted: true),
            new(101, LobbyLayout.Update41Beta, ModesTrusted: true),

            // Not newer, it was a typo on an Update 41 branch that still has the older layout
            new(200, LobbyLayout.Update41Beta, ModesTrusted: true),

            // Update 41 betas. 93 to 97 moved their bools within one version number, and 98 to 100 differ at one bit.
            new(100, LobbyLayout.Update41BetaShortName, ModesTrusted: false),
            new(99, LobbyLayout.Update41BetaShortName, ModesTrusted: false),
            new(98, LobbyLayout.Update41BetaShortName, ModesTrusted: false),
            new(97, LobbyLayout.Update41BetaShortName, ModesTrusted: false),
            new(96, LobbyLayout.Update41BetaShortName, ModesTrusted: false),
            new(95, LobbyLayout.Update41BetaShortName, ModesTrusted: false),
            new(94, LobbyLayout.Update41BetaShortName, ModesTrusted: false),
            new(93, LobbyLayout.Update41BetaShortName, ModesTrusted: false),

            // Update 40.1 and Update 39 betas, which moved their bools within one version number. A version 90 lobby the
            // game showed as Murder Match decodes as Free-Build 1-Life.
            new(91, LobbyLayout.Update40, ModesTrusted: false),
            new(90, LobbyLayout.Update40, ModesTrusted: false),
            new(89, LobbyLayout.Update39, ModesTrusted: false),
            new(88, LobbyLayout.Update39, ModesTrusted: false)
        ];

        // After All, because static fields are set in the order they're written
        static readonly Dictionary<uint, GameVersion> byNumber = All.ToDictionary(version => version.Number);

        // The versions with a heading of their own, in the order of the lobby list
        public static IReadOnlyList<GameVersion> Branches { get; } = All.Where(version => version.Branch != null).ToList();

        public static GameVersion? Find(uint version) => byNumber.GetValueOrDefault(version);

        public static bool IsKnown(uint version) => byNumber.ContainsKey(version);

        // A version the bot doesn't know keeps the layout of its range, so its map name still decodes
        public static LobbyLayout LayoutOf(uint version) => Find(version)?.Layout ?? RangeLayout(version);

        public static bool ModesTrusted(uint version) => Find(version)?.ModesTrusted ?? false;

        public static bool Notifies(uint version) => Find(version)?.Notify ?? true;

        // Update 39's flag for its Christmas playlist, whose bit later builds use for other settings
        public static bool HasChristmasFlag(uint version) => Find(version)?.HasChristmasFlag ?? false;

        // The layouts by version range, for the versions that aren't in the table. 42069, the version of a lobby without
        // one, falls in the latest, which the decoder's tests rely on.
        internal static LobbyLayout RangeLayout(uint version) => version switch
        {
            < 90 => LobbyLayout.Update39,
            < 93 => LobbyLayout.Update40,
            < 101 => LobbyLayout.Update41BetaShortName,
            < 104 or 200 => LobbyLayout.Update41Beta,
            _ => LobbyLayout.Latest
        };

        // Builds before 101 pack a short upper case name into ZMapName0 and ZMapName1 only
        internal static bool HasLongMapName(LobbyLayout layout) => layout is LobbyLayout.Update41Beta or LobbyLayout.Latest;
    }
}
