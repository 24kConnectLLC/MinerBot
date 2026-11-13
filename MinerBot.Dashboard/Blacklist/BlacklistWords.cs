using DataAccessLibrary.Blacklist;

namespace MinerBot.Dashboard.Blacklist
{
    // A text field of the blacklist's forms, for its messages
    public enum BlacklistField
    {
        SteamId,
        Name,
        Reason,
        Expires,
        Notes
    }

    // The blacklist pages' fixed text. A message is picked from a fixed set and never repeats what a request sent.
    internal static class BlacklistWords
    {
        // The list's banner. Nothing in the bot or on the website reads the blacklist yet.
        public const string NotEnforced = "The blacklist isn't enforced yet: nothing checks it, because that waits for the game's API. Entries saved here are kept for then.";

        // What an entry's page says after an action, while the entry is as the action left it
        public const string Added = "Added to the blacklist. It isn't enforced yet, so nothing changes in the game for now.";
        public const string Saved = "Saved.";
        public const string Removed = "Removed from the blacklist. Its history stays below.";

        // Refusals, which say nothing was done, and the tries that find their own change already made
        public const string Changed = "This entry changed after you opened it, so nothing was done. The page now shows it as it is: look it over again before you change it.";
        public const string RemoveChanged = "This entry changed after you opened it, so nothing was done. The page now shows it as it is: look it over again before you remove it.";
        public const string GoneSinceOpened = "This entry was removed after you opened it, so nothing was saved. Add it again if it's still needed.";
        public const string AlreadyRemoved = "This entry was already removed, so nothing more was done: your earlier try was saved, or someone removed it first.";
        public const string AlreadySaved = "This entry already has these details, so nothing more was done: your earlier try was saved, or someone made the same change first.";
        public const string Unchanged = "Nothing was saved: the entry already has these details.";

        // The page links its entry after this, with the words "Open its entry to change it"
        public const string Exists = "This account is already on the blacklist, so nothing was added.";
        public const string AlreadyAdded = "This account is already on the blacklist with these details, so nothing more was done: your earlier try was saved, or someone added it first.";
        public const string Unsure = "The dashboard lost touch with its database while it was saving this, so it can't tell whether the change was saved. Wait a minute, then open the entry again to see it. Trying again is safe: a change only applies while the entry is as you saw it, and if this one was saved, the page says so.";

        // An add the database didn't confirm. The entry may not exist, so the page links a search for the account, which
        // finds it either way.
        public const string AddUnsure = "The dashboard lost touch with its database while it was adding this account, so it can't tell whether it was added. Wait a minute, then search the blacklist for the account. Adding it again is safe: an account already on the blacklist isn't added twice.";

        // A form whose fields failed their checks, above the messages by each field, which can be far down the page
        public const string NotAdded = "Nothing was added. Correct what's marked below, then add it again.";
        public const string NotSaved = "Nothing was saved. Correct what's marked below, then save again.";

        // The rows whose number no account can be, which only something other than the dashboard can type in
        public const string NumbersIntro = "These rows hold a number that can't be a Steam account number, such as a SteamID64 cut to ten digits, so no list or search shows them and nobody can match them. Remove each one, then add the account with its real SteamID if it's still needed.";
        public const string NumberRemoved = "The row whose number wasn't a Steam account number was removed.";
        public const string NumberChanged = "That row changed after the page showed it, so nothing was done. The list now shows it as it is: look it over again before you remove it.";
        public const string NumberAlreadyRemoved = "That row was already removed, so nothing more was done: your earlier try was saved, or someone removed it first.";
        public const string NumberUnsure = "The dashboard lost touch with its database while it was removing that row, so it can't tell whether it was removed. Wait a minute, then open the blacklist again to see. Trying again is safe: a row is only removed while it's as you saw it.";

        // Where to get a custom profile link's number: MinerBot's own command, which takes the link only written exactly
        // this way, since it cuts https://steamcommunity.com/id/ off the front to find the name
        public const string CustomLinkLookup = "MinerBot's /more steam_id command in Discord gives the SteamID64 of a custom link written as https://steamcommunity.com/id/ and the name.";

        // A custom profile link, which only Steam can turn into an account. The name is never repeated.
        public const string CustomLink = "Custom profile links, steamcommunity.com/id/ and a name, can't be looked up here yet. Enter the profile's number link instead, steamcommunity.com/profiles/ and 17 digits, or its SteamID64. " + CustomLinkLookup;

        // The list's search for a custom link, which looks for it in names only
        public const string CustomLinkSearch = "Custom profile links can't be looked up here yet, so this searched for it in names only. " + CustomLinkLookup + " Search for that instead.";

        public const string UnreadableDate = "The stored end date isn't a real date. Saving the form replaces it with what's in the date box, and an empty box means no end.";

        public const string SteamIdHint = "A SteamID64, a profile link with its number, [U:1:n], STEAM_0:y:z, or the account number.";
        public const string ExpiresHint = "In UTC. From 00:00 UTC that day the entry no longer applies. Leave it empty for no end.";
        public static readonly string NameHint = $"Optional, at most {BlacklistData.UsernameLimit} characters.";
        public static readonly string ReasonHint = $"Optional, at most {BlacklistData.ReasonLimit} characters.";
        public static readonly string NotesHint = $"Optional, at most {BlacklistData.NotesLimit} characters.";

        public const string AddHelp = "Adds the account to the blacklist, which isn't enforced yet, and records it in the audit log.";
        public const string EditHelp = "Saves the name, reason, end date and notes. The account itself can't change: for a wrong number, remove this entry and add the right one.";
        public const string RemoveHelp = "Takes the account off the blacklist. Its history stays on this page and in the audit log.";
        public const string RemoveNumberHelp = "Removes this row from the website's table.";

        public const string BadDate = "Enter the end date as a day from 2000 to 9998, or leave it empty for no end.";

        // What a field's check found, in words for that field
        public static string Problem(BlacklistField field, TextProblem problem) => problem switch
        {
            TextProblem.TooLong => field switch
            {
                BlacklistField.SteamId => $"The SteamID box holds at most {BlacklistForm.SteamIdLimit} characters.",
                BlacklistField.Name => $"The name holds at most {BlacklistData.UsernameLimit} characters.",
                BlacklistField.Reason => $"The reason holds at most {BlacklistData.ReasonLimit} characters.",
                _ => $"The notes hold at most {BlacklistData.NotesLimit} characters."
            },
            _ => field switch
            {
                BlacklistField.Name => "The name can't hold a line break or another control character.",
                BlacklistField.Reason => "The reason can't hold a line break or another control character.",
                _ => "The notes can't hold a line break or another control character."
            }
        };
    }
}
