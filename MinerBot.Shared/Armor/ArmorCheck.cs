namespace MinerBot.Shared.Armor
{
    public enum ArmorProblemKind
    {
        // Blocking
        NameEmpty,
        NameHasBacktick,
        NameHasLineBreak,
        NameTaken,
        NameTooLong,
        NameTooLongForDiscord,
        AuthorTooLong,
        DescriptionTooLong,
        LookupTooLongForDiscord,
        ColorOutOfRange,

        // Warnings
        NoScreenshot,
        ScreenshotNotAllowed,
        HiddenCharacters,
        DirectionCharacters,
        MentionSyntax,
        LinkSyntax,
        Backtick
    }

    public enum ArmorField
    {
        Name,
        Description,
        Author,
        Screenshot,
        Colors
    }

    public sealed record ArmorProblem(ArmorProblemKind Kind, ArmorField Field, string Message);

    // Blocking problems stop an approval. Warnings don't, but the reviewer has to look at each one.
    public sealed record ArmorCheck(IReadOnlyList<ArmorProblem> Blocking, IReadOnlyList<ArmorProblem> Warnings)
    {
        public bool CanApprove => Blocking.Count == 0;
    }
}
