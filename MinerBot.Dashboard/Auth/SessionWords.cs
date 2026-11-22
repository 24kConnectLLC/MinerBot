using System.Globalization;
using DataAccessLibrary.Dashboard.Models;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Status;

namespace MinerBot.Dashboard.Auth
{
    // One session as the Sessions page lists it: its hash in hex, which names it to end it and signs nobody in, who it is,
    // their role on the lists now, when they signed in and whether that's within the last 15 minutes, when it was last used
    // and when it ends, where from and with what browser, as that browser sent them, and whether it's this one
    public sealed record SessionView(string Hash, ulong UserId, string UserName, string Role, DateTime SignedIn, bool Fresh, DateTime LastUsed, DateTime Ends,
        string? Ip, string? Browser, bool IsCurrent)
    {
        public string UserIdText => UserId.ToString(CultureInfo.InvariantCulture);
    }

    // The Sessions and security page's fixed words. A session's name and browser are what its visitor's Discord and browser
    // sent, so the page shows them through RevealedText; nothing here repeats them, apart from the ended session's name and
    // ID in its audit row, never its address or browser, since Admins read the audit log.
    internal static class SessionWords
    {
        public const string Title = "Sessions and security";

        public const string Intro = "Sessions in use now: signed in within the last 12 hours and used within the last 30 minutes. Addresses and browsers are "
            + "as each visitor's browser sent them, and only the Owner sees them.";

        public const string ThisSession = "This session: use Sign out";

        public const string EndHelp = "Ending a session signs that browser out. Anyone still on the lists can sign in again; to stop someone, take them off with "
            + "./setup-dashboard.sh, which ends their sessions at their next click.";

        public const string EndOthers = "End every other session";

        // A form's handler, as Razor Pages names it
        public const string End = "End";
        public const string EndOthersHandler = "EndOthers";

        // What ?done= says, from a fixed set, or null for a word that isn't one
        public static string? Done(string? done) => done switch
        {
            "ended" => "That session was ended: its browser is signed out at its next click.",
            "gone" => "That session had already ended, so nothing more was done.",
            "ended-others" => "Every other session was ended. Their browsers are signed out at their next click.",
            _ => null
        };

        public const string YourOwn = "That's this session: use Sign out to end it.";

        public static string NotFresh(DateTimeOffset? signedInAt) => (signedInAt is { } at
                ? $"Ending a session needs a sign-in from the last 15 minutes, and yours is from {RequestWords.Clock(at.UtcDateTime)}, so nothing was ended."
                : "Ending a session needs a sign-in from the last 15 minutes, and the dashboard can't tell when yours was, so nothing was ended.")
            + " Sign in again with Discord, then end it again.";

        // The page's sign-in line, while it's fresh and while it isn't
        public static string Fresh(DateTimeOffset signedInAt, DateTimeOffset until) =>
            $"Your sign-in is from {RequestWords.Clock(signedInAt.UtcDateTime)}, so ending sessions works until {RequestWords.Clock(until.UtcDateTime)}.";

        public static string NeedsFresh(DateTimeOffset? signedInAt) => signedInAt is { } at
            ? $"Ending sessions needs a sign-in from the last 15 minutes, and yours is from {RequestWords.Clock(at.UtcDateTime)}."
            : "Ending sessions needs a sign-in from the last 15 minutes, and the dashboard can't tell when yours was.";

        // Under the way to sign in again while requests to the bot are off, when a sign-in sends the owner no message
        public const string SignInAgainNoMessage = "It brings you back here. With requests to the bot off, it sends no sign-in message.";

        public static string Role(DashboardRole role) => role switch
        {
            DashboardRole.Owner => "Owner",
            DashboardRole.Admin => "Admin",
            _ => "No longer on the lists: ends at its next click"
        };

        // The audit rows' details: the ended session's person and sign-in, and how many ended of whom
        public static string Ended(SessionListRow session) =>
            $"The session of {session.user_name} (Discord ID {session.user_id.ToString(CultureInfo.InvariantCulture)}), signed in at {TimeWords.Utc(session.created_at)}.";

        public static string EndedOthers(IReadOnlyList<SessionListRow> sessions)
        {
            var people = sessions.Select(session => session.user_id).Distinct().Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList();
            return $"{TimeWords.Count(sessions.Count, "session")}, of Discord {(people.Count == 1 ? "user" : "users")} {string.Join(", ", people)}";
        }

        public static string Checked(DateTime now) => $"Checked when this page was drawn, at {RequestWords.Clock(now)}: reload it to check again.";
    }
}
