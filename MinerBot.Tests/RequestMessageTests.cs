using System.Diagnostics;
using System.Text.RegularExpressions;
using DataAccessLibrary.Requests.Models;
using MinerBot.Shared.Requests;
using MinerBot_2._0.Requests;
using MinerBot_2._0.Status;
using SteamWebAPIAccess;

namespace MinerBot.Tests
{
    // The owner's direct messages about the dashboard's requests: after each Owner-level one, whatever its outcome, never
    // after an Admin's, a refusal or an expiry, with who asked in a code span, and in the bot's history only the request's
    // number, its label and its outcome, because Admins read it
    [Collection("Discord")]
    public class RequestMessageTests
    {
        const ulong JaneId = 4242424242;

        sealed class ExitCodeKept : IDisposable
        {
            readonly int code = Environment.ExitCode;

            public void Dispose() => Environment.ExitCode = code;
        }

        [Theory]
        [InlineData(RequestKinds.JobColors, "The colour job ran in 0 s: added colour codes to 2 armor styles.")]
        [InlineData(RequestKinds.SteamRenew, "Steam kept the token, which ends on 2027-01-01.")]
        [InlineData(RequestKinds.PostsPause, "Posts are paused. New armor, maps and lobbies wait until they're resumed, and then go out. The reason given: `Maintenance`.")]
        [InlineData(RequestKinds.PostsResume, "Posts are resumed. The next notification run, within 5 minutes, posts what waited.")]
        [InlineData(RequestKinds.StatusSet, "The status text is set. Discord shows it while the bot is connected. The status text: `Back soon`.")]
        [InlineData(RequestKinds.SteamReconnect, "Steam disconnected and logged on again after 3 s.")]
        [InlineData(RequestKinds.GuildLeave, "Left the server. Discord then tells the bot, which deletes the server's channel settings, as when a server removes the bot. The server: `Guild` (ID 2000).")]
        [InlineData(RequestKinds.BotRestart, "The bot is restarting. It's usually back within a minute.")]
        public async Task EveryOwnerLevelKindDmsTheOwner(string word, string result)
        {
            using var exitCode = new ExitCodeKept();
            await using var bot = await RequestBot.StartAsync();
            var kind = RequestKinds.Find(word)!;
            bot.Steam.StatusAfterReconnect = new SteamStatus(SteamConnection.LoggedOn, RequestBot.Now.AddSeconds(3));

            if (word == RequestKinds.PostsResume)
                bot.Data.Settings = new BotSettingsRow { posts_paused = true, pause_reason = "Held", pause_changed_at = RequestBot.Noon.UtcDateTime };

            var id = bot.Add(word, guildId: kind.NeedsServer ? FakeDiscord.GuildId : null,
                argument: kind.Argument == RequestArgument.Reason ? "Maintenance" : kind.Argument == RequestArgument.StatusText ? "Back soon" : null);

            await bot.TickAsync();

            Assert.Equal(RequestStates.Done, bot.Row(id).state);

            // A job's time is the run's own, in whole seconds
            Assert.Equal($"{kind.Label}, asked for on the MinerBot dashboard by `Owner` (Discord ID 123456789012345678), request 1: {result}",
                Regex.Replace(Assert.Single(bot.Messages())!, @"ran in \d+ s", "ran in 0 s"));
            Assert.Equal(RequestDmStates.Sent, bot.Row(id).dm_state);
            Assert.Equal($"request 1: {kind.Label}, done", Assert.Single(bot.Events(BotEvents.AlertSent)).Detail);
        }

        // The other kinds tell nobody, and a sign-in alert is the one message it asks for
        [Fact]
        public async Task NoOtherKindDmsTheOwner()
        {
            await using var bot = await RequestBot.StartAsync();
            bot.Actions.LimitOf = kind => kind.Word == RequestKinds.GuildRefresh ? TimeSpan.FromMilliseconds(200) : kind.ActionLimit;

            var quiet = new[]
            {
                bot.Add(RequestKinds.CachesClear),
                bot.Add(RequestKinds.JobNotifications),
                bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.ChannelId),
                bot.Add(RequestKinds.GuildRefresh, guildId: FakeDiscord.GuildId),
                bot.Add(RequestKinds.SteamLookup, argument: "gaben")
            };
            var signin = bot.Add(RequestKinds.OwnerSigninAlert);

            await bot.TickAsync();

            Assert.All(quiet, id => Assert.Null(bot.Row(id).dm_state));
            Assert.All(quiet, id => Assert.NotNull(bot.Row(id).state));
            Assert.StartsWith("Dashboard sign-in: ", Assert.Single(bot.Messages()));
            Assert.Equal(RequestDmStates.Sent, bot.Row(signin).dm_state);
        }

        // A message that fails leaves the request as its action left it, and the row says the message failed. The bot's
        // history keeps the failure without the name.
        [Fact]
        public async Task AMessageThatFailsLeavesTheRequestAndSaysSo()
        {
            await using var bot = await RequestBot.StartAsync();
            bot.Discord.FailStep = "send message";
            var id = bot.Add(RequestKinds.PostsPause, argument: "Maintenance", name: "Jane Doe", actorId: JaneId);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Done, RequestDmStates.Failed), (bot.Row(id).state, bot.Row(id).dm_state));
            Assert.True(bot.Pause.Read().Paused);
            Assert.Equal("HttpException: request 1: Pause posts, done", Assert.Single(bot.Events(BotEvents.AlertFailed)).Detail);
            Assert.Empty(bot.Events(BotEvents.AlertSent));
        }

        // A sign-in alert is a message itself, so one Discord refuses is a request that failed, with the failure's type
        [Fact]
        public async Task ASignInAlertThatFailsSaysWhy()
        {
            await using var bot = await RequestBot.StartAsync();
            bot.Discord.FailStep = "send message";
            var id = bot.Add(RequestKinds.OwnerSigninAlert);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Failed, "The direct message to the owner failed (HttpException).", RequestDmStates.Failed),
                (bot.Row(id).state, bot.Row(id).result, bot.Row(id).dm_state));
        }

        // One that doesn't go within its wait may still go: unknown, and the row says how the message went once it ends
        [Fact]
        public async Task ASignInAlertThatOutlastsItsWaitIsUnknownUntilItEnds()
        {
            await using var bot = await RequestBot.StartAsync();
            var hold = new TaskCompletionSource();
            bot.Discord.SendHold = hold.Task;
            bot.Actions.LimitOf = _ => TimeSpan.FromMilliseconds(100);
            var id = bot.Add(RequestKinds.OwnerSigninAlert);

            try
            {
                await bot.TickAsync();

                Assert.Equal((RequestStates.Unknown, "The direct message to the owner didn't go within 30 seconds. It may still arrive.", (string?)null),
                    (bot.Row(id).state, bot.Row(id).result, bot.Row(id).dm_state));
            }
            finally
            {
                hold.SetResult();
            }

            var waited = Stopwatch.StartNew();

            while (bot.Row(id).dm_state == null)
            {
                Assert.True(waited.Elapsed < TimeSpan.FromSeconds(15), "The message's state was never written once it went");
                await Task.Delay(10, TestContext.Current.CancellationToken);
                await bot.TickAsync();
            }

            Assert.Equal(RequestDmStates.Sent, bot.Row(id).dm_state);
        }

        // The message says who, by name and ID, and the reason, in code spans; the bot's history holds none of it
        [Fact]
        public async Task DmNamesWhoAskedAndTheRecordHoldsNoName()
        {
            await using var bot = await RequestBot.StartAsync();
            bot.Add(RequestKinds.PostsPause, argument: "Maintenance window", name: "Jane Doe", actorId: JaneId);

            await bot.TickAsync();

            Assert.Equal("Pause posts, asked for on the MinerBot dashboard by `Jane Doe` (Discord ID 4242424242), request 1: Posts are paused. New armor, maps and lobbies "
                + "wait until they're resumed, and then go out. The reason given: `Maintenance window`.", Assert.Single(bot.Messages()));
            Assert.Equal("request 1: Pause posts, done", Assert.Single(bot.Events(BotEvents.AlertSent)).Detail);
            Assert.All(bot.Recorder.PeekEvents(1000), row => Assert.True(row.detail == null || !(row.detail.Contains("Jane") || row.detail.Contains("4242424242")
                || row.detail.Contains("Maintenance")), row.detail));
        }

        // Backticks, a line break, a character that turns the text around and a masked link, in the name and in the server's
        // name, all stay inside their code spans, so Discord shows them as text
        [Fact]
        public async Task DmNameCantBreakOutOfItsCodeSpan()
        {
            await using var bot = await RequestBot.StartAsync();
            await bot.Discord.UpdateGuildAsync("Bad`Server\u2028[x](https://y)\u202E");
            bot.Add(RequestKinds.GuildLeave, guildId: FakeDiscord.GuildId, name: "Ev`il\u2028[click](https://x)\u202Eend   name");

            await bot.TickAsync();

            var message = Assert.Single(bot.Messages())!;

            Assert.Equal(4, message.Count(character => character == '`'));
            Assert.DoesNotContain('\u2028', message);
            Assert.DoesNotContain('\u202E', message);
            Assert.Contains("by `Evil[click](https://x)end name` (Discord ID 123456789012345678)", message);
            Assert.EndsWith("The server: `BadServer[x](https://y)` (ID 2000).", message);
        }

        [Theory]
        [InlineData("Jane", "Jane")]
        [InlineData("A name that's much longer than the thirty-two characters a message keeps", "A name that's much longer than …")]
        [InlineData("\u202E\u2028`", "no name")]
        public void NamesAreCleanedAndClipped(string name, string shown)
        {
            Assert.Equal(shown, RequestTexts.CodeSpan(name, 32, "no name"));
        }

        // The sign-in's time is the request's own, and the request takes no text at all, so no address can reach the owner's
        // message or the bot's history, which Admins read
        [Fact]
        public async Task SignInAlertHoldsNoAddressAndTakesNoArgument()
        {
            await using var bot = await RequestBot.StartAsync();
            var signin = bot.Add(RequestKinds.OwnerSigninAlert, role: RequestRoles.Admin, name: "Jane", actorId: JaneId);
            var withAddress = bot.Add(RequestKinds.OwnerSigninAlert, role: RequestRoles.Admin, name: "Jane", actorId: JaneId, argument: "203.0.113.7");

            await bot.TickAsync();

            Assert.Equal("Dashboard sign-in: `Jane` (Discord ID 4242424242) signed in as Admin at 12:06 UTC on 2026-11-20 (request 1). If that wasn't them, take them "
                + "off the lists with ./setup-dashboard.sh, which ends their sessions at their next click, and check the dashboard's audit log.", Assert.Single(bot.Messages()));
            Assert.Equal((RequestStates.Done, "The owner was sent a direct message about this sign-in.", RequestDmStates.Sent),
                (bot.Row(signin).state, bot.Row(signin).result, bot.Row(signin).dm_state));
            Assert.Equal("request 1: a dashboard sign-in", Assert.Single(bot.Events(BotEvents.AlertSent)).Detail);

            Assert.Equal((RequestStates.Refused, "This request's details don't fit its kind: it takes no text. The bot didn't act on it."),
                (bot.Row(withAddress).state, bot.Row(withAddress).result));
            Assert.DoesNotContain(bot.Discord.Steps, step => step.Body.Contains("203.0.113.7"));
            Assert.DoesNotContain(bot.Recorder.PeekEvents(1000), row => row.detail != null && row.detail.Contains("203.0.113.7"));
        }

        // A row that doesn't verify tells the owner once an hour, and the Bot page lists every one
        [Fact]
        public async Task RejectionDmsOnceAnHour()
        {
            await using var bot = await RequestBot.StartAsync();
            var otherKey = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();

            ulong Forged() => bot.Data.Add(SignedRequests.Row(RequestFields.New(RequestFields.NewKey(), RequestKinds.Find(RequestKinds.CachesClear)!, SignedRequests.OwnerId,
                "Owner", RequestRoles.Owner, null, null, null, bot.Clock), otherKey));

            Forged();
            Forged();
            await bot.TickAsync();

            bot.Move(TimeSpan.FromMinutes(30));
            Forged();
            await bot.TickAsync();

            Assert.Equal(["MinerBot refused dashboard request 1: its signature didn't match the bot's key. Either something other than the dashboard wrote it, or the "
                + "request key changed and the bot wasn't recreated. ./status.sh --check on the server says which. Further refusals within the hour aren't sent; the Bot "
                + "page lists every one."], bot.Messages());

            bot.Move(TimeSpan.FromMinutes(31));
            var later = Forged();
            await bot.TickAsync();

            Assert.Equal(2, bot.Messages().Count);
            Assert.StartsWith($"MinerBot refused dashboard request {later}: ", bot.Messages()[1]);
            Assert.Equal(["request 1: refused, its signature didn't match", $"request {later}: refused, its signature didn't match"],
                bot.Events(BotEvents.AlertSent).Select(row => row.Detail));
        }

        // A row in another form was never checked against the key, and a dashboard on another key still writes its form, so
        // the message names the form, as the row and the event do, and no key. It's the hour's one message, for every reason.
        [Fact]
        public async Task ARowInAnotherFormIsntCalledASignatureThatDidntMatch()
        {
            await using var bot = await RequestBot.StartAsync();
            var otherKey = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
            var shortSignature = SignedRequests.Signed(RequestKinds.CachesClear, bot.Clock);
            shortSignature.signature = new byte[16];
            var form = bot.Data.Add(shortSignature);

            await bot.TickAsync();

            Assert.Equal("This request isn't in the form the dashboard writes, so the bot didn't act on it.", bot.Row(form).result);
            Assert.Equal([$"MinerBot refused dashboard request {form}: it isn't in the form the dashboard writes, so something other than the dashboard wrote it. "
                + "Further refusals within the hour aren't sent; the Bot page lists every one."], bot.Messages());
            Assert.Equal([$"request {form}: refused, not in the dashboard's form"], bot.Events(BotEvents.AlertSent).Select(row => row.Detail));

            bot.Move(TimeSpan.FromMinutes(30));
            bot.Data.Add(SignedRequests.Row(RequestFields.New(RequestFields.NewKey(), RequestKinds.Find(RequestKinds.CachesClear)!, SignedRequests.OwnerId, "Owner",
                RequestRoles.Owner, null, null, null, bot.Clock), otherKey));
            await bot.TickAsync();

            Assert.Single(bot.Messages());
        }

        // What the bot's history keeps of each message, at its longest, fits the 300 characters its table keeps, and so does
        // each event's detail
        [Fact]
        public void RecordedTextFitsThreeHundredCharacters()
        {
            const ulong longest = ulong.MaxValue;
            var longestKind = new string('k', 20) + "." + new string('d', 19);

            var recorded = RequestKinds.All.Select(kind => RequestTexts.OwnerRecord(kind, longest, RequestStates.Unknown))
                .Append(RequestTexts.ToldAfterRestartRecord(longest))
                .Append(RequestTexts.SigninRecord(longest))
                .Concat(Enum.GetValues<Rejection>().Select(reason => RequestTexts.RejectionRecord(longest, reason)))
                .Concat(Enum.GetValues<Rejection>().Select(reason => RequestTexts.RejectedEvent(longest, longestKind, reason)))
                .Concat(Enum.GetValues<Rejection>().Select(reason => RequestTexts.RejectedEvents(longest - 1, longest, int.MaxValue, reason)))
                .Append(RequestTexts.InterruptedEvent(longest, longestKind))
                .Append(RequestTexts.StillPaused(DateTime.MaxValue, longest))
                .Append(RequestTexts.ByRequest(longest))
                .Append(RequestTexts.RequestsOff("isn't 64 hexadecimal digits in small letters"))
                .ToList();

            Assert.All(recorded, text => Assert.True(text.Length <= 300, $"{text.Length} characters: {text}"));
            Assert.All(RequestKinds.All, kind => Assert.DoesNotContain("`", RequestTexts.OwnerRecord(kind, longest, RequestStates.Done)));
        }
    }
}
