namespace DataAccessLibrary.Posting.Models
{
    // A save of one server's switches from a form: what it asks for, and the version of the switches the form was drawn
    // from, 0 when the server had none. Who saves them: a Discord user ID, and owner or server_admin.
    public sealed class SwitchChange
    {
        public ulong GuildId { get; init; }
        public bool ArmorOn { get; init; }
        public bool MapsOn { get; init; }
        public bool LobbiesOn { get; init; }
        public uint Version { get; init; }
        public DateTime At { get; init; }
        public ulong ChangedBy { get; init; }
        public string Role { get; init; } = "";
    }

    public enum SwitchSaveOutcome
    {
        // Written, with the next version
        Saved,

        // What was asked for is what the server has, so nothing was written
        Unchanged,

        // The switches changed after the form was drawn, so nothing was written
        Stale
    }

    // How a save went, with the server's switches as they were before it, null when it had none, and as saved
    public sealed record SwitchSave(SwitchSaveOutcome Outcome, GuildSwitches? Before, GuildSwitches? After);
}
