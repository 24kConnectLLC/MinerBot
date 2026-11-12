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

        // What the page says after an action, while the armor still has the status the action gave it. What the bot does
        // next comes from its five-minute post, its name cache and the lookup, which the dashboard can't change or see.
        public const string Approved = "Approved. The bot posts new armor at its next five-minute check, in every server with armor posts turned on, together with any other armor approved by then; if the bot is restarting then, it won't post this one. /armor lookup finds it by its exact name now, and /armor list and the name suggestions catch up within 5 minutes.";
        public const string ApprovedAgain = "Approved. /armor lookup finds it by its exact name now, and /armor list and the name suggestions catch up within 5 minutes. The bot may not post it: " + PostedOnce + ".";
        public const string Rejected = "Rejected. The bot doesn't show it.";
        public const string RejectedWithReason = "Rejected. The bot doesn't show it, and the reason is kept in the history below.";
        public const string TakenDown = "Taken down. /armor lookup stops finding it now, though its name can stay among the suggestions for up to 5 minutes. A post the bot already made stays in Discord.";
        public const string MovedBackToPending = "Moved back to Pending. If the bot already posted it, that post stays in Discord, and approving it again may not post it again.";
        public const string MovedBackFromApproved = "Moved back to Pending, so the bot stops showing it, as after Take down: /armor lookup stops finding it now, though its name can stay in /armor list and the suggestions for up to 5 minutes. A post the bot already made stays in Discord, and approving it again may not post it again.";

        public const string Changed = "This armor changed after you opened it, so nothing was done. The page now shows it as it is: look it over again before you choose.";
        public const string Blocked = "This armor can't be approved, so nothing was done. What stops it is listed under Rules, and what you can do about it under Decide.";
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

        public const string ApproveHelp = "The bot shows it in /armor list and /armor lookup, and posts it at its next five-minute check in every server with armor posts turned on.";
        public const string ApproveAgainHelp = "The bot shows it in /armor list and /armor lookup, but may not post it: " + PostedOnce + ".";
        public const string RejectHelp = "The bot doesn't show it. The reason is optional, kept in the history and shown only on the dashboard.";
        public const string TakeDownHelp = "The bot stops showing it. A post it already made stays in Discord.";
        public const string BackToPendingHelp = "Puts the armor back in the queue, undoing the last decision. The dashboard can't tell whether the bot already posted it, and a post stays in Discord.";
        public const string BackToPendingFromApprovedHelp = "Puts the armor back in the queue, undoing the approval, so the bot stops showing it, as after Take down: /armor lookup at once, and /armor list and the name suggestions within 5 minutes. The dashboard can't tell whether the bot already posted it, and a post stays in Discord.";

        // The previews' note on the new-armor post
        public const string PostNote = "The bot posts it at its next five-minute check after an approval, in every server with armor posts turned on. Other armor approved by then shares the message.";
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
