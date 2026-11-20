using MinerBot.Shared.Requests;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MinerBot.Shared.Tests
{
    // The fixed list of what the dashboard may ask the bot to do, written out field by field, so a change to the list is a
    // change here too
    public class RequestKindsTests
    {
        static string Minutes(TimeSpan span) => string.Create(CultureInfo.InvariantCulture, $"{span.TotalMinutes:0.##} min");

        static string Seconds(TimeSpan span) => string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:0.##} s");

        static string Row(RequestKind kind) => string.Join(" | ",
            kind.Word,
            kind.Label,
            string.Join(" ", kind.Roles),
            $"{kind.Limit} per {Minutes(kind.Window)}",
            string.Join(" ", kind.CountedWith),
            kind.PerServer ? "per server" : "-",
            $"expires {Minutes(kind.Expiry)}",
            $"acts within {Seconds(kind.ActionLimit)}",
            (kind.NeedsServer, kind.NeedsChannel) switch { (true, true) => "server and channel", (true, false) => "server", _ => "-" },
            kind.Argument.ToString(),
            kind.Job ?? "-");

        // Word | label | roles | limit | counted with | per server | expiry | action limit | server and channel | text | job
        [Fact]
        public void TheListIsThePlansTable()
        {
            Assert.Equal(
            [
                "caches.clear | Clear caches | owner admin | 30 per 60 min | caches.clear | - | expires 2 min | acts within 5 s | - | None | -",
                "job.notifications | Run the notification job | owner admin | 1 per 1 min | job.notifications | - | expires 2 min | acts within 60 s | - | None | GuildNotificationJob.NotifyAllGuilds",
                "job.colors | Run the colour job | owner | 1 per 1 min | job.colors | - | expires 2 min | acts within 60 s | - | None | ArmorJob.AddColorArrays",
                "steam.renew | Renew the Steam token | owner | 1 per 10 min | steam.renew | - | expires 2 min | acts within 45 s | - | None | -",
                "posts.pause | Pause posts | owner | 10 per 60 min | posts.pause posts.resume | - | expires 10 min | acts within 20 s | - | Reason | -",
                "posts.resume | Resume posts | owner | 10 per 60 min | posts.pause posts.resume | - | expires 10 min | acts within 20 s | - | None | -",
                "status.set | Set the status text | owner | 6 per 60 min | status.set | - | expires 2 min | acts within 30 s | - | StatusText | -",
                "steam.reconnect | Reconnect Steam | owner | 1 per 5 min | steam.reconnect | - | expires 2 min | acts within 45 s | - | None | -",
                "guild.test_message | Send a test message | owner admin server_admin | 3 per 10 min | guild.test_message | per server | expires 2 min | acts within 30 s | server and channel | None | -",
                "guild.refresh | Refresh a server's channels | owner admin server_admin | 1 per 1 min | guild.refresh | per server | expires 2 min | acts within 20 s | server | None | -",
                "steam.lookup | Look up a Steam profile link | owner admin | 60 per 60 min | steam.lookup | - | expires 2 min | acts within 30 s | - | CustomLinkName | -",
                "guild.leave | Leave a server | owner | 5 per 60 min | guild.leave | - | expires 2 min | acts within 30 s | server | None | -",
                "bot.restart | Restart the bot | owner | 1 per 10 min | bot.restart | - | expires 2 min | acts within 30 s | - | None | -",
                "owner.signin_alert | Tell the owner about a sign-in | owner admin | 20 per 60 min | owner.signin_alert | - | expires 10 min | acts within 30 s | - | None | -"
            ], RequestKinds.All.Select(Row));
        }

        // A kind is written into bot_commands.kind, recorded in the bot's events as a request's kind, and used as the audit
        // log's action, so it fits all three. A role fits actor_role, and a state or a direct message's state fits theirs.
        [Fact]
        public void WordsFitTheEventAuditAndColumnForms()
        {
            var eventKind = new Regex(@"^[a-z_]+\.[a-z_]+\z");
            var auditAction = new Regex(@"^[a-z_]+(\.[a-z_]+)+\z");
            var word = new Regex(@"^[a-z_]+\z");

            Assert.Equal(14, RequestKinds.All.Count);
            Assert.Equal(RequestKinds.All.Count, RequestKinds.All.Select(kind => kind.Word).Distinct().Count());
            Assert.Equal(RequestKinds.All.Count, RequestKinds.All.Select(kind => kind.Label).Distinct().Count());

            Assert.All(RequestKinds.All, kind =>
            {
                Assert.Matches(eventKind, kind.Word);
                Assert.Matches(auditAction, kind.Word);
                // The column's 40 characters, and an event kind's
                Assert.InRange(kind.Word.Length, 1, RequestRules.KindLimit);
                Assert.Null(RequestRules.FormProblem(new RequestFields(new string('a', 32), kind.Word, 1, "Test Owner", kind.Roles[0], null, null, null,
                    DateTime.UnixEpoch, DateTime.UnixEpoch.AddMinutes(1))));
                Assert.Same(kind, RequestKinds.Find(kind.Word));
                Assert.NotEmpty(kind.Roles);
                Assert.All(kind.Roles, role => Assert.True(RequestRoles.IsKnown(role), $"{kind.Word} names the role {role}"));
                Assert.Contains(kind.Word, kind.CountedWith);
            });

            Assert.All(RequestRoles.All, role =>
            {
                Assert.Matches(word, role);
                Assert.InRange(role.Length, 1, RequestRules.RoleLimit);
            });

            Assert.All(RequestStates.All.Concat(RequestDmStates.All), state =>
            {
                Assert.Matches(word, state);
                Assert.InRange(state.Length, 1, 10);
            });

            // Exact, as the rest of the bot's words are
            Assert.Null(RequestKinds.Find("Posts.Pause"));
            Assert.Null(RequestKinds.Find("posts.pause "));
            Assert.Null(RequestKinds.Find(null));
        }

        // A limit kinds share is one limit: each counts the other, with the same number in the same window
        [Fact]
        public void KindsCountedTogetherShareOneLimit()
        {
            Assert.All(RequestKinds.All, kind => Assert.All(kind.CountedWith, other =>
            {
                var shared = RequestKinds.Find(other);

                Assert.NotNull(shared);
                Assert.Equal(kind.CountedWith.Order(), shared.CountedWith.Order());
                Assert.Equal((kind.Limit, kind.Window, kind.PerServer), (shared.Limit, shared.Window, shared.PerServer));
            }));
        }

        // Pausing and resuming posts, and a sign-in alert, are still wanted minutes late. Anything else that happened that late
        // would surprise.
        [Fact]
        public void OnlyPauseResumeAndSignInAlertsLastTenMinutes()
        {
            Assert.Equal([RequestKinds.PostsPause, RequestKinds.PostsResume, RequestKinds.OwnerSigninAlert],
                RequestKinds.All.Where(kind => kind.Expiry == TimeSpan.FromMinutes(10)).Select(kind => kind.Word));
            Assert.All(RequestKinds.All, kind => Assert.Contains(kind.Expiry, new[] { TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10) }));
        }

        // The bot acts on one request at a time, so no action may hold the rest up for more than a minute, and one taken
        // near the end of its 2 minutes still ends while the dashboard waits
        [Fact]
        public void EveryActionEndsWithinAMinute()
        {
            Assert.All(RequestKinds.All, kind => Assert.InRange(kind.ActionLimit, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)));
        }

        // The kinds after which the bot tells the owner what it did are the ones only the Owner can ask for
        [Fact]
        public void OwnerOnlyKindsAreTheOwnerLevelOnes()
        {
            Assert.Equal(
                [RequestKinds.JobColors, RequestKinds.SteamRenew, RequestKinds.PostsPause, RequestKinds.PostsResume, RequestKinds.StatusSet,
                    RequestKinds.SteamReconnect, RequestKinds.GuildLeave, RequestKinds.BotRestart],
                RequestKinds.All.Where(kind => kind.OwnerOnly).Select(kind => kind.Word));

            Assert.Equal("the Owner", RequestKinds.Find(RequestKinds.BotRestart)!.WhoMayAsk);
            Assert.Equal("the Owner or an Admin", RequestKinds.Find(RequestKinds.CachesClear)!.WhoMayAsk);
            Assert.Equal("the Owner, an Admin or the server's own admins", RequestKinds.Find(RequestKinds.GuildRefresh)!.WhoMayAsk);
        }

        // A request can't be made to last longer than its kind allows, and must end after it was made
        [Fact]
        public void ExpiryIsTheLongestARequestMayLast()
        {
            var restart = RequestKinds.Find(RequestKinds.BotRestart)!;
            var made = new DateTime(2026, 11, 20, 15, 4, 5, 123, DateTimeKind.Utc);

            Assert.True(restart.FitsExpiry(made, made.AddMinutes(2)));
            Assert.True(restart.FitsExpiry(made, made.AddMilliseconds(1)));
            Assert.False(restart.FitsExpiry(made, made.AddMinutes(2).AddMilliseconds(1)));
            Assert.False(restart.FitsExpiry(made, made));
            Assert.False(restart.FitsExpiry(made, made.AddMilliseconds(-1)));
            Assert.True(RequestKinds.Find(RequestKinds.PostsPause)!.FitsExpiry(made, made.AddMinutes(10)));
        }
    }
}
