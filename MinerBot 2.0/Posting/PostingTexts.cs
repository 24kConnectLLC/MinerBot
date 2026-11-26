using DataAccessLibrary.Posting;
using MinerBot.Shared.Delivery;
using System.Globalization;

namespace MinerBot_2._0.Posting
{
    // The notification run's words: its details, which the Bot page, the Overview and Announce now show, its events' details
    // and its lines in the log. Made of the bot's own words, counts, IDs, times and Discord's numbers, never Discord's text,
    // which can carry Discord's words or a token. Only a read of the armor, the maps or the lobbies that failed logs its
    // exception's message, as it always has.
    internal static class PostingTexts
    {
        // The run's details. Every one says something, so an older bot's runs, which have none, can be told from this one's.

        public const string DiscordNotReady = "Discord hasn't sent the bot's servers yet, so this run found and posted nothing";

        public const string PauseUnknown = "posts wait until the bot has read whether they're paused; what this run found is queued";

        // What waits for the resume, by kind, and for how many servers
        public static string Paused(int armor, int maps, int lobbies, int servers) => armor + maps + lobbies == 0
            ? "posts are paused, and nothing waits"
            : Text($"posts are paused: {Items(armor, maps, lobbies)} {Wait(armor + maps + lobbies)} for {Count(servers, "server")}");

        public static string DatabaseDidntAnswer(string problem) => $"posts wait: MinerBot's database didn't answer ({problem})";

        public static string WebsiteDidntAnswer(string problem) => $"posts wait: the website's database didn't give the servers' channels ({problem})";

        public static string FindingNotSaved(string problem) =>
            $"posts wait: MinerBot's database didn't take what this run found ({problem}), so it posted nothing; the next run finds it again";

        public static string BacklogNotTaken(string problem) => $"posts wait: MinerBot's database didn't take what an earlier run sent ({problem})";

        // Why a run stopped posting before it was through, after what it posted and what waits; for MinerBot's database, with
        // what it answered
        public static string Stopped(RunStop stop, string? problem = null) => stop switch
        {
            RunStop.RateLimited => "Discord rate-limited the bot, so this run stopped posting",
            RunStop.Unauthorized => "Discord refused the bot's token, so this run stopped posting",
            RunStop.Unreachable => "the bot couldn't connect to Discord, so this run stopped posting",
            RunStop.Unanswered => Text($"Discord didn't answer {PostingLimits.UnansweredPerRun} posts, so this run stopped posting"),
            RunStop.Database => $"MinerBot's database didn't take a post's record ({problem}), so this run stopped posting",
            RunStop.MessageLimit => Text($"this run sent its limit of {PostingLimits.MessagesPerRun} messages"),
            RunStop.TimeLimit => Text($"this run reached its limit of {PostingLimits.ClaimTime.TotalSeconds:0} seconds"),
            _ => "the bot is stopping, so this run stopped posting"
        };

        public static string ArmorUnreadable(string problem) => $"armor list couldn't be read ({problem})";

        public const string NothingNew = "nothing new to post";

        public const string PostedNothing = "posted nothing";

        // Such as "took the first baseline of armor, maps and lobbies (122, 20 and 3 items)", or a new one with the time since
        // which an older bot posted
        public static string Baselines(IReadOnlyList<TakenBaseline> taken)
        {
            var first = taken.Where(baseline => baseline.Reason == BaselineReason.First).ToList();
            var olderBot = taken.Where(baseline => baseline.Reason == BaselineReason.OlderBot).ToList();
            var parts = new List<string>();

            if (first.Count > 0)
                parts.Add($"took the first baseline of {Of(first)}");

            if (olderBot.Count > 0)
                parts.Add(Text($"took a new baseline of {Of(olderBot)}, because an older bot posted since {Time(olderBot.Min(baseline => baseline.OlderBotSince!.Value))}"));

            return string.Join("; ", parts);

            static string Of(List<TakenBaseline> baselines) =>
                Text($"{And(baselines.Select(baseline => baseline.Source))} ({And(baselines.Select(baseline => baseline.Items.ToString(CultureInfo.InvariantCulture)))} {(baselines.Count == 1 && baselines[0].Items == 1 ? "item" : "items")})");
        }

        // One kind's posts this run, such as "1 armor: posted in 5 of 6 servers, 1 failed"
        public static string Posted(string source, int items, int tried, int posted, int failed, int unanswered) =>
            Text($"{Item(source, items)}: posted in {posted} of {Count(tried, "server")}") + (failed > 0 ? Text($", {failed} failed") : "")
            + (unanswered > 0 ? Text($", {unanswered} not answered") : "");

        // What still waits after the run
        public static string StillWait(int items, int servers) => Text($"nothing new; {Count(items, "item")} still {Wait(items)} for {Count(servers, "server")}");

        public static string Waiting(int items, int servers) => Text($"{Count(items, "item")} {Wait(items)} for {Count(servers, "server")}");

        // The posts.baseline event's detail: one source's baseline
        public static string BaselineEvent(TakenBaseline baseline) => baseline.Reason == BaselineReason.OlderBot
            ? Text($"{baseline.Source}: {Count(baseline.Items, "item")} taken as already posted, because an older bot posted since {Time(baseline.OlderBotSince!.Value)}")
            : Text($"{baseline.Source}: {Count(baseline.Items, "item")} taken as already posted, the first baseline");

        // The posts.unconfirmed event's detail: IDs and the time only
        public static string UnconfirmedEvent(string source, ulong guildId, ulong channelId, DateTime attemptedAt) =>
            Text($"{(source == PostSource.Armor ? "an" : "a")} {Kind(source)} post to server {guildId}, channel {channelId}, at {attemptedAt:yyyy-MM-dd HH:mm:ss} UTC may not have arrived: the bot didn't learn whether it went, and couldn't check that channel's messages");

        // Lines in the log

        // A source the run couldn't read, as before PR 11, with the exception's message: the website's database's and Steam's,
        // never Discord's
        public static string ReadFailed(string what, Exception e) => $"Couldn't read {what} for notifications\n{e.Message}";

        // A kind's name in the log's lines, as before PR 11
        public static string Kind(string source) => source switch
        {
            PostSource.Armor => "armor",
            PostSource.Maps => "map",
            _ => "lobby"
        };

        public static string CouldntPost(string source, ulong channelId, ulong guildId, string because) =>
            Text($"Couldn't send the {Kind(source)} notification to channel {channelId} of guild {guildId}, because {because}");

        public const string NotInServer = "the bot isn't in that server";
        public const string ServerUnavailable = "Discord reports that server unavailable";
        public const string ServerNotSent = "Discord hasn't sent that server yet";
        public const string ChannelNotInServer = "that channel isn't in that server";

        public static string Missing(int mask) => $"the bot is missing {DeliveryReasons.MissingNames(mask)} there";

        // Discord refused it: the reason as it's recorded, with Discord's numbers, never its text
        public static string Refused(string source, ulong channelId, ulong guildId, string reason, ushort? httpStatus, uint? discordCode) =>
            Text($"Couldn't send the {Kind(source)} notification to channel {channelId} of guild {guildId}: {reason}{Numbers(httpStatus, discordCode)}");

        public static string Unanswered(string source, ulong channelId, ulong guildId, ushort? httpStatus) =>
            Text($"Discord didn't answer the {Kind(source)} notification to channel {channelId} of guild {guildId}{Numbers(httpStatus, null)}, so it may or may not have arrived");

        public static string ClaimLost(string source, ulong guildId) =>
            Text($"Another run took the {Kind(source)} notification for guild {guildId} first, so this run left it");

        public static string ClaimFailed(string source, ulong guildId, string problem) =>
            Text($"MinerBot's database didn't take the {Kind(source)} notification for guild {guildId} before its send ({problem}), so this run sends nothing more");

        public static string SettleFailed(string source, ulong channelId, ulong guildId, string problem) =>
            Text($"MinerBot's database didn't take how the {Kind(source)} notification to channel {channelId} of guild {guildId} ended ({problem}), so this run sends nothing more");

        public static string NotBuilt(string source, ulong guildId, string problem) =>
            Text($"Couldn't make the {Kind(source)} notification for guild {guildId} ({problem}), so its items wait for the next run");

        public static string DropFailed(string source, ulong guildId, string problem) =>
            Text($"MinerBot's database didn't take what's dropped of guild {guildId}'s {Kind(source)} posts ({problem}), so this run sends nothing more");

        public static string MapsNotLookedUp(string problem) => $"Couldn't look up the maps that wait on Steam ({problem}), so they wait for the next run";

        public static string StatusNotCleared(string source, ulong guildId, string problem) =>
            Text($"MinerBot's database didn't take that guild {guildId}'s {Kind(source)} posts are off now ({problem}); the next run tries again");

        public static string StoppedPosting(RunStop stop, string? problem = null) => $"The notification run stopped posting: {Stopped(stop, problem)}";

        public static string WaitsNotEnded(ulong guildId, string problem) =>
            Text($"MinerBot's database didn't take that guild {guildId}'s refused posts may be tried again now ({problem}), so they wait as before");

        // The look in a channel's history for a message whose fate the bot didn't know

        public static string FoundSent(string source, ulong channelId, ulong guildId) =>
            Text($"Found the {Kind(source)} notification to channel {channelId} of guild {guildId} in the channel's messages, so it went");

        public static string NotThere(string source, ulong channelId, ulong guildId) =>
            Text($"The {Kind(source)} notification to channel {channelId} of guild {guildId} isn't in the channel's messages, so this run sends it again");

        public static string CouldntCheck(string source, ulong channelId, ulong guildId, string because, int look) =>
            Text($"Couldn't check whether the {Kind(source)} notification to channel {channelId} of guild {guildId} arrived, because {because} (look {look} of {PostingLimits.Looks})");

        public static string Unconfirmed(string source, ulong channelId, ulong guildId, string because) =>
            Text($"The {Kind(source)} notification to channel {channelId} of guild {guildId} may not have arrived, and the bot couldn't check{(because.Length == 0 ? "" : ", because " + because)}, so it isn't sent again");

        public static string ReadUnanswered(ulong channelId, ulong guildId, string problem) =>
            Text($"Discord didn't answer the read of channel {channelId}'s messages in guild {guildId} ({problem}), so the other checks wait for the next run");

        public static string ReadUnansweredInOutage(string source, ulong channelId, ulong guildId, string problem) =>
            Text($"Discord didn't answer the read of channel {channelId}'s messages in guild {guildId} ({problem}), nor, before this run's look, any of the posts' requests since the {Kind(source)} notification was tried, so it waits for Discord to answer");

        public static string CheckFailed(string source, ulong channelId, ulong guildId, string problem) =>
            Text($"MinerBot's database didn't answer the check of the {Kind(source)} notification to channel {channelId} of guild {guildId} ({problem}), so the next run checks again");

        // Discord's numbers only
        public static string ReadRefused(int httpStatus, int? discordCode) => $"Discord refused the read of the channel's messages ({Answer(httpStatus, discordCode)})";

        // Such as "HTTP 403, Discord 50001"
        public static string Answer(int httpStatus, int? discordCode) =>
            NumberList((ushort)Math.Clamp(httpStatus, 0, ushort.MaxValue), discordCode is { } code and > 0 ? (uint)code : null);

        public static string TooBusy => Text($"the channel is too busy to read {PostingLimits.HistoryAfter.TotalMinutes:0} minutes past the post in {PostingLimits.HistoryPages} pages");

        static string Numbers(ushort? httpStatus, uint? discordCode) => NumberList(httpStatus, discordCode) is { Length: > 0 } numbers ? $" ({numbers})" : "";

        static string NumberList(ushort? httpStatus, uint? discordCode)
        {
            var numbers = new List<string>();

            if (httpStatus is { } status)
                numbers.Add(Text($"HTTP {status}"));

            if (discordCode is { } code)
                numbers.Add(Text($"Discord {code}"));

            return string.Join(", ", numbers);
        }

        // "2 armor, 1 map and 3 lobbies", leaving out a kind with none
        static string Items(int armor, int maps, int lobbies) =>
            And(new[] { (PostSource.Armor, armor), (PostSource.Maps, maps), (PostSource.Lobbies, lobbies) }.Where(kind => kind.Item2 > 0)
                .Select(kind => Item(kind.Item1, kind.Item2)));

        static string Item(string source, int count) => source switch
        {
            PostSource.Armor => Text($"{count} armor"),
            PostSource.Maps => Count(count, "map"),
            _ => Count(count, "lobby", "lobbies")
        };

        static string Wait(int count) => count == 1 ? "waits" : "wait";

        static string And(IEnumerable<string> parts)
        {
            var list = parts.ToList();
            return list.Count <= 1 ? string.Concat(list) : $"{string.Join(", ", list.Take(list.Count - 1))} and {list[^1]}";
        }

        static string Time(DateTime at) => at.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

        static string Count(int count, string one, string? many = null) => Text($"{count} {(count == 1 ? one : many ?? one + "s")}");

        static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
    }
}
