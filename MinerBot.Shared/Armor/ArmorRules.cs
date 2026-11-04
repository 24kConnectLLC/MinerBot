using MinerBot.Shared.Discord;
using MinerBot.Shared.Text;
using System.Text;

namespace MinerBot.Shared.Armor
{
    // What a submission has to pass before it can be approved, and what a reviewer has to look at first. A blocking
    // problem would break a post or a command in every server, or makes the row impossible to save. A warning needs a tick.
    public static class ArmorRules
    {
        // What the website's table holds, counted in code points the way MariaDB counts them. Its strict mode makes a
        // longer value fail the save instead of cutting it.
        public const int NameLimit = 60;
        public const int AuthorLimit = 60;
        public const int DescriptionLimit = 256;

        // Every screenshot was an upload on the website when this was written
        public static readonly IReadOnlyList<string> ScreenshotHosts = ["murderminershub.com", "www.murderminershub.com"];

        // The longest screenshot address that's looked at. An upload's address is far shorter, and the website's column has
        // no limit, so a longer one is refused before it's read through.
        public const int ScreenshotLimit = 2048;

        // The characters Unicode always breaks a line at
        static readonly char[] LineBreaks = ['\n', '\v', '\f', '\r', '\u0085', '\u2028', '\u2029'];

        static readonly (string Part, Func<IArmorSubmission, int?> Color)[] Colors =
        [
            ("helmet", armor => armor.Helmet),
            ("visor", armor => armor.Visor),
            ("torso", armor => armor.Torso),
            ("shoulders", armor => armor.Shoulders),
            ("biceps", armor => armor.Biceps),
            ("forearms", armor => armor.Forearms),
            ("hands", armor => armor.Hands),
            ("pelvis", armor => armor.Pelvis),
            ("thighs", armor => armor.Thighs),
            ("legs", armor => armor.Legs),
            ("boots", armor => armor.Boots),
            ("shields", armor => armor.Shields),
            ("other", armor => armor.Other)
        ];

        // nameTaken says whether another approved armor has the same name, which only the database can tell
        public static ArmorCheck Check(IArmorSubmission armor, bool nameTaken)
        {
            var blocking = new List<ArmorProblem>();
            var warnings = new List<ArmorProblem>();

            CheckName(armor.ArmorName, nameTaken, blocking);
            CheckSizes(armor, blocking);
            CheckColors(armor, blocking);

            CheckScreenshot(armor.ArmorScreenshot, warnings);
            CheckText(ArmorField.Name, "name", armor.ArmorName, warnings);
            CheckText(ArmorField.Description, "description", armor.ArmorDescription, warnings);
            CheckText(ArmorField.Author, "author", armor.Author, warnings);

            return new ArmorCheck(blocking, warnings);
        }

        // The review page shows the screenshot and the bot fetches it, so it has to be an https address on the website
        // itself. Whitespace anywhere doesn't pass either, because the bot takes spaces out of the address before fetching.
        public static bool IsAllowedScreenshot(string? address)
        {
            if (string.IsNullOrEmpty(address) || address.Length > ScreenshotLimit || address.Any(char.IsWhiteSpace) || TextInspector.HasHidden(address))
                return false;

            return Uri.TryCreate(address, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && uri.IsDefaultPort
                && uri.UserInfo.Length == 0
                && ScreenshotHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase);
        }

        static void CheckName(string? name, bool nameTaken, List<ArmorProblem> blocking)
        {
            if (IsBlank(name))
                blocking.Add(Problem(ArmorProblemKind.NameEmpty, ArmorField.Name, "The armor has no name, so /armor list and /armor lookup can't show it"));

            if (nameTaken)
                blocking.Add(Problem(ArmorProblemKind.NameTaken, ArmorField.Name,
                    "An approved armor already has this name, ignoring capitals and spaces around it, so /armor lookup could only ever find one of them"));

            if (name == null)
                return;

            // The new-armor post lists the names inside a code block, in every server that has armor posts
            if (name.Contains('`'))
                blocking.Add(Problem(ArmorProblemKind.NameHasBacktick, ArmorField.Name,
                    "The name has a backtick (`), which would end the code block that the new-armor post lists names in"));

            if (name.IndexOfAny(LineBreaks) >= 0)
                blocking.Add(Problem(ArmorProblemKind.NameHasLineBreak, ArmorField.Name,
                    "The name has a line break, which would split it across lines of the new-armor post and of /armor list"));

            // This can happen inside the table's limit, because an emoji outside the Basic Multilingual Plane counts twice.
            // Discord.Net refuses a suggestion that's too long, so every /armor lookup search that would suggest it fails.
            var discordLimit = Math.Min(DiscordLimits.AutocompleteChoiceName, DiscordLimits.EmbedTitle);

            if (name.Length > discordLimit)
                blocking.Add(Problem(ArmorProblemKind.NameTooLongForDiscord, ArmorField.Name,
                    $"The name takes {name.Length} of Discord's characters, and a suggestion in /armor lookup holds at most {discordLimit}, so searches that would suggest it fail"));
        }

        static void CheckSizes(IArmorSubmission armor, List<ArmorProblem> blocking)
        {
            TooLong(ArmorProblemKind.NameTooLong, ArmorField.Name, "name", armor.ArmorName, NameLimit, blocking);
            TooLong(ArmorProblemKind.AuthorTooLong, ArmorField.Author, "author", armor.Author, AuthorLimit, blocking);
            TooLong(ArmorProblemKind.DescriptionTooLong, ArmorField.Description, "description", armor.ArmorDescription, DescriptionLimit, blocking);

            // The way /armor lookup's card writes them
            var lookup = $"```\n{armor.ArmorDescription}\nMade by {armor.Author}```";

            if (lookup.Length > DiscordLimits.EmbedDescription)
                blocking.Add(Problem(ArmorProblemKind.LookupTooLongForDiscord, ArmorField.Description,
                    $"The description and author take {lookup.Length} of Discord's characters on /armor lookup's card, which holds at most {DiscordLimits.EmbedDescription}"));
        }

        static void TooLong(ArmorProblemKind kind, ArmorField field, string fieldName, string? text, int limit, List<ArmorProblem> blocking)
        {
            var length = text == null ? 0 : CodePoints(text);

            if (length > limit)
                blocking.Add(Problem(kind, field,
                    $"The {fieldName} is {length} characters long, and the website's table holds at most {limit}, so saving it would fail"));
        }

        // Reading an approved armor turns each colour into a byte with an overflow check, so one colour outside a byte
        // stops the armor posts, the colour job and /armor list for every server
        static void CheckColors(IArmorSubmission armor, List<ArmorProblem> blocking)
        {
            var outside = Colors
                .Select(part => (part.Part, Color: part.Color(armor)))
                .Where(part => part.Color is < 0 or > 255)
                .Select(part => $"{part.Part} {part.Color}")
                .ToList();

            if (outside.Count > 0)
                blocking.Add(Problem(ArmorProblemKind.ColorOutOfRange, ArmorField.Colors,
                    $"Colours have to be from 0 to 255, and these aren't: {string.Join(", ", outside)}. The bot couldn't read the armor, which would stop its armor posts and /armor list"));
        }

        static void CheckScreenshot(string? address, List<ArmorProblem> warnings)
        {
            if (string.IsNullOrWhiteSpace(address))
                warnings.Add(Problem(ArmorProblemKind.NoScreenshot, ArmorField.Screenshot,
                    "There's no screenshot, so you can't see the armor before approving it, and /armor lookup shows no picture"));
            else if (address.Length > ScreenshotLimit)
                warnings.Add(Problem(ArmorProblemKind.ScreenshotNotAllowed, ArmorField.Screenshot,
                    $"The screenshot's address is over {ScreenshotLimit} characters long, far longer than an upload's, so the review page won't show it and the bot shouldn't fetch it"));
            else if (!IsAllowedScreenshot(address))
                warnings.Add(Problem(ArmorProblemKind.ScreenshotNotAllowed, ArmorField.Screenshot,
                    $"The screenshot isn't an https address on {string.Join(" or ", ScreenshotHosts)}, so the review page won't show it and the bot shouldn't fetch it"));
        }

        static void CheckText(ArmorField field, string fieldName, string? text, List<ArmorProblem> warnings)
        {
            var findings = TextInspector.Inspect(text);

            var hidden = findings.Hidden.Where(character => !character.ChangesDirection).ToList();
            if (hidden.Count > 0)
                warnings.Add(Problem(ArmorProblemKind.HiddenCharacters, field,
                    $"The {fieldName} has characters that show nothing: {Describe(hidden)}"));

            var direction = findings.Hidden.Where(character => character.ChangesDirection).ToList();
            if (direction.Count > 0)
                warnings.Add(Problem(ArmorProblemKind.DirectionCharacters, field,
                    $"The {fieldName} has characters that change the direction of the text after them, so it may read differently from how it's stored: {Describe(direction)}"));

            if (findings.Mentions.Count > 0)
                warnings.Add(Problem(ArmorProblemKind.MentionSyntax, field,
                    $"The {fieldName} has Discord's mention syntax: {Quote(findings.Mentions)}"));

            if (findings.Links.Count > 0)
                warnings.Add(Problem(ArmorProblemKind.LinkSyntax, field,
                    $"The {fieldName} has a link: {Quote(findings.Links)}"));

            // The name's backtick is blocking above. /armor lookup shows the description and the author inside a code block.
            if (field != ArmorField.Name && text?.Contains('`') == true)
                warnings.Add(Problem(ArmorProblemKind.Backtick, field,
                    $"The {fieldName} has a backtick (`), which can end the code block that /armor lookup shows it in"));
        }

        static ArmorProblem Problem(ArmorProblemKind kind, ArmorField field, string message) => new(kind, field, message);

        // Nothing but spaces and characters that show nothing
        static bool IsBlank(string? text) => text == null || text.EnumerateRunes().All(rune => Rune.IsWhiteSpace(rune) || TextInspector.IsHidden(rune.Value));

        static int CodePoints(string text) => text.EnumerateRunes().Count();

        static string Describe(IEnumerable<HiddenCharacter> characters) =>
            string.Join(", ", characters.DistinctBy(character => character.CodePoint).Select(character => $"{character.Name} ({character.Code})"));

        static string Quote(IEnumerable<TextMatch> matches) =>
            string.Join(", ", matches.Select(match => TextInspector.Reveal(match.Text)).Distinct());
    }
}
