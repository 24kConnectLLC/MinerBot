using DataAccessLibrary.Armor;
using MinerBot.Shared.Armor;

namespace MinerBot.Dashboard.Armor
{
    // The armor pages' fixed text: what each problem is called on the list, and what the review page says after an
    // action. A message is picked from a fixed set and never repeats what a request sent.
    internal static class ArmorWords
    {
        public static string Label(ArmorProblemKind kind) => kind switch
        {
            ArmorProblemKind.NameEmpty => "No name",
            ArmorProblemKind.NameHasBacktick => "Backtick in the name",
            ArmorProblemKind.NameHasLineBreak => "Line break in the name",
            ArmorProblemKind.NameTaken => "Name already approved",
            ArmorProblemKind.NameTooLong => "Name too long",
            ArmorProblemKind.NameTooLongForDiscord => "Name too long for Discord",
            ArmorProblemKind.AuthorTooLong => "Author too long",
            ArmorProblemKind.DescriptionTooLong => "Description too long",
            ArmorProblemKind.LookupTooLongForDiscord => "Card too long for Discord",
            ArmorProblemKind.ColorOutOfRange => "Colour out of range",
            ArmorProblemKind.IdTooBigForTheBot => "ID too big for the bot",
            ArmorProblemKind.ScreenshotNotAllowed => "Screenshot not an upload",
            ArmorProblemKind.StyleHasBacktick => "Backtick in the helmet or skin",
            ArmorProblemKind.StyleHasLineBreak => "Line break in the helmet or skin",
            ArmorProblemKind.SaveStringDiffers => "Save string not the colours'",
            ArmorProblemKind.NoScreenshot => "No screenshot",
            ArmorProblemKind.HiddenCharacters => "Hidden characters",
            ArmorProblemKind.DirectionCharacters => "Right-to-left characters",
            ArmorProblemKind.MentionSyntax => "Mention",
            ArmorProblemKind.LinkSyntax => "Link",
            ArmorProblemKind.Backtick => "Backtick",
            _ => kind.ToString()
        };

        public static string Field(ArmorField field) => field switch
        {
            ArmorField.Id => "ID",
            ArmorField.Name => "name",
            ArmorField.Description => "description",
            ArmorField.Author => "author",
            ArmorField.Screenshot => "screenshot",
            ArmorField.Colors => "colours",
            ArmorField.Helmet => "helmet",
            ArmorField.Skin => "skin",
            ArmorField.SaveString => "save string",
            _ => field.ToString()
        };

        // Why an approval may bring no post: the bot posts an armor only the first time it sees it approved, and remembers
        // every armor it has seen approved until it stops, taken down or not
        const string PostedOnce = "it posts an armor only the first time it sees it approved since it last started, and this one was approved before, or may have been";

        // When a first approval brings no post either: the bot's first check since it started only notes the approved armor it
        // finds, as Announce now's line then says, so each promise of a post allows for it
        const string NotAtTheFirstCheck = "; if that's the bot's first check since it started, it won't post this one";

        // What the page says after an action, while the armor still has the status the action gave it. What the bot does
        // next comes from its five-minute post, its name cache and the lookup. An approval, a take-down and a move back from
        // Approved ask the bot to clear its name cache, and the line after these says how that went.
        public const string Approved = "Approved. The bot posts new armor at its next five-minute check, in every server with armor posts turned on, together with any other armor approved by then" + NotAtTheFirstCheck + ". /armor lookup finds it by its exact name now.";
        public const string ApprovedAgain = "Approved. /armor lookup finds it by its exact name now. The bot may not post it: " + PostedOnce + ".";
        public const string Rejected = "Rejected. The bot doesn't show it.";
        public const string RejectedWithReason = "Rejected. The bot doesn't show it, and the reason is kept in the history below.";
        public const string TakenDown = "Taken down. /armor lookup stops finding it now. A post the bot already made stays in Discord.";
        public const string MovedBackToPending = "Moved back to Pending. If the bot already posted it, that post stays in Discord, and approving it again may not post it again.";
        public const string MovedBackFromApproved = "Moved back to Pending, so the bot stops showing it, as after Take down: /armor lookup stops finding it now. A post the bot already made stays in Discord, and approving it again may not post it again.";

        // The line after an approval, a take-down or a move back from Approved: the bot was asked to clear its name cache,
        // followed by the request's box, or why it wasn't, by the word the redirect carries. A word this version doesn't
        // know says nothing.
        public const string CacheClearing = "The bot was asked to clear its armor-name cache, so /armor list and the name suggestions follow as soon as it has:";

        public const string UnsentOff = "off";
        public const string UnsentLimit = "limit";
        public const string UnsentUnavailable = "unavailable";
        public const string UnsentUnsure = "unsure";

        const string CatchUp = "so /armor list and the name suggestions catch up within 5 minutes.";

        public static string? CacheNotCleared(string? unsent) => unsent switch
        {
            UnsentOff => "The bot's armor-name cache wasn't cleared, because requests to the bot are off, " + CatchUp,
            UnsentLimit => "The bot's armor-name cache wasn't cleared, because it has been cleared as often as the limits allow this hour, " + CatchUp,
            UnsentUnavailable => "The bot's armor-name cache wasn't cleared, because MinerBot's database didn't answer, " + CatchUp,
            UnsentUnsure => "MinerBot's database didn't confirm the request to clear the bot's caches, so it may or may not have gone. Either way /armor list and the name suggestions catch up within 5 minutes.",
            _ => null
        };

        // Announce now, which runs the bot's notification job for approved armor the bot hasn't seen yet, and what stands in
        // its place when it isn't offered. What a pause holds lives only in the bot's memory, as the pause's own words say.
        public const string AnnounceHelp = "Runs the bot's notification job now instead of at its next five-minute check, so this armor goes out with any other new armor, maps and lobbies, in every server with those posts turned on. The bot takes this at most once a minute, and not while a run is under way.";
        public const string AnnouncePaused = "Posts are paused, so the bot posts this armor once they're resumed, unless the bot restarts first, which loses it. Announce now isn't offered while they are.";
        public const string AnnounceOff = "Requests to the bot are off, so the bot posts it at its next five-minute check.";
        public static string AnnounceRunning(string since) => $"The notification job is running now, since {since}, and may post this armor, unless the bot restarts or stops first, which loses it. Announce now isn't offered while it runs.";

        // In its place when the bot's next notification run is its first since a start, which only notes the approved armor
        // it finds: what the bot has seen lives only in its memory, so a restart loses this armor if it wasn't posted before,
        // and Announce now would only make that run sooner. The status row can't tell whether it was posted, since it keeps
        // only the last run, nor whether that first run is under way, since a run is recorded as it ends, and armor approved
        // during that run is posted by the next, so neither line claims more.
        public static string AnnounceRestarted(string since) =>
            $"MinerBot started at {since}, and no notification run since is recorded yet. Its first run after a start only notes the approved armor it finds, without posting any, so if the bot hasn't posted this armor yet, it won't, unless the armor was approved during that run. Announce now isn't offered.";
        public const string AnnounceStopped = "MinerBot is stopped. Its first notification run once it starts again only notes the approved armor it finds, without posting any, so if the bot hasn't posted this armor yet, it won't. Announce now isn't offered.";
        public const string AnnounceNotNow = "Announce now isn't offered any more, for the reason below, so nothing was sent.";

        // As things were when it was pressed, so it stays true on its notice's GET once the armor is approved again
        public const string NotApprovedNow = "This armor wasn't approved when Announce now was pressed, so nothing was sent.";

        // AnnounceNotNow on its notice's GET once that reason isn't drawn below, as once a notification run has started
        // since: it's only ever for a stop or a start since the page was drawn
        public const string AnnounceWasNotOffered = "Announce now wasn't offered any more when it was pressed, because MinerBot had stopped or started again, so nothing was sent.";

        public const string Changed = "This armor changed after you opened it, so nothing was done. The page now shows it as it is: look it over again before you choose.";
        public const string Blocked = "This armor can't be approved, so nothing was done. What stops it is listed under Rules, and what you can do about it under Decide.";

        // Blocked on its notice's GET once nothing stops an approval, as once the other armor with the name has been taken
        // down, or this one has been approved since: Rules lists nothing to point to then
        public const string WasBlocked = "This armor couldn't be approved when Approve was pressed, so nothing was done. What stopped it has gone since: the page shows the armor as it is now.";

        public const string Unticked = "Tick each warning to show you've looked at it, then approve again. Nothing was done.";
        public const string Unsure = "The dashboard lost touch with its database while it was saving this, so it can't tell whether the change was saved. Wait a minute, then open the armor's page again to see its status. Trying again is safe: a change only applies while the armor still has the status you saw, and if this one was saved, the page says so.";

        // The armor already has the status the action leads to: an earlier try the dashboard couldn't confirm was saved
        // after all, or another reviewer made the same change first. The status is one of the website's three words.
        public static string Already(string status) =>
            $"This armor is already {status}, so nothing more was done: your earlier try was saved, or someone made the same change first. The history below lists the changes made on the dashboard.";

        public const string UnknownStatus = "The website doesn't use this status, so the bot doesn't show the armor, the list shows it only under All, and the dashboard can't change it. To review it here, set its status to Pending Approval in the website's database.";

        // A status the table's collation can't tell from one of the website's words, such as 'approved ', which the bot
        // goes by. The word is one of those three.
        public static string SpeltDifferently(string status) =>
            $"Its status is stored spelt differently from the website's {status}, which the website's table doesn't tell apart, so the bot and the list treat the armor as {status}, and so does this page. An action here saves the website's own word.";

        public const string ApproveHelp = "The bot shows it in /armor list and /armor lookup, and posts it at its next five-minute check in every server with armor posts turned on, or sooner with Announce now" + NotAtTheFirstCheck + ".";
        public const string ApproveAgainHelp = "The bot shows it in /armor list and /armor lookup, but may not post it: " + PostedOnce + ".";
        public const string RejectHelp = "The bot doesn't show it. The reason is optional, kept in the history and shown only on the dashboard.";
        public const string TakeDownHelp = "The bot stops showing it. A post it already made stays in Discord.";
        public const string BackToPendingHelp = "Puts the armor back in the queue, undoing the last decision. The dashboard can't tell whether the bot already posted it, and a post stays in Discord.";
        public const string BackToPendingFromApprovedHelp = "Puts the armor back in the queue, undoing the approval, so the bot stops showing it, as after Take down: /armor lookup at once, and /armor list and the name suggestions within 5 minutes. The dashboard can't tell whether the bot already posted it, and a post stays in Discord.";

        // The previews' note on the new-armor post
        public const string PostNote = "The bot posts it at its next five-minute check after an approval, or sooner with Announce now, in every server with armor posts turned on" + NotAtTheFirstCheck + ". Other armor approved by then shares the message.";
        public const string PostMayNotComeNote = "The bot posts new armor at its next five-minute check after an approval, in every server with armor posts turned on, but may not post this one: " + PostedOnce + ".";

        // What the screenshot section says when it doesn't load the address, which depends on whether the bot fetches it
        public const string ScreenshotBlocksApproval = "and the armor can't be approved until the website has an upload's address for it.";
        public const string ScreenshotFetchedNow = "The armor is approved, so the bot already fetches this address from the server's own network on every /armor lookup of it: Decide, below, says what to do.";

        // What a reviewer can do about what stops an approval, by the armor's status. The dashboard changes only an armor's
        // status, so anything else is changed on the website. Until then pending armor is rejected, rejected armor is left
        // as it is, and approved armor is taken down if the problem matters.
        public const string NotOffered = "Approve isn't offered, because of what stops an approval, under Rules. What you can do:";
        public const string ApprovedButBlocked = "The rules would stop this armor being approved now, but it's approved already, so the bot shows it as it is. What you can do:";

        public static string NameTakenStep(string? status) => status == ArmorStatus.Approved
            ? "Another approved armor has the same name, and /armor lookup only ever finds one of them: take down the one that shouldn't have it."
            : "Another approved armor already has this name. To give it to this one, take that one down first; " + Otherwise(status) + ".";

        public static string ScreenshotStep(string? status) => status == ArmorStatus.Approved
            ? "Take it down, so the bot stops fetching its screenshot, then have the address changed to an upload on the website before approving it again."
            : "Have the screenshot's address changed to an upload on the website, then open this page again; " + Otherwise(status) + ".";

        public static string OtherStep(string? status) => FixedOnTheWebsite + status switch
        {
            ArmorStatus.Approved => " Until then, take the armor down if the problem matters.",
            ArmorStatus.NotApproved => " Until then, leave this armor as it is.",
            _ => " Until then, reject this armor."
        };

        const string FixedOnTheWebsite = "A problem with what the website saved, such as the name or the colours, can only be fixed on the website, since the dashboard changes only an armor's status.";

        static string Otherwise(string? status) => status == ArmorStatus.NotApproved ? "otherwise leave this armor as it is" : "otherwise reject this armor";

        public static readonly string ReasonHint = $"Optional, at most {ArmorReviewData.ReasonLimit} characters. Only the dashboard shows it.";
    }
}
