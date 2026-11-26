using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;
using DataAccessLibrary;
using DataAccessLibrary.Requests.Models;
using Discord;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MinerBot.Shared.Requests;
using MinerBot_2._0.Requests;
using MinerBot_2._0.Status;
using SteamWebAPIAccess;

namespace MinerBot.Tests
{
    // What each kind of request does, through the request worker, against FakeDiscord, FakeSteam, FakeGetData and the bot's
    // own jobs and copy of its servers
    [Collection("Discord")]
    public class RequestActionTests
    {
        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);
        static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);

        static async Task WaitUntilAsync(Func<bool> condition, string failure)
        {
            var clock = Stopwatch.StartNew();

            while (!condition())
            {
                Assert.True(clock.Elapsed < WaitLimit, failure);
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }

        static (string? State, string? Result) Outcome(RequestBot bot, ulong id) => (bot.Row(id).state, bot.Row(id).result);

        static Task<RequestBot> StartAsync() => RequestBot.StartAsync();

        // caches.clear

        [Fact]
        public async Task CachesClearClearsBothCaches()
        {
            await using var bot = await StartAsync();
            var before = bot.Discord.Services.GetRequiredService<CacheReset>().Token;
            var id = bot.Add(RequestKinds.CachesClear);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Done, (string?)"Cleared the armor-name and latest-maps caches."), Outcome(bot, id));
            Assert.True(before.IsCancellationRequested);
        }

        // job.notifications and job.colors

        [Fact]
        public async Task NotifyNowRunsTheNotificationJobThroughJobHandler()
        {
            await using var bot = await StartAsync();
            var id = bot.Add(RequestKinds.JobNotifications);

            await bot.TickAsync();

            // Its first run, which takes the baselines, and says so, as every run says what it did
            const string Baseline = "took the first baseline of armor, maps and lobbies (2, 5 and 0 items) and posted nothing";

            Assert.Equal(RequestStates.Done, bot.Row(id).state);
            Assert.Matches(@"^The notification job ran in \d+ s: " + System.Text.RegularExpressions.Regex.Escape(Baseline) + @"\.$", bot.Row(id).result);

            var run = Assert.Single(bot.Discord.Recorder.PeekJobRuns(10));
            Assert.Equal(("GuildNotificationJob.NotifyAllGuilds", (string?)$"asked for on request 1: {Baseline}"), (run.job, run.detail));
        }

        [Fact]
        public async Task ColourJobSaysWhatItAdded()
        {
            await using var bot = await StartAsync();
            var id = bot.Add(RequestKinds.JobColors);

            await bot.TickAsync();

            Assert.Equal(RequestStates.Done, bot.Row(id).state);
            Assert.Matches(@"^The colour job ran in \d+ s: added colour codes to 2 armor styles\.$", bot.Row(id).result);
            Assert.Equal([1, 2], bot.Discord.Armor.ColorArrays.Keys.Order());
        }

        [Fact]
        public async Task ARunningJobIsntStartedAgain()
        {
            await using var bot = await StartAsync();
            await bot.Discord.RunNotificationJobAsync();
            await bot.TickAsync();
            bot.Discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(100));
            var hold = new TaskCompletionSource();
            bot.Discord.SendHold = hold.Task;

            try
            {
                await bot.Jobs.RunNowAsync("GuildNotificationJob.NotifyAllGuilds", 99, Short, TestContext.Current.CancellationToken);
                var id = bot.Add(RequestKinds.JobNotifications);

                await bot.TickAsync();

                Assert.Equal((RequestStates.Refused, (string?)"The notification job is running now, so it wasn't started again."), Outcome(bot, id));
            }
            finally
            {
                hold.SetResult();
            }
        }

        // A run that throws failed, in JobHandler's words for why: the exception's type
        [Fact]
        public async Task AJobThatFailsSaysWhy()
        {
            await using var bot = await StartAsync();
            bot.Discord.Armor.Failure = new InvalidOperationException("zz-marker");
            var id = bot.Add(RequestKinds.JobColors);

            await bot.TickAsync();

            Assert.Equal(RequestStates.Failed, bot.Row(id).state);
            Assert.Matches(@"^The colour job failed after \d+ s: InvalidOperationException\.$", bot.Row(id).result);
        }

        // Past its wait the run carries on, and the Bot page shows how it ends: the request did what it asked, starting it
        [Fact]
        public async Task AJobStillRunningAfterItsWaitIsDone()
        {
            await using var bot = await StartAsync();
            await bot.Discord.RunNotificationJobAsync();
            await bot.TickAsync();
            bot.Discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(100));
            var hold = new TaskCompletionSource();
            bot.Discord.SendHold = hold.Task;
            bot.Actions.LimitOf = _ => Short;
            var id = bot.Add(RequestKinds.JobNotifications);

            try
            {
                await bot.TickAsync();

                Assert.Equal((RequestStates.Done, (string?)"The notification job is still running after 60 seconds. The Bot page shows how its run ends."), Outcome(bot, id));
            }
            finally
            {
                hold.SetResult();
            }

            await WaitUntilAsync(() => !bot.Jobs.IsRunning("GuildNotificationJob.NotifyAllGuilds"), "The notification run never ended");
        }

        // steam.renew

        [Theory]
        [InlineData(TokenRenewalOutcome.Renewed, "Steam renewed the token, which now ends on 2027-05-01.")]
        [InlineData(TokenRenewalOutcome.Kept, "Steam kept the token, which ends on 2027-05-01.")]
        public async Task RenewSaysRenewedOrKept(TokenRenewalOutcome outcome, string result)
        {
            await using var bot = await StartAsync();
            bot.Steam.Renewal = new TokenRenewal(outcome, new DateOnly(2027, 5, 1));
            var id = bot.Add(RequestKinds.SteamRenew);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Done, (string?)result), Outcome(bot, id));
            Assert.Equal(1, bot.Steam.Renewals);
        }

        [Theory]
        [InlineData(TokenRenewalOutcome.Failed, "Steam didn't renew the token (TimeoutException). It still ends on 2027-01-01.")]
        [InlineData(TokenRenewalOutcome.NotSaved, "MinerBot couldn't save its Steam token first, so it didn't ask Steam. The owner was sent why.")]
        [InlineData(TokenRenewalOutcome.NotSignedIn, "Steam isn't logged on, so there's no token to renew. The Bot page says why.")]
        public async Task RenewFailureSaysWhy(TokenRenewalOutcome outcome, string result)
        {
            await using var bot = await StartAsync();
            bot.Steam.Renewal = new TokenRenewal(outcome, new DateOnly(2027, 1, 1), "TimeoutException");
            var id = bot.Add(RequestKinds.SteamRenew);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Failed, (string?)result), Outcome(bot, id));
        }

        // A renewal while Steam isn't logged on would only send the owner another "can't sign in" message
        [Fact]
        public async Task RenewRefusedWhileSteamIsntLoggedOn()
        {
            await using var bot = await StartAsync();
            bot.Steam.Status = new SteamStatus(SteamConnection.NeedsSignIn, RequestBot.Noon);
            var id = bot.Add(RequestKinds.SteamRenew);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Refused, (string?)"Steam isn't logged on, so there's no token to renew. The Bot page says why."), Outcome(bot, id));
            Assert.Equal(0, bot.Steam.Renewals);
        }

        [Fact]
        public async Task RenewThatNeverAnswersIsUnknown()
        {
            await using var bot = await StartAsync();
            bot.Steam.RenewalHold = new TaskCompletionSource().Task;
            bot.Actions.LimitOf = _ => Short;
            var id = bot.Add(RequestKinds.SteamRenew);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Unknown, (string?)"Steam didn't answer within 45 seconds, so the token may or may not have been renewed."), Outcome(bot, id));
        }

        // steam.reconnect

        [Fact]
        public async Task ReconnectSaysWhenSteamIsBack()
        {
            await using var bot = await StartAsync();
            bot.Steam.StatusAfterReconnect = new SteamStatus(SteamConnection.LoggedOn, RequestBot.Now.AddSeconds(3));
            var id = bot.Add(RequestKinds.SteamReconnect);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Done, (string?)"Steam disconnected and logged on again after 3 s."), Outcome(bot, id));
            Assert.Equal(1, bot.Steam.Reconnects);
        }

        [Fact]
        public async Task ReconnectThatDoesntLogOnAgainSaysSo()
        {
            await using var bot = await StartAsync();
            bot.Actions.LimitOf = _ => Short;
            var id = bot.Add(RequestKinds.SteamReconnect);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Done,
                (string?)"Steam disconnected, and MinerBot hasn't logged on again within 45 seconds. It keeps trying, and the Bot page shows when it does."), Outcome(bot, id));
        }

        [Fact]
        public async Task ReconnectRefusedWhileNotLoggedOn()
        {
            await using var bot = await StartAsync();
            bot.Steam.Status = new SteamStatus(SteamConnection.Offline, RequestBot.Noon);
            var id = bot.Add(RequestKinds.SteamReconnect);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Refused, (string?)"Steam isn't logged on, so there's nothing to reconnect: MinerBot is already trying to."), Outcome(bot, id));
            Assert.Equal(0, bot.Steam.Reconnects);
        }

        [Fact]
        public async Task ReconnectDuringASignInFails()
        {
            await using var bot = await StartAsync();
            bot.Steam.ReconnectAnswer = SteamReconnect.SigningIn;
            var id = bot.Add(RequestKinds.SteamReconnect);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Failed, (string?)"A Steam sign-in is under way, so MinerBot doesn't reconnect now."), Outcome(bot, id));
        }

        // guild.test_message

        [Fact]
        public async Task TestMessageGoesToThatServersChannelSilentlyWithNoPings()
        {
            await using var bot = await StartAsync();
            var id = bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.ChannelId);

            await bot.TickAsync();

            var sent = Assert.Single(bot.Posts());
            Assert.Equal(FakeDiscord.ChannelId, sent.SentTo);
            Assert.True(sent.AllowsNoMentions);
            Assert.True(((MessageFlags)sent.Message.GetProperty("flags").GetInt32()).HasFlag(MessageFlags.SuppressNotification));
            Assert.Equal((RequestStates.Done, "Sent a test message.", (ulong?)Assert.Single(bot.Discord.MessagesIn(FakeDiscord.ChannelId))),
                (bot.Row(id).state, bot.Row(id).result, bot.Row(id).result_number));
        }

        // Nothing of the request reaches the channel: a fixed embed and no text, whoever asked
        [Fact]
        public async Task TestMessageTextIsFixed()
        {
            await using var bot = await StartAsync();
            bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.AnnouncementChannelId, name: "zz-marker name");

            await bot.TickAsync();

            var sent = Assert.Single(bot.Posts());
            Assert.True(string.IsNullOrEmpty(sent.Content));
            Assert.Equal(["MinerBot test message"], sent.EmbedTitles);
            Assert.Equal(["MinerBot can post in this channel. This test was sent from MinerBot's dashboard."], sent.EmbedDescriptions);
            Assert.DoesNotContain("zz-marker", sent.Body);
        }

        [Fact]
        public async Task TestMessageOnlyInThatServersChannel()
        {
            await using var bot = await StartAsync();
            await bot.Discord.JoinGuildAsync(members: $"[{FakeDiscord.BotMemberJson(FakeDiscord.BotRoleId)}]");
            var posts = bot.Posts().Count;
            var id = bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.JoinedChannelId);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Refused, (string?)"That channel isn't a text or announcement channel of that server."), Outcome(bot, id));
            Assert.Equal(posts, bot.Posts().Count);
        }

        // From the cache, before Discord could refuse it and spend an invalid request
        [Theory]
        [InlineData(FakeDiscord.NoSendChannelId, "MinerBot can't post in that channel: it's missing Send Messages, Embed Links.")]
        [InlineData(FakeDiscord.NoEmbedChannelId, "MinerBot can't post in that channel: it's missing Embed Links.")]
        public async Task TestMessageNamesTheMissingPermission(ulong channelId, string result)
        {
            await using var bot = await StartAsync();
            var id = bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: channelId);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Refused, (string?)result), Outcome(bot, id));
            Assert.Empty(bot.Posts());
        }

        [Fact]
        public async Task TestMessageRefusedByDiscordFails()
        {
            await using var bot = await StartAsync();
            bot.Discord.FailStep = "send message";
            var id = bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.ChannelId);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Failed, (string?)"Discord refused the test message (error 50013)."), Outcome(bot, id));
        }

        // Discord's own trouble answers with code 0, its "general error", so the result names the HTTP status, never "error 0"
        [Fact]
        public async Task TestMessageRefusedByDiscordsServerSaysItsHttpStatus()
        {
            await using var bot = await StartAsync();
            bot.Discord.ChannelFailures[FakeDiscord.ChannelId] = (500, 0);
            var id = bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.ChannelId);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Failed, (string?)"Discord refused the test message (error 500)."), Outcome(bot, id));
        }

        // Discord.Net lists a voice channel among the text channels, and the notification job keeps doing so, but a test shows
        // whether a post can arrive, which is only in a text or announcement channel
        [Fact]
        public async Task TestMessageToAVoiceChannelIsRefused()
        {
            await using var bot = await StartAsync();
            await bot.Discord.DeliverAsync("CHANNEL_CREATE", FakeDiscord.ChannelEventJson(FakeDiscord.GuildId, 3007, 5, type: 2));
            var id = bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: 3007);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Refused, (string?)"That channel isn't a text or announcement channel of that server."), Outcome(bot, id));
        }

        [Fact]
        public async Task TestMessageThatNeverAnswersIsUnknown()
        {
            await using var bot = await StartAsync();
            var hold = new TaskCompletionSource();
            bot.Discord.SendHold = hold.Task;
            bot.Actions.LimitOf = _ => Short;
            var id = bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.ChannelId);

            try
            {
                await bot.TickAsync();
            }
            finally
            {
                hold.SetResult();
            }

            Assert.Equal((RequestStates.Unknown, (string?)"Discord didn't answer within 30 seconds, so the test message may or may not have been sent."), Outcome(bot, id));
        }

        // guild.refresh: the copy written again from the bot's cache, even unchanged

        [Fact]
        public async Task RefreshRewritesTheCopy()
        {
            await using var bot = await StartAsync();
            var copy = new FakeBotGuildData();
            var writer = Writer(bot, copy);

            await writer.TickAsync(TestContext.Current.CancellationToken);
            Assert.Single(copy.Saved);

            var id = bot.Add(RequestKinds.GuildRefresh, guildId: FakeDiscord.GuildId);

            await writer.StartAsync(TestContext.Current.CancellationToken);

            try
            {
                await bot.TickAsync();
            }
            finally
            {
                await writer.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
            }

            Assert.Equal((RequestStates.Done, (string?)"Rewrote the copy of this server and its channels at 12:06:00 UTC: 5 text and announcement channels."), Outcome(bot, id));
            Assert.Equal(2, copy.Saved.Count);
        }

        [Fact]
        public async Task RefreshOfAServerNotInTheBotIsRefused()
        {
            await using var bot = await StartAsync();
            var id = bot.Add(RequestKinds.GuildRefresh, guildId: 9999);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Refused, (string?)"The bot isn't in that server."), Outcome(bot, id));
        }

        // MinerBot's database refused the copy: the request failed, in the failure's type and MariaDB's number
        [Fact]
        public async Task RefreshWhoseCopyIsntTakenFails()
        {
            await using var bot = await StartAsync();
            var copy = new FakeBotGuildData();
            var writer = Writer(bot, copy);

            await writer.TickAsync(TestContext.Current.CancellationToken);
            copy.Failure = FakeDatabase.MySqlError(1205);
            var id = bot.Add(RequestKinds.GuildRefresh, guildId: FakeDiscord.GuildId);

            await writer.StartAsync(TestContext.Current.CancellationToken);

            try
            {
                await bot.TickAsync();
            }
            finally
            {
                await writer.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
            }

            Assert.Equal((RequestStates.Failed, (string?)"MinerBot's database didn't take the copy (MySqlException, MySQL error 1205)."), Outcome(bot, id));
        }

        // The bot left the server after the request was taken, before the writer came to it
        [Fact]
        public async Task RefreshOfAServerTheBotLeftMeanwhileFails()
        {
            await using var bot = await StartAsync();
            var copy = new FakeBotGuildData();
            var writer = Writer(bot, copy);
            await writer.TickAsync(TestContext.Current.CancellationToken);

            var id = bot.Add(RequestKinds.GuildRefresh, guildId: FakeDiscord.GuildId);
            var ticking = bot.TickAsync();
            await WaitUntilAsync(() => bot.Discord.Watcher.PeekNotes().Any(note => note.Forced), "The rewrite was never asked for");

            await bot.Discord.LeaveGuildAsync();
            await writer.TickAsync(TestContext.Current.CancellationToken);
            await ticking.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.Equal((RequestStates.Failed, (string?)"The bot left that server before its copy was rewritten."), Outcome(bot, id));
        }

        static GuildWriter Writer(RequestBot bot, FakeBotGuildData copy) =>
            new(bot.Discord.Client, bot.Discord.Watcher, copy, bot.Recorder, BotRecorderTests.Settings(botConnection: RequestBot.Connection), bot.Discord.Clock,
                new RecordingLogger<GuildWriter>())
            {
                Interval = TimeSpan.FromMilliseconds(20)
            };

        // steam.lookup

        [Fact]
        public async Task LookupReturnsTheAccountNumber()
        {
            await using var bot = await StartAsync();
            var id = bot.Add(RequestKinds.SteamLookup, argument: "gaben");

            await bot.TickAsync();

            Assert.Equal((RequestStates.Done, "That link is account [U:1:1].", (ulong?)1), (bot.Row(id).state, bot.Row(id).result, bot.Row(id).result_number));
            Assert.Equal("gaben", bot.Discord.Steam.RequestedName);
        }

        // The Kelvin sign, which the shared link pattern takes for a K, never reaches Steam
        [Fact]
        public async Task LookupRefusesTheKelvinSign()
        {
            await using var bot = await StartAsync();
            var id = bot.Add(RequestKinds.SteamLookup, argument: "\u212Aey");

            await bot.TickAsync();

            Assert.Equal((RequestStates.Refused, (string?)"This request's details don't fit its kind: the custom link's name has a character other than the letters A to Z, "
                + "digits, _ and -. The bot didn't act on it."), Outcome(bot, id));
            Assert.Null(bot.Discord.Steam.RequestedName);
        }

        [Fact]
        public async Task LookupNotFoundIsDone()
        {
            await using var bot = await StartAsync();
            bot.Discord.Steam.VanityId = null;
            var id = bot.Add(RequestKinds.SteamLookup, argument: "nobody_here");

            await bot.TickAsync();

            Assert.Equal((RequestStates.Done, "Steam has no profile with that custom link.", (ulong?)null), (bot.Row(id).state, bot.Row(id).result, bot.Row(id).result_number));
        }

        [Fact]
        public async Task LookupOfAnAccountThatIsntAPlayersFails()
        {
            await using var bot = await StartAsync();
            bot.Discord.Steam.VanityId = 103582791429521412;
            var id = bot.Add(RequestKinds.SteamLookup, argument: "some_group");

            await bot.TickAsync();

            Assert.Equal((RequestStates.Failed, (string?)"Steam's answer for that link isn't an individual player's account."), Outcome(bot, id));
        }

        // The type only: Steam's message can hold the request, the Web API key included
        [Fact]
        public async Task LookupFailureStoresTheTypeOnly()
        {
            await using var bot = await StartAsync();
            bot.Discord.Steam.Failure = new HttpRequestException("GET https://api.steampowered.com/ISteamUser/ResolveVanityURL/v1/?key=SECRETKEY failed");
            var id = bot.Add(RequestKinds.SteamLookup, argument: "gaben");

            await bot.TickAsync();

            Assert.Equal((RequestStates.Failed, (string?)"Steam didn't answer the lookup (HttpRequestException)."), Outcome(bot, id));
            Assert.DoesNotContain(bot.Logger.Entries, entry => entry.Message.Contains("SECRETKEY"));
        }

        // guild.leave

        // Discord's word that the bot left comes back through the gateway: the copy records the leave with the request, and
        // the website's channel settings go, as when a server removes the bot
        [Fact]
        public async Task LeaveCallsDiscordAndRecordsItsDetail()
        {
            await using var bot = await StartAsync();
            bot.Discord.Guilds.Guild = FakeGuildData.CreateGuild(FakeDiscord.ChannelId);
            var copy = new FakeBotGuildData();
            var writer = Writer(bot, copy);
            await writer.TickAsync(TestContext.Current.CancellationToken);

            var id = bot.Add(RequestKinds.GuildLeave, guildId: FakeDiscord.GuildId);
            await bot.TickAsync();

            Assert.Equal((RequestStates.Done, (string?)"Left the server. Discord then tells the bot, which deletes the server's channel settings, as when a server removes the bot."),
                Outcome(bot, id));
            Assert.Single(bot.Discord.Steps, step => step.Name == "leave server" && step.Endpoint == $"users/@me/guilds/{FakeDiscord.GuildId}");

            await WaitUntilAsync(() => bot.Discord.Watcher.PeekNotes().Any(note => note.LeftAt != null), "Discord's word that the bot left never came");
            await WaitUntilAsync(() => bot.Discord.StepNames.Contains(nameof(FakeGuildData.DeleteGuild)), "The website's channel settings were never deleted");
            await writer.TickAsync(TestContext.Current.CancellationToken);

            Assert.Equal((FakeDiscord.GuildId, (string?)"left on dashboard request 1"), copy.Leaves.Select(leave => (leave.GuildId, leave.Detail)).Single());
        }

        // Discord said no, so a later leave, such as the server removing the bot, isn't recorded as the request's
        [Fact]
        public async Task LeaveRefusedByDiscordKeepsNoDetail()
        {
            await using var bot = await StartAsync();
            bot.Discord.FailStep = "leave server";
            var copy = new FakeBotGuildData();
            var writer = Writer(bot, copy);
            await writer.TickAsync(TestContext.Current.CancellationToken);

            var id = bot.Add(RequestKinds.GuildLeave, guildId: FakeDiscord.GuildId);
            await bot.TickAsync();

            Assert.Equal((RequestStates.Failed, (string?)"Discord refused to let the bot leave (error 50013)."), Outcome(bot, id));

            await bot.Discord.LeaveGuildAsync();
            await writer.TickAsync(TestContext.Current.CancellationToken);

            Assert.Equal((FakeDiscord.GuildId, (string?)null), copy.Leaves.Select(leave => (leave.GuildId, leave.Detail)).Single());
        }

        [Fact]
        public async Task LeaveOfAServerTheBotIsntInIsRefused()
        {
            await using var bot = await StartAsync();
            var id = bot.Add(RequestKinds.GuildLeave, guildId: 9999);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Refused, (string?)"The bot isn't in that server."), Outcome(bot, id));
            Assert.DoesNotContain("leave server", bot.Discord.StepNames);
        }

        [Fact]
        public async Task LeaveThatNeverAnswersIsUnknown()
        {
            await using var bot = await StartAsync();
            var hold = new TaskCompletionSource();
            bot.Discord.LeaveHold = hold.Task;
            bot.Actions.LimitOf = _ => Short;
            var id = bot.Add(RequestKinds.GuildLeave, guildId: FakeDiscord.GuildId);

            try
            {
                await bot.TickAsync();
            }
            finally
            {
                hold.SetResult();
            }

            Assert.Equal((RequestStates.Unknown, (string?)"Discord didn't answer within 30 seconds, so the bot may or may not have left. The servers page shows which."),
                Outcome(bot, id));
        }

        // status.set: a custom status, which replaces "Playing", sent again at every connection

        static List<SentPresence> Sent(RequestBot bot, string? text) => bot.Discord.Presences.Where(presence => presence.State == text && presence.Type == 4).ToList();

        [Fact]
        public async Task StatusTextIsSentAsACustomStatus()
        {
            await using var bot = await StartAsync();
            var id = bot.Add(RequestKinds.StatusSet, argument: "Live check of PR 9");

            await bot.TickAsync();

            Assert.Equal((RequestStates.Done, (string?)"The status text is set. Discord shows it while the bot is connected."), Outcome(bot, id));
            Assert.Equal(new SentPresence(3, "Custom Status", 4, "Live check of PR 9"), bot.Discord.Presences[^1]);
            Assert.Equal("Live check of PR 9", bot.Presence.Wanted);
        }

        // A RESUME carries no presence, so the text goes again once the connection is back
        [Fact]
        public async Task StatusIsSentAgainAfterAResume()
        {
            await using var bot = await StartAsync();
            bot.Add(RequestKinds.StatusSet, argument: "Live check of PR 9");
            await bot.TickAsync();
            Assert.Single(Sent(bot, "Live check of PR 9"));

            await bot.Discord.DropAndResumeAsync();

            await WaitUntilAsync(() => Sent(bot, "Live check of PR 9").Count == 2, "The status text wasn't sent again after the resume");
            Assert.Equal(3, bot.Discord.Presences[^1].Op);
        }

        // A frame sent while the socket is down is dropped without a word, and the next session's IDENTIFY carries the text
        [Fact]
        public async Task StatusSetWhileDisconnectedArrivesWithTheNextSession()
        {
            await using var bot = await StartAsync();
            await bot.Discord.StopGatewayAsync();

            await bot.Presence.SetAsync("While away");
            Assert.Empty(Sent(bot, "While away"));

            await bot.Discord.StartNewSessionAsync();

            Assert.Equal(2, bot.Discord.Presences.First(presence => presence.State == "While away").Op);
        }

        [Fact]
        public async Task ResetGoesBackToPlaying()
        {
            await using var bot = await StartAsync();
            bot.Add(RequestKinds.StatusSet, argument: "Back soon");
            var reset = bot.Add(RequestKinds.StatusSet);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Done, (string?)"The status is back to Playing Murder Miners on my PC."), Outcome(bot, reset));
            Assert.Equal(new SentPresence(3, "Murder Miners on my PC", 0, null), bot.Discord.Presences[^1]);
            Assert.Null(bot.Presence.Wanted);
        }

        // Read back at the run's first tick, so a restart keeps the text
        [Fact]
        public async Task SavedStatusIsRestoredAtStart()
        {
            await using var bot = await StartAsync();
            bot.Data.Settings = new BotSettingsRow { status_text = "Saved text", status_changed_at = RequestBot.Noon.UtcDateTime };

            await bot.TickAsync();

            Assert.Equal("Saved text", bot.Presence.Wanted);
            Assert.Single(Sent(bot, "Saved text"));
        }

        // A row written by hand, or one an older rule let through, isn't shown
        [Fact]
        public async Task StoredTextThatBreaksTheRulesIsNotApplied()
        {
            await using var bot = await StartAsync();
            bot.Data.Settings = new BotSettingsRow { status_text = "Visit https://example.com", status_changed_at = RequestBot.Noon.UtcDateTime };

            await bot.TickAsync();

            Assert.Null(bot.Presence.Wanted);
            Assert.Empty(Sent(bot, "Visit https://example.com"));
            Assert.True(bot.Logger.Logged(LogLevel.Warning, "The status text MinerBot's database holds breaks the rules (the status text has a link), so the bot shows its default status"));
        }

        // One written by hand with U+FFFE, which .NET's normalization throws on, is refused the same way, and the requests go
        // on: had the rule thrown, every tick would have failed at the settings, before any request
        [Fact]
        public async Task StoredTextWithACharacterNormalizationRefusesIsNotAppliedAndRequestsGoOn()
        {
            await using var bot = await StartAsync();
            bot.Data.Settings = new BotSettingsRow { status_text = "Back soon\uFFFE", status_changed_at = RequestBot.Noon.UtcDateTime };
            var id = bot.Add(RequestKinds.CachesClear);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Done, (string?)"Cleared the armor-name and latest-maps caches."), Outcome(bot, id));
            Assert.Null(bot.Presence.Wanted);
            Assert.True(bot.Logger.Logged(LogLevel.Warning, "The status text MinerBot's database holds breaks the rules (the status text has a broken character), so the bot shows its default status"));
        }

        // Read again at every tick for a while after a change that didn't end done, such a text is still logged only once
        [Fact]
        public async Task StoredTextThatBreaksTheRulesIsLoggedOnce()
        {
            await using var bot = await StartAsync();
            bot.Data.Settings = new BotSettingsRow { status_text = "Visit https://example.com", status_changed_at = RequestBot.Noon.UtcDateTime };
            bot.Data.FailNext(nameof(FakeRequestData.SaveStatus), FakeDatabase.MySqlError(1205));
            bot.Add(RequestKinds.StatusSet, argument: "Not saved");

            for (var tick = 0; tick < 5; tick++)
                await bot.TickAsync();

            Assert.True(bot.Data.Calls.Count(call => call == nameof(FakeRequestData.GetSettings)) >= 5);
            Assert.Single(bot.Logger.Entries, entry => entry.Message.StartsWith("The status text MinerBot's database holds breaks the rules", StringComparison.Ordinal));
            Assert.Null(bot.Presence.Wanted);
        }

        // Saved first, then shown: a text MinerBot's database didn't take would vanish at the next restart
        [Fact]
        public async Task StatusIsntChangedWhenTheSaveFails()
        {
            await using var bot = await StartAsync();
            bot.Data.FailNext(nameof(FakeRequestData.SaveStatus), FakeDatabase.MySqlError(1205));
            var id = bot.Add(RequestKinds.StatusSet, argument: "Not saved");

            await bot.TickAsync();

            Assert.Equal((RequestStates.Failed, (string?)"MinerBot's database didn't take the change, so the status text wasn't changed."), Outcome(bot, id));
            Assert.Null(bot.Presence.Wanted);
            Assert.Empty(Sent(bot, "Not saved"));
        }

        // Its answer didn't come, though it landed: the status waits, and the next tick reads the text back and shows it
        [Fact]
        public async Task AStatusThatDidntConfirmIsReadBack()
        {
            await using var bot = await StartAsync();
            bot.Data.FailNext(nameof(FakeRequestData.SaveStatus), new TimeoutException("The database didn't answer within 15 seconds"), lands: true);
            var id = bot.Add(RequestKinds.StatusSet, argument: "Read back");

            await bot.TickAsync();

            Assert.Equal((RequestStates.Unknown, (string?)"MinerBot's database didn't confirm the change within 15 seconds. The bot reads it back and follows what it holds."),
                Outcome(bot, id));
            Assert.Null(bot.Presence.Wanted);
            Assert.Empty(Sent(bot, "Read back"));

            await bot.TickAsync();

            Assert.Equal("Read back", bot.Presence.Wanted);
            Assert.Single(Sent(bot, "Read back"));
        }

        // Its answer was a lost connection, which may follow the server taking the text: unknown, never "wasn't changed", in
        // the row and the owner's message, and the next tick reads the text back and shows it. MySql.Data reports a lost
        // connection with no number and the stream's error inside, and 2013 is the client error for one.
        [Theory]
        [InlineData(0, true)]
        [InlineData(2013, false)]
        public async Task AStatusWhoseAnswerWasLostIsUnknownAndReadBack(int number, bool streamError)
        {
            await using var bot = await StartAsync();
            bot.Data.FailNext(nameof(FakeRequestData.SaveStatus), FakeDatabase.MySqlError(number, streamError ? new IOException("The connection was closed") : null), lands: true);
            var id = bot.Add(RequestKinds.StatusSet, argument: "Read back");

            await bot.TickAsync();

            const string Lost = "MinerBot's database didn't confirm the change: the call failed before its answer came. The bot reads it back and follows what it holds.";
            Assert.Equal((RequestStates.Unknown, (string?)Lost), Outcome(bot, id));
            Assert.EndsWith($"request 1: {Lost} The status text: `Read back`.", Assert.Single(bot.Messages()));
            Assert.Null(bot.Presence.Wanted);

            await bot.TickAsync();

            Assert.Equal("Read back", bot.Presence.Wanted);
            Assert.Single(Sent(bot, "Read back"));
        }

        // An exception that isn't MySQL's, such as the NullReferenceException MySql.Data throws for a statement that a KILL
        // QUERY ended, doesn't say the server refused the text either: unknown, as a lost answer is, and the next tick reads
        // the text back and shows it
        [Fact]
        public async Task AStatusThatEndsWithAnExceptionNotMySqlsIsUnknownAndReadBack()
        {
            await using var bot = await StartAsync();
            bot.Data.FailNext(nameof(FakeRequestData.SaveStatus), new NullReferenceException(), lands: true);
            var id = bot.Add(RequestKinds.StatusSet, argument: "Read back");

            await bot.TickAsync();

            Assert.Equal((RequestStates.Unknown, (string?)"MinerBot's database didn't confirm the change: the call failed before its answer came. The bot reads it back and follows what it holds."),
                Outcome(bot, id));
            Assert.Null(bot.Presence.Wanted);

            await bot.TickAsync();

            Assert.Equal("Read back", bot.Presence.Wanted);
        }
    }
}
