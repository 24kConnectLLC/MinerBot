using System.Globalization;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Posting.Models;

namespace MinerBot.Dashboard.Servers
{
    // A server's switches on its page: what each one does, the Owner's form, and every answer a save can get, each a fixed
    // text. The switches are the bot's posts of armor, maps and lobbies in that server; announcements have none.
    internal static class SwitchWords
    {
        // The form's handler, its notices' name, the word ?done= carries after a save and the section's anchor
        public const string Handler = "Switches";
        public const string Form = "switches";
        public const string DoneWord = "switches";
        public const string Anchor = "switches";

        public const string Heading = "Switches";

        public const string Help = "A kind with a channel is posted only while its switch is on. MinerBot reads the switches at its next notification run, within 5 minutes, "
            + "and what it found while a kind was off isn't posted when the kind is turned on again. /admin set_channels doesn't change them, and announcements have "
            + "no switch: they go out only when the server's admins use /admin announce.";

        public const string ArmorLabel = "Armor posts";
        public const string MapsLabel = "Map posts";
        public const string LobbiesLabel = "Lobby posts";
        public const string Button = "Save switches";

        public const string NoneSaved = "No switches saved: every kind is on.";

        // The name of whoever saved them, from the server's latest changes, or their role when that row isn't among them
        public static string LastChanged(DateTime at) => $"Last changed {at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC by";

        public static string RoleName(string role) => role == AuditRole.Owner ? "the Owner" : "a server admin";

        public static string OnOff(bool on) => on ? "on" : "off";

        public const string OnlyOwner = "Only the Owner can change switches.";
        public const string NotIn = "Switches can be changed only while MinerBot is in the server.";

        // The line under a kind's mark that's switched off
        public const string SwitchedOffLine = "Switched off on the dashboard, so MinerBot posts none of these here, though a channel is set.";

        // A save's answers: done, shown only while the switches are the ones it saved, and the notices of everything else.
        // Already is nothing to save from a form drawn before the switches last changed, so what it asks for was saved since.
        public const string Saved = "Saved. MinerBot uses these switches from its next notification run, within 5 minutes.";
        public const string Unchanged = "Nothing changed, so nothing was saved.";
        public const string Already = "The switches are already as you chose, so nothing more was done: your earlier try was saved, or someone made the same change first.";
        public const string Stale = "The switches changed after you opened this page, so nothing was saved. The page now shows them as they are: choose again.";
        public const string Unsure = "The dashboard lost touch with its database while saving the switches, so it can't tell whether they were saved. Wait a minute, "
            + "then open the page again to see them. Saving again is safe: a save applies only to the switches you saw.";
        public const string NotInNow = "MinerBot isn't in this server now, so nothing was saved: switches can be changed only while it's in the server.";

        public static string Limit(DateTime after) =>
            $"You've changed switches {SwitchLimits.PerHour} times in the last hour, so nothing was saved. Try again after {after.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC.";

        // The audit log's detail of each refusal
        public const string OnlyOwnerDetail = "Only the Owner can change switches";
        public const string StaleDetail = "The switches had changed since the page showed them";
        public static readonly string LimitDetail = $"Over the limit of {SwitchLimits.PerHour} switch changes an hour";

        // The audit row's keys for the switches that changed, which AuditRows labels
        public const string ArmorKey = "armor_on";
        public const string MapsKey = "maps_on";
        public const string LobbiesKey = "lobbies_on";

        public static bool ArmorOn(GuildSwitches? switches) => switches?.armor_on ?? true;
        public static bool MapsOn(GuildSwitches? switches) => switches?.maps_on ?? true;
        public static bool LobbiesOn(GuildSwitches? switches) => switches?.lobbies_on ?? true;
    }

    // How often one person may save switches: from their own rows in the audit log, so a restart of the dashboard resets
    // nothing
    internal static class SwitchLimits
    {
        public const int PerHour = 30;
        public static readonly TimeSpan Window = TimeSpan.FromHours(1);
    }
}
