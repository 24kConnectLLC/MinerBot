using Steam.Models;

namespace SteamWebAPIAccess
{
    // What the bot may do with a Workshop item that Steam described
    public enum WorkshopItemCheck
    {
        Shown,

        // Removed, banned, private, friends-only, or no details at all. One answer for all of them, so the bot never
        // confirms that a private map exists.
        NotFound,

        OtherGame
    }

    // One set of checks for the latest maps and for /maps lookup, so a map removed or hidden on Steam can't reach either
    public static class WorkshopItems
    {
        // Steam's EResult.OK. A removed or unknown item comes back as a placeholder with another result and no other fields.
        const uint ResultOK = 1;

        // Steam's unlisted visibility, which Steam.Models names Unknown
        public const PublishedFileVisibility Unlisted = (PublishedFileVisibility)3;

        // An unlisted map is meant to be shared by its link, so a lookup can show one, but the latest maps only list public ones
        public static WorkshopItemCheck Check(PublishedFileDetailsModel? item, uint appID, bool unlistedAllowed)
        {
            if (item == null || item.Result != ResultOK || item.Banned)
                return WorkshopItemCheck.NotFound;

            if (item.Visibility != PublishedFileVisibility.Public && !(unlistedAllowed && item.Visibility == Unlisted))
                return WorkshopItemCheck.NotFound;

            // After the visibility, so another game's private item isn't confirmed either
            return item.ConsumerAppId == appID ? WorkshopItemCheck.Shown : WorkshopItemCheck.OtherGame;
        }
    }
}
