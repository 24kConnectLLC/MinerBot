namespace DataAccessLibrary.Posting
{
    // The words of the posting tables. The reasons a post didn't go are MinerBot.Shared's DeliveryReasons, which the
    // dashboard shows in words of its own.

    // What the bot watches. The first three it posts, each server with its own switch; submissions are armor waiting for a
    // review, which it only tells the owner about.
    public static class PostSource
    {
        public const string Armor = "armor";
        public const string Maps = "maps";
        public const string Lobbies = "lobbies";
        public const string Submissions = "submissions";

        public static IReadOnlyList<string> All { get; } = [Armor, Maps, Lobbies, Submissions];

        // The kinds a server is owed and sent, with a switch and a channel each
        public static IReadOnlyList<string> Posted { get; } = [Armor, Maps, Lobbies];

        public static bool IsKnown(string? source) => source is Armor or Maps or Lobbies or Submissions;

        public static bool IsPosted(string? source) => source is Armor or Maps or Lobbies;
    }

    // What became of an item the bot found. Baseline is one it takes as already posted: there at a baseline, a map that
    // moved up Steam's list, or one too old to be news. Queued was found new and queued for the servers then owed it, which
    // may be none. Ignored is a lobby of a version whose lobbies aren't posted. Alerted and alert failed are submissions the
    // owner was or wasn't told about.
    public static class SeenState
    {
        public const string Baseline = "baseline";
        public const string Queued = "queued";
        public const string Ignored = "ignored";
        public const string Alerted = "alerted";
        public const string AlertFailed = "alert_failed";

        public static IReadOnlyList<string> All { get; } = [Baseline, Queued, Ignored, Alerted, AlertFailed];

        public static bool IsKnown(string? state) => state is Baseline or Queued or Ignored or Alerted or AlertFailed;
    }

    // A server's item in the queue: pending until a message claims it, sending while that message's fate is open, then sent,
    // or dropped with a reason. A row goes back from sending to pending only when its message is known not to have been made.
    public static class QueueState
    {
        public const string Pending = "pending";
        public const string Sending = "sending";
        public const string Sent = "sent";
        public const string Dropped = "dropped";

        public static IReadOnlyList<string> All { get; } = [Pending, Sending, Sent, Dropped];

        public static bool IsKnown(string? state) => state is Pending or Sending or Sent or Dropped;
    }

    // A message the bot sent or tried: sending from its claim to its answer, then sent with Discord's message ID, failed with
    // a reason, in doubt while Discord didn't answer, or unconfirmed when the bot couldn't tell whether it arrived, which
    // counts as sent so it's never sent again. A refusal the bot's cache saw first is failed, with no message.
    public static class DeliveryState
    {
        public const string Sending = "sending";
        public const string Sent = "sent";
        public const string Failed = "failed";
        public const string InDoubt = "in_doubt";
        public const string Unconfirmed = "unconfirmed";

        public static IReadOnlyList<string> All { get; } = [Sending, Sent, Failed, InDoubt, Unconfirmed];

        public static bool IsKnown(string? state) => state is Sending or Sent or Failed or InDoubt or Unconfirmed;

        // Not settled yet: its rows stay sending, and its server's kind gets no new message
        public static bool IsOpen(string? state) => state is Sending or InDoubt;
    }

    // Why a source's baseline was taken: its first good read, or a new one because an older bot posted since this bot last
    // checked
    public static class BaselineReason
    {
        public const string First = "first";
        public const string OlderBot = "older_bot";

        public static IReadOnlyList<string> All { get; } = [First, OlderBot];

        public static bool IsKnown(string? reason) => reason is First or OlderBot;
    }
}
