namespace MinerBot.Shared.Steam
{
    public enum SteamIdKind
    {
        AccountNumber,

        // A custom profile link, which only Steam can turn into an account
        NeedsLookup,

        Invalid
    }

    // AccountNumber is the n in [U:1:n], which is what the blacklist stores. VanityName is set when the input needs a
    // lookup, and Error, in plain English, when it's invalid.
    public sealed record SteamIdResult(SteamIdKind Kind, uint AccountNumber, string? VanityName, string? Error);
}
