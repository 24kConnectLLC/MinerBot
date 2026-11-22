using DataAccessLibrary.Requests.Models;
using MinerBot.Dashboard.Requests;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Tests
{
    // The bot's limit, checked before the dashboard writes a request the bot would refuse for it, from the rows the two reads
    // give: claims counted as the bot counts them, and the requests that wait counting too, since the bot will count them
    // once it takes them. BotRequestReadsTests compares the same rule with the bot's own count on a real database.
    public class BotLimitsTests
    {
        static readonly DateTime Now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        const ulong ServerA = 1000000000000000701, ServerB = 1000000000000000702;

        static RequestKind Kind(string word) => RequestKinds.Find(word)!;

        static ClaimRow Claim(string kind, DateTime? at, ulong? guild = null, ulong id = 0) => new() { id = id, kind = kind, claimed_at = at, guild_id = guild };

        static WaitingRow Waiting(ulong id, string kind, DateTime? asked, ulong? guild = null) =>
            new() { id = id, kind = kind, guild_id = guild, created_at = asked, expires_at = asked + Kind(kind).Expiry };

        static BotLimitCheck Check(string kind, IReadOnlyList<ClaimRow> claims, IReadOnlyList<WaitingRow>? waiting = null, ulong? guild = null) =>
            BotLimits.Check(Kind(kind), guild, claims, waiting ?? [], Now);

        // Every claim in the kind's window counts, the window's first moment included, as CountClaims' claimed_at >= @since
        // does, and one a millisecond older doesn't. At the limit, the oldest counted claim says when another fits, in the
        // bot's own words. Fails if the window isn't the kind's, or its start doesn't count.
        [Fact]
        public void ClaimsInTheWindowCountAsTheBotCountsThem()
        {
            var restart = Kind(RequestKinds.BotRestart);

            var edge = Check(RequestKinds.BotRestart, [Claim(RequestKinds.BotRestart, Now - restart.Window)]);
            Assert.Equal((BotLimitState.Limit, 1, (DateTime?)Now.AddMinutes(-10), (DateTime?)Now), (edge.State, edge.Count, edge.Oldest, edge.After));
            Assert.Equal("The bot takes this at most once every 10 minutes, and last took one at 11:50:00 UTC, so nothing was sent. Try again after 12:00:00 UTC.",
                RequestWords.BotLimit(restart, edge));

            var older = Check(RequestKinds.BotRestart, [Claim(RequestKinds.BotRestart, Now - restart.Window - TimeSpan.FromMilliseconds(1))]);
            Assert.Equal((BotLimitState.Available, 0), (older.State, older.Count));

            // A claim of an hour ago counts for a kind whose window is an hour, and not for one whose window is a minute
            Assert.Equal(BotLimitState.Available, Check(RequestKinds.JobNotifications, [Claim(RequestKinds.JobNotifications, Now.AddSeconds(-61))]).State);
            Assert.Equal(BotLimitState.Limit, Check(RequestKinds.JobNotifications, [Claim(RequestKinds.JobNotifications, Now.AddSeconds(-59))]).State);

            // 30 cache clears an hour: 29 leave room, and the 30th fills it, the oldest saying when
            var clears = Enumerable.Range(1, 30).Select(minutes => Claim(RequestKinds.CachesClear, Now.AddMinutes(-minutes))).ToList();
            Assert.Equal(BotLimitState.Available, Check(RequestKinds.CachesClear, clears.Take(29).ToList()).State);

            var full = Check(RequestKinds.CachesClear, clears);
            Assert.Equal((BotLimitState.Limit, 30, (DateTime?)Now.AddMinutes(-30), (DateTime?)Now.AddMinutes(30)), (full.State, full.Count, full.Oldest, full.After));
            Assert.Equal("The bot takes this at most 30 times an hour and has taken 30 since 11:30:00 UTC, so nothing was sent. Try again after 12:30:00 UTC.",
                RequestWords.BotLimit(Kind(RequestKinds.CachesClear), full));

            // Another kind's claims count for nothing here
            Assert.Equal(BotLimitState.Available, Check(RequestKinds.BotRestart, [Claim(RequestKinds.CachesClear, Now)]).State);
        }

        // Pausing and resuming posts share 10 an hour, so claims of either count toward both. Fails if each kind is counted
        // alone.
        [Fact]
        public void KindsCountedTogetherShareALimit()
        {
            var claims = Enumerable.Range(1, 6).Select(minutes => Claim(RequestKinds.PostsPause, Now.AddMinutes(-minutes)))
                .Concat(Enumerable.Range(7, 4).Select(minutes => Claim(RequestKinds.PostsResume, Now.AddMinutes(-minutes))))
                .ToList();

            Assert.Equal(BotLimitState.Limit, Check(RequestKinds.PostsPause, claims).State);
            Assert.Equal(BotLimitState.Limit, Check(RequestKinds.PostsResume, claims).State);
            Assert.Equal(10, Check(RequestKinds.PostsResume, claims).Count);
            Assert.Equal(BotLimitState.Available, Check(RequestKinds.PostsPause, claims.Skip(1).ToList()).State);

            // A kind counted alone isn't touched by them
            Assert.Equal(BotLimitState.Available, Check(RequestKinds.StatusSet, claims).State);
        }

        // Test messages and refreshes are counted for each server apart, and a leave, which isn't, counts every server's.
        // Fails if the server is ignored, or used for a kind that isn't counted by server.
        [Fact]
        public void PerServerKindsCountEachServerApart()
        {
            var tests = Enumerable.Range(1, 3).Select(minutes => Claim(RequestKinds.GuildTestMessage, Now.AddMinutes(-minutes), ServerA)).ToList();
            Assert.Equal(BotLimitState.Limit, Check(RequestKinds.GuildTestMessage, tests, guild: ServerA).State);
            Assert.Equal(BotLimitState.Available, Check(RequestKinds.GuildTestMessage, tests, guild: ServerB).State);
            Assert.Equal("The bot takes this at most 3 times every 10 minutes, for each server, and has taken 3 since 11:57:00 UTC, so nothing was sent. Try again after 12:07:00 UTC.",
                RequestWords.BotLimit(Kind(RequestKinds.GuildTestMessage), Check(RequestKinds.GuildTestMessage, tests, guild: ServerA)));

            ClaimRow[] refresh = [Claim(RequestKinds.GuildRefresh, Now.AddSeconds(-30), ServerA)];
            Assert.Equal(BotLimitState.Limit, Check(RequestKinds.GuildRefresh, refresh, guild: ServerA).State);
            Assert.Equal(BotLimitState.Available, Check(RequestKinds.GuildRefresh, refresh, guild: ServerB).State);
            Assert.Equal("The bot takes this at most once a minute, for each server, and last took one at 11:59:30 UTC, so nothing was sent. Try again after 12:00:30 UTC.",
                RequestWords.BotLimit(Kind(RequestKinds.GuildRefresh), Check(RequestKinds.GuildRefresh, refresh, guild: ServerA)));

            var leaves = Enumerable.Range(1, 5).Select(minutes => Claim(RequestKinds.GuildLeave, Now.AddMinutes(-minutes), minutes % 2 == 0 ? ServerA : ServerB)).ToList();
            Assert.Equal(BotLimitState.Limit, Check(RequestKinds.GuildLeave, leaves, guild: ServerA).State);
        }

        // A request that waits isn't counted by the bot yet, but will be once it's taken, so with the claims it fills the
        // limit here: the check names the oldest that waits, and when it expires. Fails if waiting requests are left out.
        [Fact]
        public void WaitingRowsCount()
        {
            var check = Check(RequestKinds.BotRestart, [], [Waiting(43, RequestKinds.BotRestart, Now.AddSeconds(-10)), Waiting(41, RequestKinds.BotRestart, Now.AddSeconds(-30))]);

            Assert.Equal(BotLimitState.Waiting, check.State);
            Assert.Equal(41UL, check.Waiting?.id);
            Assert.Equal(Now.AddSeconds(90), check.After);
            Assert.Equal("A request like this is already waiting for the bot: request 41, asked for at 11:59:30 UTC. The bot takes it within seconds while it's on Discord, "
                + "or it expires at 12:01:30 UTC. Nothing more was sent.", RequestWords.Waiting(check.Waiting!));

            // Another kind's, or another server's for a kind counted by server, don't count
            Assert.Equal(BotLimitState.Available, Check(RequestKinds.BotRestart, [], [Waiting(42, RequestKinds.CachesClear, Now)]).State);
            Assert.Equal(BotLimitState.Available, Check(RequestKinds.GuildRefresh, [], [Waiting(44, RequestKinds.GuildRefresh, Now, ServerA)], ServerB).State);

            // Two test messages taken and one waiting fill a server's 3; two taken and none waiting leave room
            ClaimRow[] taken = [Claim(RequestKinds.GuildTestMessage, Now.AddMinutes(-2), ServerA), Claim(RequestKinds.GuildTestMessage, Now.AddMinutes(-1), ServerA)];
            var filled = Check(RequestKinds.GuildTestMessage, taken, [Waiting(45, RequestKinds.GuildTestMessage, Now.AddSeconds(-5), ServerA)], ServerA);
            Assert.Equal((BotLimitState.Waiting, 3, (ulong?)45), (filled.State, filled.Count, filled.Waiting?.id));
            Assert.Equal(BotLimitState.Available, Check(RequestKinds.GuildTestMessage, taken, guild: ServerA).State);

            // A kind's limit that the claims fill alone is the bot's limit, whatever waits
            Assert.Equal(BotLimitState.Limit, Check(RequestKinds.BotRestart, [Claim(RequestKinds.BotRestart, Now.AddMinutes(-1))], [Waiting(46, RequestKinds.BotRestart, Now)]).State);
        }

        // The waiting rows are read first, so a request the bot takes between the two reads is in both: it counts once, as
        // taken. A server's test message taken 5 minutes ago, and request 42, waiting at the first read and taken at the
        // second, are 2 of its 3, so one more fits, as the bot would take it; and a waiting request the claims don't hold
        // still counts. Fails if a request in both reads counts twice, which would refuse it as waiting for request 42.
        [Fact]
        public void ARequestInBothReadsCountsOnce()
        {
            ClaimRow[] claims = [Claim(RequestKinds.GuildTestMessage, Now.AddMinutes(-5), ServerA, id: 40), Claim(RequestKinds.GuildTestMessage, Now, ServerA, id: 42)];
            WaitingRow[] waiting = [Waiting(42, RequestKinds.GuildTestMessage, Now.AddSeconds(-2), ServerA)];

            var check = Check(RequestKinds.GuildTestMessage, claims, waiting, ServerA);
            Assert.Equal((BotLimitState.Available, 2), (check.State, check.Count));

            var another = Check(RequestKinds.GuildTestMessage, claims, [.. waiting, Waiting(43, RequestKinds.GuildTestMessage, Now.AddSeconds(-1), ServerA)], ServerA);
            Assert.Equal((BotLimitState.Waiting, 3, (ulong?)43), (another.State, another.Count, another.Waiting?.id));
        }

        // The claims read goes back an hour, so no kind's window may be longer
        [Fact]
        public void NoWindowIsLongerThanTheRead()
        {
            Assert.Equal(TimeSpan.FromHours(1), BotLimits.Longest);
            Assert.All(RequestKinds.All, kind => Assert.True(kind.Window <= BotLimits.Longest, $"{kind.Word}'s window is longer than the claims read"));
        }

        // A claim whose time MySql.Data couldn't read counts, as the bot counts it, but can't be the oldest or say when
        // another fits, and the words say so rather than make a time up
        [Fact]
        public void AnUnreadableTimeCountsButSaysNoTime()
        {
            var restart = Kind(RequestKinds.BotRestart);
            var check = Check(RequestKinds.BotRestart, [Claim(RequestKinds.BotRestart, null)]);

            Assert.Equal((BotLimitState.Limit, 1, (DateTime?)null, (DateTime?)null), (check.State, check.Count, check.Oldest, check.After));
            Assert.Equal("The bot takes this at most once every 10 minutes, and last took one at an unreadable time, so nothing was sent. Try again later.",
                RequestWords.BotLimit(restart, check));

            var clears = Enumerable.Range(1, 29).Select(minutes => Claim(RequestKinds.CachesClear, Now.AddMinutes(-minutes))).Append(Claim(RequestKinds.CachesClear, null)).ToList();
            var full = Check(RequestKinds.CachesClear, clears);
            Assert.Equal((BotLimitState.Limit, 30, (DateTime?)Now.AddMinutes(-29)), (full.State, full.Count, full.Oldest));

            var waiting = Check(RequestKinds.BotRestart, [], [Waiting(47, RequestKinds.BotRestart, null)]);
            Assert.Equal(BotLimitState.Waiting, waiting.State);
            Assert.Contains("request 47, asked for at an unreadable time", RequestWords.Waiting(waiting.Waiting!));
        }
    }
}
