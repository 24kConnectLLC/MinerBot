using System.Globalization;
using System.Text.RegularExpressions;

namespace MinerBot.Shared.Steam
{
    // Reads the ways people write a Steam account and turns each into the 32-bit account number the blacklist stores.
    // Only individual accounts in the public universe pass, because only a player can be banned.
    public static class SteamIds
    {
        // [U:1:0] written as a SteamID64: the public universe, an individual account, the desktop instance
        public const ulong IndividualBase = 76561197960265728;

        // A SteamID64 keeps its universe in the top 8 bits, so a smaller number can only be an account number
        const ulong UniverseBits = 1UL << 56;

        const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        static readonly TimeSpan MatchLimit = TimeSpan.FromSeconds(1);

        // [0-9] rather than \d, which also takes other scripts' digits
        static readonly Regex ProfileLink = new(@"^(?:https?://)?(?:www\.)?steamcommunity\.com/profiles/([0-9]{1,20})/?\z", Options, MatchLimit);
        static readonly Regex VanityLink = new(@"^(?:https?://)?(?:www\.)?steamcommunity\.com/id/([a-z0-9_-]{1,32})/?\z", Options, MatchLimit);
        static readonly Regex Steam2 = new(@"^STEAM_[01]:([01]):([0-9]{1,10})\z", Options, MatchLimit);
        static readonly Regex Steam3 = new(@"^\[U:1:([0-9]{1,10})\]\z|^U:1:([0-9]{1,10})\z", Options, MatchLimit);
        static readonly Regex Number = new(@"^[0-9]{1,20}\z", Options, MatchLimit);

        public static SteamIdResult Parse(string? input)
        {
            var text = input?.Trim() ?? "";

            if (text.Length == 0)
                return Invalid("Enter a SteamID, an account number or a Steam profile link");

            var match = ProfileLink.Match(text);
            if (match.Success)
                return FromProfileLink(match.Groups[1].Value);

            match = VanityLink.Match(text);
            if (match.Success)
                return new SteamIdResult(SteamIdKind.NeedsLookup, 0, match.Groups[1].Value, null);

            // STEAM_X:Y:Z is account 2Z + Y, where X is the universe, which older games wrote as 0
            match = Steam2.Match(text);
            if (match.Success)
            {
                if (!uint.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var z) || z > int.MaxValue)
                    return Invalid("That account number is out of range");

                return Account(z * 2 + uint.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            }

            match = Steam3.Match(text);
            if (match.Success)
            {
                var digits = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;

                if (!uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var accountNumber))
                    return Invalid("That account number is out of range");

                return Account(accountNumber);
            }

            if (Number.IsMatch(text))
                return FromNumber(text);

            return Invalid("That isn't a SteamID, an account number or a Steam profile link");
        }

        public static ulong ToSteamId64(uint accountNumber)
        {
            if (accountNumber == 0)
                throw new ArgumentOutOfRangeException(nameof(accountNumber), "Account number 0 isn't a Steam account");

            return IndividualBase + accountNumber;
        }

        public static string ToSteam3(uint accountNumber) => $"[U:1:{accountNumber}]";

        static SteamIdResult FromProfileLink(string digits)
        {
            if (!ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var steamId64) || steamId64 <= uint.MaxValue)
                return Invalid("That profile link doesn't hold a SteamID64");

            return FromSteamId64(steamId64);
        }

        // A bare number up to 32 bits is an account number, and a bigger one a SteamID64
        static SteamIdResult FromNumber(string digits)
        {
            if (!ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                return Invalid("That number is too big to be a Steam account");

            if (value <= uint.MaxValue)
                return Account((uint)value);

            if (value < UniverseBits)
                return Invalid("That account number is out of range");

            return FromSteamId64(value);
        }

        // Every individual account in the public universe lies in this one range, so the same check refuses other
        // universes, other kinds of account such as groups, and other instances
        static SteamIdResult FromSteamId64(ulong steamId64)
        {
            if (steamId64 == IndividualBase)
                return Invalid("That SteamID64 is account 0, which isn't a Steam account");

            if (steamId64 < IndividualBase || steamId64 - IndividualBase > uint.MaxValue)
                return Invalid("That SteamID64 isn't an individual Steam account");

            return Account((uint)(steamId64 - IndividualBase));
        }

        static SteamIdResult Account(uint accountNumber) => accountNumber == 0
            ? Invalid("Account number 0 isn't a Steam account")
            : new SteamIdResult(SteamIdKind.AccountNumber, accountNumber, null, null);

        static SteamIdResult Invalid(string error) => new(SteamIdKind.Invalid, 0, null, error);
    }
}
