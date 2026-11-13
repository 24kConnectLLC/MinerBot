using DataAccessLibrary.Armor.Models;
using MinerBot.Dashboard.Audit;
using MinerBot.Shared.Armor;

namespace MinerBot.Dashboard.Armor
{
    // What a reviewer can do to an armor. Each moves it to one status, from the statuses it starts from.
    public enum ArmorAction
    {
        Approve,
        Reject,
        TakeDown,
        BackToPending
    }

    internal static class ArmorActions
    {
        public static string Target(this ArmorAction action) => action switch
        {
            ArmorAction.Approve => ArmorStatus.Approved,
            ArmorAction.BackToPending => ArmorStatus.Pending,
            _ => ArmorStatus.NotApproved
        };

        // The audit log's words, fixed, because the audit page filters on them
        public static string AuditAction(this ArmorAction action) => action switch
        {
            ArmorAction.Approve => AuditWords.ArmorApprove,
            ArmorAction.Reject => AuditWords.ArmorReject,
            ArmorAction.TakeDown => AuditWords.ArmorTakeDown,
            _ => AuditWords.ArmorPending
        };

        // The status an armor's actions start from: the website's word the table reads its status as, which the bot and the
        // list's tabs go by too, so armor stored as 'approved' is Approved here as well, and can be taken down. False for a
        // status that matches none of the words, which ArmorSubmissionData can't change.
        public static bool TryStatusOf(ArmorSubmission armor, out string? status)
        {
            status = armor.KnownStatus;
            return armor.Status == null || armor.KnownStatus != null;
        }

        // Reject is for armor waiting for a review, and Take down for armor already approved. Back to pending undoes either
        // decision, and puts armor with no status in the queue. Approving from Not Approved saves a step through Pending:
        // it runs the same rules. The status is TryStatusOf's, so anything but the website's words matches none.
        public static bool StartsFrom(this ArmorAction action, string? status) => action switch
        {
            ArmorAction.Approve => status is ArmorStatus.Pending or ArmorStatus.NotApproved,
            ArmorAction.Reject => status is ArmorStatus.Pending,
            ArmorAction.TakeDown => status is ArmorStatus.Approved,
            ArmorAction.BackToPending => status is null or ArmorStatus.Approved or ArmorStatus.NotApproved,
            _ => false
        };

        public static IReadOnlyList<ArmorAction> From(string? status) => Enum.GetValues<ArmorAction>().Where(action => action.StartsFrom(status)).ToList();

        // The word the page's address carries after an action, so the message shown is fixed text and never what came in
        public static string DoneWord(this ArmorAction action) => action switch
        {
            ArmorAction.Approve => "approved",
            ArmorAction.Reject => "rejected",
            ArmorAction.TakeDown => "taken-down",
            _ => "pending"
        };

        public static ArmorAction? FromDoneWord(string? word) =>
            Enum.GetValues<ArmorAction>().Cast<ArmorAction?>().FirstOrDefault(action => action!.Value.DoneWord() == word);
    }
}
