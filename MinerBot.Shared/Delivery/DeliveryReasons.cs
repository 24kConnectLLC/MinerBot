using System.Globalization;

namespace MinerBot.Shared.Delivery
{
    // Why one of the bot's armor, map or lobby posts didn't go, in the words the bot records in notification_deliveries and
    // notification_queue, and the plain words the dashboard shows for each. Discord's answers are read by their numbers, the
    // HTTP status and Discord's own code, never by Discord.Net's names for them, which aren't Discord's (it calls 50001
    // MissingPermissions), and never by Discord's text, which a row, an event or the console must never hold.
    public static class DeliveryReasons
    {
        // Refusals the bot's copy of the server saw first, which cost Discord nothing: the channel isn't one it can post in,
        // or the bot lacks a permission there, with the mask of the ones missing
        public const string NoChannel = "no_channel";
        public const string MissingPermissions = "missing_permissions";

        // Discord's refusals, by Discord's own code
        public const string MissingAccess = "missing_access";
        public const string DiscordPermissions = "discord_permissions";
        public const string UnknownChannel = "unknown_channel";
        public const string UnknownServer = "unknown_server";
        public const string NotTextChannel = "not_text_channel";
        public const string TooLarge = "too_large";
        public const string InvalidPost = "invalid_post";
        public const string ChannelWriteLimit = "channel_write_limit";
        public const string Slowmode = "slowmode";

        // By the HTTP status: a rate limit that reached the bot, a refused token, Discord's own trouble or no answer at all,
        // and any other refusal
        public const string RateLimited = "rate_limited";
        public const string Unauthorized = "unauthorized";
        public const string NoAnswer = "no_answer";
        public const string Refused = "refused";

        // A message that never reached the channel, so it's sent again: the look in the channel's history didn't find one
        // Discord didn't answer about, its send never left the bot (the connection didn't open, or the bot's stop came before
        // the first try), or its claim's answer never came, so nothing was sent for it. And one the look couldn't find out
        // about, so it's counted as sent.
        public const string NotSent = "not_sent";
        public const string Unconfirmed = "unconfirmed";

        // Why an item waiting for a server was dropped there without a message
        public const string NotApproved = "not_approved";
        public const string Hidden = "hidden";
        public const string Closed = "closed";
        public const string SwitchedOff = "switched_off";
        public const string ChannelOff = "channel_off";
        public const string NoSettings = "no_settings";
        public const string NotInServer = "not_in_server";
        public const string Left = "left";
        public const string TooOld = "too_old";

        // The permissions a post needs, as the bits of the mask a refusal keeps: Read Message History only for the look in the
        // channel's history
        public const int ViewChannel = 1;
        public const int SendMessages = 2;
        public const int EmbedLinks = 4;
        public const int ReadMessageHistory = 8;

        // Discord's codes the bot knows, each to its reason
        static readonly Dictionary<int, string> Codes = new()
        {
            [50001] = MissingAccess,
            [50013] = DiscordPermissions,
            [10003] = UnknownChannel,
            [10004] = UnknownServer,
            [50008] = NotTextChannel,
            [40005] = TooLarge,
            [50035] = InvalidPost,
            [20028] = ChannelWriteLimit,
            [20016] = Slowmode
        };

        // A message's, or a refusal's
        public static IReadOnlyList<string> Failures { get; } =
        [
            NoChannel, MissingPermissions, MissingAccess, DiscordPermissions, UnknownChannel, UnknownServer, NotTextChannel, TooLarge, InvalidPost,
            ChannelWriteLimit, Slowmode, RateLimited, Unauthorized, NoAnswer, Refused, NotSent, Unconfirmed
        ];

        // A waiting item's
        public static IReadOnlyList<string> Drops { get; } = [NotApproved, Hidden, Closed, SwitchedOff, ChannelOff, NoSettings, NotInServer, Left, TooOld];

        public static IReadOnlyList<string> All { get; } = [.. Failures, .. Drops];

        public static bool IsFailure(string? reason) => reason != null && Failures.Contains(reason, StringComparer.Ordinal);

        public static bool IsDrop(string? reason) => reason != null && Drops.Contains(reason, StringComparer.Ordinal);

        public static bool IsKnown(string? reason) => IsFailure(reason) || IsDrop(reason);

        // Discord's own code as a number to keep: none for a missing one or for 0, Discord's "general error", which its 5xx and
        // 401 answers carry
        public static int? CodeOf(int? discordCode) => discordCode is { } code and not 0 ? code : null;

        // The reason for Discord's answer to a send. A 5xx is Discord's own trouble, whatever its code, and a 429 and a 401
        // are about every send, not this channel; any other answer goes by Discord's code first, then is a refusal.
        public static string ForAnswer(int httpStatus, int? discordCode)
        {
            if (httpStatus >= 500)
                return NoAnswer;

            if (httpStatus == 429)
                return RateLimited;

            if (httpStatus == 401)
                return Unauthorized;

            return CodeOf(discordCode) is { } code && Codes.TryGetValue(code, out var reason) ? reason : Refused;
        }

        // The missing permissions' names, as Discord names them, such as "View Channel, Send Messages and Embed Links"
        public static string MissingNames(int mask)
        {
            var names = new List<string>();

            if ((mask & ViewChannel) != 0)
                names.Add("View Channel");

            if ((mask & SendMessages) != 0)
                names.Add("Send Messages");

            if ((mask & EmbedLinks) != 0)
                names.Add("Embed Links");

            if ((mask & ReadMessageHistory) != 0)
                names.Add("Read Message History");

            return names.Count switch
            {
                0 => "a permission it needs",
                1 => names[0],
                _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}"
            };
        }

        // The page's words for a reason, to follow "Couldn't post: " or a time: what happened, in words a server's admin can
        // act on. A reason the bot doesn't know is said to be one, rather than shown as it's stored.
        public static string Words(string? reason, int? missing = null) => reason switch
        {
            NoChannel => "the channel isn't a text, announcement or voice channel of the server any more: it was deleted, or it's a forum",
            MissingPermissions => $"MinerBot is missing {MissingNames(missing ?? 0)} there",
            MissingAccess => "Discord says MinerBot can't see the channel (Missing Access)",
            DiscordPermissions => "Discord says MinerBot is missing a permission there (Missing Permissions), though its copy showed it could post",
            UnknownChannel => "Discord says the channel doesn't exist any more (Unknown Channel)",
            UnknownServer => "Discord doesn't know the server, so MinerBot may have been removed (Unknown Guild)",
            NotTextChannel => "Discord takes no messages in that kind of channel",
            TooLarge => "the post was too large for Discord",
            InvalidPost => "Discord refused the post's contents, which is a fault in MinerBot",
            ChannelWriteLimit => "the channel had reached Discord's limit of messages for now",
            Slowmode => "the channel's slowmode held it back",
            RateLimited => "Discord's rate limit held it back",
            Unauthorized => "Discord refused MinerBot's token",
            NoAnswer => "Discord didn't answer in time",
            Refused => "Discord refused it",
            NotSent => "the post never reached the channel, so MinerBot tries it again",
            Unconfirmed => "MinerBot didn't learn whether the post went, and can't read the channel to check, so it may not have arrived",
            NotApproved => "it wasn't approved any more",
            Hidden => "it was hidden or removed on Steam",
            Closed => "the lobby closed first",
            SwitchedOff => "its kind was switched off on the dashboard",
            ChannelOff => "the server had no channel set for its kind any more",
            NoSettings => "the server had no channels set any more",
            NotInServer => "MinerBot wasn't in the server any more",
            Left => "the server removed MinerBot",
            TooOld => "it waited too long to be news",
            _ => "a reason this version of the dashboard doesn't know"
        };

        // The words with Discord's numbers after them, such as "Discord refused it (HTTP 403, Discord 50013)": the status when
        // there is one, and Discord's code when it isn't 0
        public static string Describe(string? reason, int? missing, int? httpStatus, long? discordCode)
        {
            var numbers = new List<string>();

            if (httpStatus is { } status)
                numbers.Add(string.Create(CultureInfo.InvariantCulture, $"HTTP {status}"));

            if (discordCode is { } code and not 0)
                numbers.Add(string.Create(CultureInfo.InvariantCulture, $"Discord {code}"));

            var words = Words(reason, missing);

            return numbers.Count == 0 ? words : $"{words} ({string.Join(", ", numbers)})";
        }
    }
}
