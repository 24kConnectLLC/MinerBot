using MinerBot.Dashboard.Audit;

namespace MinerBot.Dashboard.Blacklist
{
    // What an editor can do to the blacklist
    public enum BlacklistAction
    {
        Add,
        Edit,
        Remove
    }

    internal static class BlacklistActions
    {
        // The audit log's words. Removing a row whose number no account can be is a removal too, told apart by its detail.
        public static string AuditAction(this BlacklistAction action) => action switch
        {
            BlacklistAction.Add => AuditWords.BlacklistAdd,
            BlacklistAction.Edit => AuditWords.BlacklistEdit,
            _ => AuditWords.BlacklistRemove
        };

        // The word the page's address carries after an action, so the message shown is fixed text and never what came in
        public static string DoneWord(this BlacklistAction action) => action switch
        {
            BlacklistAction.Add => "added",
            BlacklistAction.Edit => "saved",
            _ => "removed"
        };

        public static BlacklistAction? FromDoneWord(string? word) =>
            Enum.GetValues<BlacklistAction>().Cast<BlacklistAction?>().FirstOrDefault(action => action!.Value.DoneWord() == word);
    }
}
