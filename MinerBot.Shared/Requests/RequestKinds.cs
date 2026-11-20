namespace MinerBot.Shared.Requests
{
    // Who a request says asked for it, as the dashboard signs it. The same words as the audit log's roles, which
    // DataAccessLibrary's tests pin, because this library can't reference that one.
    public static class RequestRoles
    {
        public const string Owner = "owner";
        public const string Admin = "admin";

        // A server's own admin, for the kinds about one server
        public const string ServerAdmin = "server_admin";

        public static IReadOnlyList<string> All { get; } = [Owner, Admin, ServerAdmin];

        public static bool IsKnown(string? role) => role is Owner or Admin or ServerAdmin;
    }

    // How the bot dealt with a request, in bot_commands.state, which only the bot writes. A request with no state waits.
    // Refused and expired requests were never taken, so nothing happened. Unknown is one that may or may not have happened:
    // an action past its limit, or a run that ended while the request ran.
    public static class RequestStates
    {
        public const string Running = "running";
        public const string Done = "done";
        public const string Failed = "failed";
        public const string Refused = "refused";
        public const string Expired = "expired";
        public const string Unknown = "unknown";

        public static IReadOnlyList<string> All { get; } = [Running, Done, Failed, Refused, Expired, Unknown];

        public static bool IsKnown(string? state) => state is Running or Done or Failed or Refused or Expired or Unknown;
    }

    // Whether the direct message the bot sent the owner about a request went, in bot_commands.dm_state
    public static class RequestDmStates
    {
        public const string Sent = "sent";
        public const string Failed = "failed";

        public static IReadOnlyList<string> All { get; } = [Sent, Failed];

        public static bool IsKnown(string? dm) => dm is Sent or Failed;
    }

    // The one text a kind of request takes, if any, which RequestRules checks
    public enum RequestArgument
    {
        None,

        // Why posts are paused, which the owner's direct message shows
        Reason,

        // The bot's status text, or no text for the default
        StatusText,

        // The name in a custom Steam profile link, steamcommunity.com/id/<name>
        CustomLinkName
    }

    // One kind of request on the fixed list. The bot takes at most Limit of it in each Window, counted from the table
    // together with the kinds in CountedWith, and for each server apart when PerServer is set. A request waits at most
    // Expiry to be taken, and once taken, its action ends within ActionLimit.
    public sealed record RequestKind
    {
        // Such as posts.pause, in bot_commands.kind, and as the audit log's action
        public required string Word { get; init; }

        // What the owner's direct messages and the dashboard call it, such as Pause posts
        public required string Label { get; init; }

        public required IReadOnlyList<string> Roles { get; init; }

        public required int Limit { get; init; }
        public required TimeSpan Window { get; init; }

        // The kinds whose requests count toward this one's limit, itself included
        public IReadOnlyList<string> CountedWith { get => field ?? [Word]; init; }

        public bool PerServer { get; init; }

        public required TimeSpan Expiry { get; init; }
        public required TimeSpan ActionLimit { get; init; }

        // Whether it needs a server and a channel. A kind that doesn't takes neither.
        public bool NeedsServer { get; init; }
        public bool NeedsChannel { get; init; }

        public RequestArgument Argument { get; init; }

        // The bot's job it runs, as the bot names its jobs, <class>.<method>, or null for a kind that isn't a job
        public string? Job { get; init; }

        public bool Allows(string? role) => role != null && Roles.Contains(role);

        // The kinds that only the Owner can ask for, after which the bot tells the owner what it did
        public bool OwnerOnly => Roles is [RequestRoles.Owner];

        // In a sentence such as "Only the Owner or an Admin can ask for this"
        public string WhoMayAsk => Roles.Count switch
        {
            1 => "the Owner",
            2 => "the Owner or an Admin",
            _ => "the Owner, an Admin or the server's own admins"
        };

        // A request made to last no longer than its kind allows, so a signed row can't be held back to use later
        public bool FitsExpiry(DateTime createdAt, DateTime expiresAt) => expiresAt > createdAt && expiresAt - createdAt <= Expiry;
    }

    // The fixed list of what the dashboard may ask the bot to do. Both apps and DataAccessLibrary read it, so every word,
    // role, limit and expiry has this one source. The plan's thirteen rows are fourteen kinds: pausing and resuming posts
    // are two, and the Steam-token job is the token's renewal, which both reach, so a job kind can't get round the
    // renewal's limit.
    public static class RequestKinds
    {
        public const string CachesClear = "caches.clear";
        public const string JobNotifications = "job.notifications";
        public const string JobColors = "job.colors";
        public const string SteamRenew = "steam.renew";
        public const string PostsPause = "posts.pause";
        public const string PostsResume = "posts.resume";
        public const string StatusSet = "status.set";
        public const string SteamReconnect = "steam.reconnect";
        public const string GuildTestMessage = "guild.test_message";
        public const string GuildRefresh = "guild.refresh";
        public const string SteamLookup = "steam.lookup";
        public const string GuildLeave = "guild.leave";
        public const string BotRestart = "bot.restart";
        public const string OwnerSigninAlert = "owner.signin_alert";

        // Most requests would surprise if the bot acted on them minutes after the button was pressed. Pausing, resuming
        // and a sign-in alert are still wanted a few minutes late, such as across a restart or a short outage of Discord.
        public static readonly TimeSpan UsualExpiry = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan SlowExpiry = TimeSpan.FromMinutes(10);

        static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
        static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);
        static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);
        static readonly TimeSpan Hour = TimeSpan.FromHours(1);

        static readonly IReadOnlyList<string> OwnerOnly = [RequestRoles.Owner];
        static readonly IReadOnlyList<string> OwnerOrAdmin = [RequestRoles.Owner, RequestRoles.Admin];
        static readonly IReadOnlyList<string> AnyAdmin = [RequestRoles.Owner, RequestRoles.Admin, RequestRoles.ServerAdmin];

        static TimeSpan Seconds(int seconds) => TimeSpan.FromSeconds(seconds);

        public static IReadOnlyList<RequestKind> All { get; } =
        [
            new() { Word = CachesClear, Label = "Clear caches", Roles = OwnerOrAdmin, Limit = 30, Window = Hour, Expiry = UsualExpiry, ActionLimit = Seconds(5) },
            new()
            {
                Word = JobNotifications, Label = "Run the notification job", Roles = OwnerOrAdmin, Limit = 1, Window = Minute, Expiry = UsualExpiry,
                ActionLimit = Seconds(60), Job = "GuildNotificationJob.NotifyAllGuilds"
            },
            new()
            {
                Word = JobColors, Label = "Run the colour job", Roles = OwnerOnly, Limit = 1, Window = Minute, Expiry = UsualExpiry, ActionLimit = Seconds(60),
                Job = "ArmorJob.AddColorArrays"
            },
            new() { Word = SteamRenew, Label = "Renew the Steam token", Roles = OwnerOnly, Limit = 1, Window = TenMinutes, Expiry = UsualExpiry, ActionLimit = Seconds(45) },
            new()
            {
                Word = PostsPause, Label = "Pause posts", Roles = OwnerOnly, Limit = 10, Window = Hour, CountedWith = [PostsPause, PostsResume], Expiry = SlowExpiry,
                ActionLimit = Seconds(20), Argument = RequestArgument.Reason
            },
            new()
            {
                Word = PostsResume, Label = "Resume posts", Roles = OwnerOnly, Limit = 10, Window = Hour, CountedWith = [PostsPause, PostsResume], Expiry = SlowExpiry,
                ActionLimit = Seconds(20)
            },
            new()
            {
                Word = StatusSet, Label = "Set the status text", Roles = OwnerOnly, Limit = 6, Window = Hour, Expiry = UsualExpiry, ActionLimit = Seconds(30),
                Argument = RequestArgument.StatusText
            },
            new() { Word = SteamReconnect, Label = "Reconnect Steam", Roles = OwnerOnly, Limit = 1, Window = FiveMinutes, Expiry = UsualExpiry, ActionLimit = Seconds(45) },
            new()
            {
                Word = GuildTestMessage, Label = "Send a test message", Roles = AnyAdmin, Limit = 3, Window = TenMinutes, PerServer = true, Expiry = UsualExpiry,
                ActionLimit = Seconds(30), NeedsServer = true, NeedsChannel = true
            },
            new()
            {
                Word = GuildRefresh, Label = "Refresh a server's channels", Roles = AnyAdmin, Limit = 1, Window = Minute, PerServer = true, Expiry = UsualExpiry,
                ActionLimit = Seconds(20), NeedsServer = true
            },
            new()
            {
                Word = SteamLookup, Label = "Look up a Steam profile link", Roles = OwnerOrAdmin, Limit = 60, Window = Hour, Expiry = UsualExpiry, ActionLimit = Seconds(30),
                Argument = RequestArgument.CustomLinkName
            },
            new()
            {
                Word = GuildLeave, Label = "Leave a server", Roles = OwnerOnly, Limit = 5, Window = Hour, Expiry = UsualExpiry, ActionLimit = Seconds(30), NeedsServer = true
            },
            new() { Word = BotRestart, Label = "Restart the bot", Roles = OwnerOnly, Limit = 1, Window = TenMinutes, Expiry = UsualExpiry, ActionLimit = Seconds(30) },
            new()
            {
                Word = OwnerSigninAlert, Label = "Tell the owner about a sign-in", Roles = OwnerOrAdmin, Limit = 20, Window = Hour, Expiry = SlowExpiry,
                ActionLimit = Seconds(30)
            }
        ];

        static readonly Dictionary<string, RequestKind> ByWord = All.ToDictionary(kind => kind.Word, StringComparer.Ordinal);

        // Exact, so a word in other letters isn't the same kind
        public static RequestKind? Find(string? word) => word != null && ByWord.TryGetValue(word, out var kind) ? kind : null;
    }
}
