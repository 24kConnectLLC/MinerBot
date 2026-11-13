using System.Globalization;
using MinerBot.Shared.Steam;

namespace MinerBot.Dashboard.Blacklist
{
    // The ways the blacklist pages write an account, from the account number the website's table holds, the n in [U:1:n].
    // Account 0 isn't a Steam account, so it has no SteamID64 and no profile: SteamIds.ToSteamId64 throws for it.
    internal static class SteamAccounts
    {
        const string ProfileBase = "https://steamcommunity.com/profiles/";

        public static string? SteamId64(uint account) =>
            account == 0 ? null : SteamIds.ToSteamId64(account).ToString(CultureInfo.InvariantCulture);

        public static string Steam3(uint account) => SteamIds.ToSteam3(account);

        // The old way, STEAM_0:y:z, for an account number of 2z + y
        public static string Steam2(uint account) =>
            string.Create(CultureInfo.InvariantCulture, $"STEAM_0:{account % 2}:{account / 2}");

        public static string? ProfileLink(uint account) => SteamId64(account) is { } steamId64 ? ProfileBase + steamId64 : null;

        // What a list shows for an entry, as its link's text: the SteamID64 players see on Steam, or for account 0 its
        // [U:1:0] form, with why it has no other
        public static string Shown(uint account) => SteamId64(account) ?? Steam3(account) + " (not a Steam account)";

        // The entry's own page, and its target_id in the audit log
        public static string EntryLink(uint account) => "/admin/blacklist/" + TargetId(account);

        public static string TargetId(uint account) => account.ToString(CultureInfo.InvariantCulture);
    }
}
