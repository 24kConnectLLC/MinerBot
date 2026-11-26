using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DataAccessLibrary;
using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Blacklist;
using DataAccessLibrary.Blacklist.Models;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Tests.Database;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MinerBot.Dashboard.Armor;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Blacklist;
using MinerBot.Dashboard.Pages;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Servers;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Armor;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Tests
{
    // Every answer to a form is a redirect, so no browser holds a POST that a reload, Back and Resubmit, or a restored tab
    // would send again: a sent request's own address, Forbid's /denied, and for anything else the page's own address with a
    // form's notice, whose GET draws today's answer with today's status. The fixed checks' 400s stay as they are, since
    // sending one again gets the same answer. A form that fails, or that the rate limiter refuses, is sent to the error page
    // by GET.
    [Collection("Database")]
    public partial class FormAnswerTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        // The pages whose forms send requests, or act, as the theory names them
        const string Controls = "controls";
        const string Server = "server";
        const string Leave = "leave";
        const string Review = "review";
        const string Add = "add";
        const string Switches = "switches";

        // The answers that aren't a notice: sent, or an armor action done, refused for the role, and a form someone made up
        const string Sent = "Sent";
        const string Done = "Done";
        const string Forbidden = "Forbidden";
        const string MadeUp = "MadeUp";

        // The pages' own answers, beside the sender's: a server's channel it doesn't choose now, or can't take a test
        // message now, and a server MinerBot isn't in; armor that isn't approved now, or that the bot won't post now, and
        // each way an action is refused, or wasn't confirmed; the add's fields, its lookup at each stage, an account on the
        // blacklist already, with other details or with these, and an add that wasn't confirmed
        const string NotChosenNow = "NotChosenNow";
        const string ChannelNotNow = "ChannelNotNow";
        const string NotIn = "NotIn";
        const string NotApprovedNow = "NotApprovedNow";
        const string AnnounceNotNow = "AnnounceNotNow";
        const string Changed = "Changed";
        const string Already = "Already";
        const string Blocked = "Blocked";
        const string Unticked = "Unticked";
        const string ActionUnsure = "ActionUnsure";
        const string Fields = "Fields";
        const string LookupNone = "LookupNone";
        const string LookupWaiting = "LookupWaiting";
        const string LookupSent = "LookupSent";
        const string LookupUnsure = "LookupUnsure";
        const string Exists = "Exists";
        const string AlreadyAdded = "AlreadyAdded";
        const string AddUnsure = "AddUnsure";

        // A move back to pending from Approved of armor the bot's record shows posted, and the switches' own answers: nothing
        // to save, nothing to save from a form whose change was saved since it was drawn, a form drawn before another save,
        // the person's limit, and a save the database didn't confirm
        const string Posted = "Posted";
        const string Unchanged = "Unchanged";
        const string SwitchesAlready = "SwitchesAlready";
        const string Stale = "Stale";
        const string Limit = "Limit";
        const string SwitchesUnsure = "SwitchesUnsure";

        // Each page's forms, pressed for each way they can be answered: every one of the controls page's, and for the other
        // pages, the sender's answers by their families and every answer of the page's own
        public static TheoryData<string, string> Answers => new()
        {
            { Controls, Sent },
            { Controls, Forbidden },
            { Controls, MadeUp },
            { Controls, nameof(SendOutcome.SentBefore) },
            { Controls, nameof(SendRefusal.KeyTaken) },
            { Controls, nameof(SendRefusal.Off) },
            { Controls, nameof(SendRefusal.NotFresh) },
            { Controls, nameof(SendRefusal.NotConfirmed) },
            { Controls, nameof(SendRefusal.Rule) },
            { Controls, nameof(SendRefusal.PersonLimit) },
            { Controls, nameof(SendRefusal.BotLimit) },
            { Controls, nameof(SendRefusal.Waiting) },
            { Controls, nameof(SendOutcome.Unsure) },
            { Server, Sent },
            { Server, MadeUp },
            { Server, nameof(SendOutcome.SentBefore) },
            { Server, nameof(SendRefusal.Off) },
            { Server, nameof(SendRefusal.PersonLimit) },
            { Server, nameof(SendRefusal.Waiting) },
            { Server, nameof(SendOutcome.Unsure) },
            { Server, NotChosenNow },
            { Server, ChannelNotNow },
            { Server, NotIn },
            { Leave, Sent },
            { Leave, Forbidden },
            { Leave, MadeUp },
            { Leave, nameof(SendRefusal.NotConfirmed) },
            { Leave, nameof(SendRefusal.NotFresh) },
            { Leave, nameof(SendOutcome.Unsure) },
            { Leave, NotIn },
            { Review, Sent },
            { Review, MadeUp },
            { Review, nameof(SendRefusal.Waiting) },
            { Review, nameof(SendOutcome.Unsure) },
            { Review, NotApprovedNow },
            { Review, AnnounceNotNow },
            { Review, Done },
            { Review, Changed },
            { Review, Already },
            { Review, Blocked },
            { Review, Unticked },
            { Review, ActionUnsure },
            { Review, Posted },
            { Add, Sent },
            { Add, MadeUp },
            { Add, Fields },
            { Add, LookupNone },
            { Add, LookupWaiting },
            { Add, LookupSent },
            { Add, LookupUnsure },
            { Add, Exists },
            { Add, AlreadyAdded },
            { Add, AddUnsure },
            { Switches, Done },
            { Switches, Forbidden },
            { Switches, MadeUp },
            { Switches, NotIn },
            { Switches, Unchanged },
            { Switches, SwitchesAlready },
            { Switches, Stale },
            { Switches, Limit },
            { Switches, SwitchesUnsure }
        };

        // A form pressed, and for a form's notice the status its GET answers with, the words it shows, and the key it draws
        // the form with after a save the database didn't confirm
        sealed record Pressed(TestBrowser Browser, HttpResponseMessage Response, HttpStatusCode? Drawn = null, string? Says = null, string? Key = null);

        static async Task<TestBrowser> SignedInAsync(DashboardFactory factory, TestUser user, string address)
        {
            var browser = factory.Visitor(address);
            await SignInSteps.SignedInAsync(factory, browser, user);
            return browser;
        }

        // A page's text without its markup, so a sentence reads as one string
        static string Text(string html) => Regex.Replace(Regex.Replace(Regex.Replace(WebUtility.HtmlDecode(html), "<[^>]+>", " "), "\\s+", " "), " ([.,:])(?= |$)", "$1");

        static string RequestId(string html)
        {
            var match = RequestIdText().Match(html);
            Assert.True(match.Success, "The page has no request ID");
            return match.Groups[1].Value;
        }

        // Every way a form on these pages can be answered is a redirect, apart from a form someone made up: Sent to the
        // request's own address, or the action's or the entry's, Forbidden to /denied, and anything else to the page's own
        // address with ?notice=, with no body and the notice's cookie, whose GET answers with the status and words the POST
        // itself used to, and after a save the database didn't confirm, draws the form with the key it was sent with. Fails
        // if any answer is a page drawn on the POST.
        [Theory]
        [MemberData(nameof(Answers))]
        public async Task EveryAnswerToAFormIsARedirect(string page, string outcome)
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db, Options(outcome));

            var pressed = page switch
            {
                Controls => await PressControlAsync(db, factory, outcome),
                Server => await PressServerAsync(db, factory, outcome),
                Leave => await PressLeaveAsync(db, factory, outcome),
                Review => await PressReviewAsync(db, factory, outcome),
                Add => await PressAddAsync(db, factory, outcome),
                Switches => await PressSwitchesAsync(db, factory, outcome),
                _ => throw new ArgumentException($"There's no page called {page}", nameof(page))
            };
            var response = pressed.Response;
            var location = SignInSteps.PathOf(response.Headers.Location) ?? "";

            switch (outcome)
            {
                case Sent or Done:
                    Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                    Assert.DoesNotContain("notice=", location, StringComparison.Ordinal);
                    Assert.False(RequestSteps.TouchesNotice(response));
                    break;

                case Forbidden:
                    Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                    Assert.StartsWith("/denied", location, StringComparison.Ordinal);
                    Assert.False(RequestSteps.TouchesNotice(response));
                    break;

                case MadeUp:
                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                    Assert.False(RequestSteps.TouchesNotice(response));
                    break;

                default:
                    var answer = await RequestSteps.AnsweredAsync(pressed.Browser, response);
                    var html = await answer.Content.ReadAsStringAsync(Token);

                    Assert.Equal(pressed.Drawn, answer.StatusCode);
                    Assert.Contains(pressed.Says!, Text(html));
                    PageTests.AssertAccessible($"the {page} page's notice for {outcome}", html);

                    if (pressed.Key is { } key)
                        Assert.Contains($"name=\"formkey\" value=\"{key}\"", html);

                    break;
            }
        }

        // The app as each press needs it: requests off, or a save the database doesn't confirm, the bot's or the website's
        static DashboardFactoryOptions? Options(string outcome) => outcome switch
        {
            nameof(SendRefusal.Off) => new DashboardFactoryOptions { Settings = new Dictionary<string, string?> { ["Dashboard:RequestSigningKey"] = "" } },
            nameof(SendOutcome.Unsure) or LookupUnsure => new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider =>
                    new UnconfirmedQueue(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider))))
            },
            ActionUnsure => new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IArmorSubmissionData>(provider =>
                    new LateArmor(ActivatorUtilities.CreateInstance<ArmorSubmissionData>(provider))))
            },
            AddUnsure => new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IBlacklistData>(provider =>
                    new LateBlacklist(ActivatorUtilities.CreateInstance<BlacklistData>(provider))))
            },
            SwitchesUnsure => new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IGuildSwitchData>(provider =>
                    new LateSwitches(ActivatorUtilities.CreateInstance<GuildSwitchData>(provider))))
            },
            _ => null
        };

        // A form's key as the page drew it, by the address its form's action names, or null when the page has no such form
        static string? KeyOf(string html, string action)
        {
            var match = Regex.Match(html, $"action=\"{Regex.Escape(action)}\"[^>]*>\\s*<input type=\"hidden\" name=\"formkey\" value=\"([0-9a-f]{{32}})\" />");
            return match.Success ? match.Groups[1].Value : null;
        }

        // A form as a browser sends it from the dashboard, from the page given: its token, the key the page drew the form with
        // unless another is given, and its other fields
        static async Task<HttpResponseMessage> PostFormAsync(TestBrowser browser, string page, string action, string? key = null, params (string Name, string Value)[] fields)
        {
            var html = await browser.Client.GetStringAsync(page, Token);
            var sent = new List<KeyValuePair<string, string>> { new("formkey", key ?? KeyOf(html, action) ?? RequestFields.NewKey()) };
            sent.AddRange(fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)));

            return await SignInSteps.PostAsync(browser, action, SignInSteps.FormToken(html), origin: DashboardFactory.PublicOrigin, fields: sent);
        }

        // The controls page's forms, the Owner's unless the role is what's refused
        static async Task<Pressed> PressControlAsync(MariaDbFixture db, DashboardFactory factory, string outcome)
        {
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.180");
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var page = await RequestSteps.ControlsAsync(owner);

            Task<HttpResponseMessage> Restart(string typed) => RequestSteps.PostControlAsync(owner, page, ControlWords.Restart, ("confirm", typed));

            switch (outcome)
            {
                case Sent:
                    return new(owner, await RequestSteps.PostControlAsync(owner, page, ControlWords.ClearCaches));

                case Forbidden:
                    var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.181");
                    return new(admin, await RequestSteps.PostControlAsync(admin, ControlWords.Restart, (await RequestSteps.ControlsAsync(admin)).Token, RequestFields.NewKey(),
                        ("confirm", "restart")));

                case MadeUp:
                    return new(owner, await RequestSteps.PostControlAsync(owner, ControlWords.ClearCaches, page.Token, "not-a-form-key"));

                case nameof(SendOutcome.SentBefore):
                    await RequestSteps.PostControlAsync(owner, page, ControlWords.SetStatus, ("text", "Back soon"));
                    var first = Assert.Single(await db.SentAsync()).id;
                    return new(owner, await RequestSteps.PostControlAsync(owner, page, ControlWords.SetStatus, ("text", "Back later")), HttpStatusCode.Conflict,
                        RequestWords.SentBefore(first));

                case nameof(SendRefusal.KeyTaken):
                    var taken = RequestFields.NewKey();
                    await RequestSteps.AddAsync(db, factory, RequestKinds.CachesClear, now, key: taken);
                    return new(owner, await RequestSteps.PostControlAsync(owner, ControlWords.RunNotifications, page.Token, taken), HttpStatusCode.Conflict, RequestWords.KeyTaken);

                case nameof(SendRefusal.Off):
                    return new(owner, await RequestSteps.PostControlAsync(owner, ControlWords.ClearCaches, page.Token, RequestFields.NewKey()), HttpStatusCode.Conflict,
                        RequestWords.Off);

                case nameof(SendRefusal.NotFresh):
                    factory.Clock.Advance(TimeSpan.FromMinutes(16));
                    return new(owner, await Restart("restart"), HttpStatusCode.Forbidden, "This needs a sign-in from the last 15 minutes");

                case nameof(SendRefusal.NotConfirmed):
                    return new(owner, await Restart("restar"), HttpStatusCode.BadRequest, RequestWords.NotConfirmed("restart"));

                case nameof(SendRefusal.Rule):
                    return new(owner, await RequestSteps.PostControlAsync(owner, page, ControlWords.SetStatus, ("text", "<b>x.com/minerbot</b>")), HttpStatusCode.BadRequest,
                        "The bot would refuse this, so nothing was sent: the status text has a link.");

                case nameof(SendRefusal.PersonLimit):
                    await RequestSteps.AddRefusedAsync(db, RequestKinds.CachesClear, TestUsers.Owner, Enumerable.Range(1, 20).Select(minute => now.AddMinutes(-minute)));
                    return new(owner, await RequestSteps.PostControlAsync(owner, page, ControlWords.ClearCaches), HttpStatusCode.TooManyRequests,
                        "You've asked for this 20 times in the last hour");

                case nameof(SendRefusal.BotLimit):
                    await RequestSteps.TakenAsync(db, factory, RequestKinds.BotRestart, now.AddMinutes(-2).AddSeconds(-1));
                    return new(owner, await Restart("restart"), HttpStatusCode.Conflict, "The bot takes this at most once every 10 minutes");

                case nameof(SendRefusal.Waiting):
                    await RequestSteps.AddAsync(db, factory, RequestKinds.BotRestart, now.AddSeconds(-30));
                    return new(owner, await Restart("restart"), HttpStatusCode.Conflict, "A request like this is already waiting for the bot");

                case nameof(SendOutcome.Unsure):
                    return new(owner, await RequestSteps.PostControlAsync(owner, page, ControlWords.ClearCaches), HttpStatusCode.ServiceUnavailable, RequestWords.Unsure,
                        page.Keys[ControlWords.ClearCaches]);
            }

            throw new ArgumentException($"The controls page has no press for {outcome}", nameof(outcome));
        }

        static string ServerPage(ulong id) => "/admin/servers/" + id.ToString(CultureInfo.InvariantCulture);

        // A server's page's forms, the Owner's, on StatusSteps' server, whose armor posts go to general and its maps to news,
        // each of which can take a test message, and its lobbies to a channel hidden from MinerBot, which can't
        static async Task<Pressed> PressServerAsync(MariaDbFixture db, DashboardFactory factory, string outcome)
        {
            await StatusSteps.ServersAsync(db);
            await db.Guilds().SaveGuild(new Guild { guild_id = RequestSteps.ServerId, armor_channel_id = 3001, map_channel_id = 3002, lobby_channel_id = 3003, announcements_channel_id = 0 });
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.184");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            Task<HttpResponseMessage> Press(string handler, string? key = null, string? channel = null, ulong? server = null)
            {
                var page = ServerPage(server ?? RequestSteps.ServerId);
                return PostFormAsync(owner, page, page + "?handler=" + handler, key, channel == null ? [] : [("channel", channel)]);
            }

            switch (outcome)
            {
                case Sent:
                    return new(owner, await Press(ServerActions.Refresh));

                case MadeUp:
                    return new(owner, await Press(ServerActions.Refresh, "not-a-form-key"));

                case nameof(SendOutcome.SentBefore):
                    var used = RequestFields.NewKey();
                    await Press(ServerActions.TestMessage, used, "3001");
                    var first = Assert.Single(await db.SentAsync()).id;
                    return new(owner, await Press(ServerActions.TestMessage, used, "3002"), HttpStatusCode.Conflict, RequestWords.SentBefore(first));

                case nameof(SendRefusal.Off):
                    return new(owner, await Press(ServerActions.Refresh), HttpStatusCode.Conflict, RequestWords.Off);

                case nameof(SendRefusal.PersonLimit):
                    await RequestSteps.AddRefusedAsync(db, RequestKinds.GuildRefresh, TestUsers.Owner, Enumerable.Range(1, 20).Select(minute => now.AddMinutes(-minute)));
                    return new(owner, await Press(ServerActions.Refresh), HttpStatusCode.TooManyRequests, "You've asked for this 20 times in the last hour");

                case nameof(SendRefusal.Waiting):
                    var waiting = await RequestSteps.AddAsync(db, factory, RequestKinds.GuildRefresh, now.AddSeconds(-10));
                    return new(owner, await Press(ServerActions.Refresh), HttpStatusCode.Conflict, $"A request like this is already waiting for the bot: request {waiting}");

                case nameof(SendOutcome.Unsure):
                    var unsure = RequestFields.NewKey();
                    return new(owner, await Press(ServerActions.Refresh, unsure), HttpStatusCode.ServiceUnavailable, RequestWords.Unsure, unsure);

                case NotChosenNow:
                    return new(owner, await Press(ServerActions.TestMessage, channel: "3004"), HttpStatusCode.Conflict, ServerActions.NotChosenNow);

                case ChannelNotNow:
                    return new(owner, await Press(ServerActions.TestMessage, channel: "3003"), HttpStatusCode.Conflict,
                        "That channel can't take a test message now: MinerBot is missing View Channel, Send Messages and Embed Links there. Nothing was sent.");

                case NotIn:
                    return new(owner, await Press(ServerActions.Refresh, server: StatusSteps.Id(StatusSteps.SettingsOnlyId)), HttpStatusCode.Conflict,
                        ServerActions.NotOffered(ServerActionState.NotIn));
            }

            throw new ArgumentException($"A server's page has no press for {outcome}", nameof(outcome));
        }

        // The leave page's form, the Owner's, for StatusSteps' server, whose name is Test Server
        static async Task<Pressed> PressLeaveAsync(MariaDbFixture db, DashboardFactory factory, string outcome)
        {
            await StatusSteps.ServersAsync(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.185");
            var leave = ServerPage(RequestSteps.ServerId) + "/leave";

            Task<HttpResponseMessage> Press(string typed, string? key = null, string? at = null) => PostFormAsync(owner, at ?? leave, at ?? leave, key, ("confirm", typed));

            switch (outcome)
            {
                case Sent:
                    return new(owner, await Press("Test Server"));

                // Refused by the Owner policy before any handler
                case Forbidden:
                    var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.186");
                    var token = SignInSteps.FormToken(await admin.Client.GetStringAsync(ServerPage(RequestSteps.ServerId), Token));
                    return new(admin, await SignInSteps.PostAsync(admin, leave, token, origin: DashboardFactory.PublicOrigin,
                        fields: [new("formkey", RequestFields.NewKey()), new("confirm", "Test Server")]));

                case MadeUp:
                    return new(owner, await Press("Test Server", "not-a-form-key"));

                case nameof(SendRefusal.NotConfirmed):
                    return new(owner, await Press("Another Server"), HttpStatusCode.BadRequest, RequestWords.NotConfirmedServer);

                case nameof(SendRefusal.NotFresh):
                    factory.Clock.Advance(TimeSpan.FromMinutes(16));
                    return new(owner, await Press("Test Server"), HttpStatusCode.Forbidden, "This needs a sign-in from the last 15 minutes");

                case nameof(SendOutcome.Unsure):
                    var unsure = RequestFields.NewKey();
                    return new(owner, await Press("Test Server", unsure), HttpStatusCode.ServiceUnavailable, RequestWords.Unsure, unsure);

                case NotIn:
                    return new(owner, await Press(StatusSteps.SettingsOnlyId, at: ServerPage(StatusSteps.Id(StatusSteps.SettingsOnlyId)) + "/leave"), HttpStatusCode.Conflict,
                        ServerActions.NothingToLeave + " Nothing was sent.");
            }

            throw new ArgumentException($"The leave page has no press for {outcome}", nameof(outcome));
        }

        // The review page's forms, an Admin's: Announce now for armor 101 once it's approved here, which the bot may then post,
        // and the actions on armor 102, waiting for a review
        static async Task<Pressed> PressReviewAsync(MariaDbFixture db, DashboardFactory factory, string outcome)
        {
            await db.AddAsync(ArmorSteps.Submission(101), ArmorSteps.Submission(102, ArmorStatus.Pending, "Test Armor 102"));
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.187");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            // Approved, and its page as it's drawn then, with Announce now's form
            async Task<string> ApprovedAsync()
            {
                Assert.Equal(HttpStatusCode.Redirect, (await ArmorSteps.PostAsync(admin, 101, "Approve", await ArmorSteps.OpenAsync(admin, 101))).StatusCode);
                return await admin.Client.GetStringAsync("/admin/armor/101", Token);
            }

            Task<HttpResponseMessage> Announce(long id, string? key = null) => PostFormAsync(admin, $"/admin/armor/{id}", $"/admin/armor/{id}?handler=Announce", key);

            Task<HttpResponseMessage> Approve(ReviewForm page, bool tickAll = true) => ArmorSteps.PostAsync(admin, 102, "Approve", page, tickAll);

            switch (outcome)
            {
                case Sent:
                    await ApprovedAsync();
                    return new(admin, await Announce(101));

                case MadeUp:
                    await ApprovedAsync();
                    return new(admin, await Announce(101, "not-a-form-key"));

                case nameof(SendRefusal.Waiting):
                    await ApprovedAsync();
                    var waiting = await RequestSteps.AddAsync(db, factory, RequestKinds.JobNotifications, now.AddSeconds(-10));
                    return new(admin, await Announce(101), HttpStatusCode.Conflict, $"A request like this is already waiting for the bot: request {waiting}");

                case nameof(SendOutcome.Unsure):
                    await ApprovedAsync();
                    var unsure = RequestFields.NewKey();
                    return new(admin, await Announce(101, unsure), HttpStatusCode.ServiceUnavailable, RequestWords.Unsure, unsure);

                case NotApprovedNow:
                    return new(admin, await Announce(102), HttpStatusCode.Conflict, ArmorWords.NotApprovedNow);

                // Drawn before the bot stopped, which the status row then shows
                case AnnounceNotNow:
                    var drawn = await ApprovedAsync();
                    var stopped = StatusSteps.Status(now);
                    stopped.stopped_at = now;
                    await db.Status().SaveStatus(stopped);
                    return new(admin, await Announce(101, KeyOf(drawn, "/admin/armor/101?handler=Announce")), HttpStatusCode.Conflict, ArmorWords.AnnounceNotNow);

                case Done:
                    return new(admin, await Approve(await ArmorSteps.OpenAsync(admin, 102)));

                case Changed:
                    var stale = await ArmorSteps.OpenAsync(admin, 102);
                    await db.RootExecuteAsync($"UPDATE {MariaDbFixture.ArmorTable} SET ArmorDescription = 'Changed after the page opened' WHERE ID = 102");
                    return new(admin, await Approve(stale), HttpStatusCode.Conflict, ArmorWords.Changed);

                // The same form sent again once it was done
                case Already:
                    var twice = await ArmorSteps.OpenAsync(admin, 102);
                    Assert.Equal(HttpStatusCode.Redirect, (await Approve(twice)).StatusCode);
                    return new(admin, await Approve(twice), HttpStatusCode.Conflict, ArmorWords.Already(ArmorStatus.Approved));

                // Another approved armor has its name, so the page offers no Approve: the status and version of its other forms
                case Blocked:
                    await db.AddAsync(ArmorSteps.Submission(103, ArmorStatus.Approved, "Test Armor 102"));
                    return new(admin, await Approve(await ArmorSteps.OpenAsync(admin, 102)), HttpStatusCode.Conflict, ArmorWords.Blocked);

                case Unticked:
                    await db.RootExecuteAsync($"UPDATE {MariaDbFixture.ArmorTable} SET ArmorDescription = 'Visit https://example.com' WHERE ID = 102");
                    return new(admin, await Approve(await ArmorSteps.OpenAsync(admin, 102), tickAll: false), HttpStatusCode.Conflict, ArmorWords.Unticked);

                case ActionUnsure:
                    return new(admin, await Approve(await ArmorSteps.OpenAsync(admin, 102)), HttpStatusCode.ServiceUnavailable, ArmorWords.Unsure);

                // Drawn before the bot posted it, which its record then shows
                case Posted:
                    var before = await ApprovedAsync();
                    var queued = await PostSteps.QueueAsync(db, PostSource.Armor, 101, now, RequestSteps.ServerId);
                    await PostSteps.SentAsync(db, queued, 3001, now, 5000000000000000101);
                    return new(admin, await ArmorSteps.PostAsync(admin, 101, "BackToPending", ArmorSteps.Read(before)), HttpStatusCode.Conflict, ArmorWords.WasPosted);
            }

            throw new ArgumentException($"The review page has no press for {outcome}", nameof(outcome));
        }

        // The switches' form on a server's page, the Owner's, on StatusSteps' server, whose armor, maps and lobbies each have a
        // channel set: armor posts turned off, from the version the page drew
        static async Task<Pressed> PressSwitchesAsync(MariaDbFixture db, DashboardFactory factory, string outcome)
        {
            await StatusSteps.ServersAsync(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.189");
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var page = ServerPage(RequestSteps.ServerId);

            async Task<HttpResponseMessage> Press(TestBrowser browser, string? version = null, bool armor = false, ulong? server = null)
            {
                var html = await browser.Client.GetStringAsync(page, Token);
                var drawn = version ?? Regex.Match(html, "name=\"version\" value=\"([0-9]+)\"").Groups[1].Value;
                List<KeyValuePair<string, string>> fields = [new("version", drawn), new("maps", "on"), new("lobbies", "on")];

                if (armor)
                    fields.Add(new("armor", "on"));

                return await SignInSteps.PostAsync(browser, ServerPage(server ?? RequestSteps.ServerId) + "?handler=Switches", SignInSteps.FormToken(html),
                    origin: DashboardFactory.PublicOrigin, fields: fields);
            }

            switch (outcome)
            {
                case Done:
                    return new(owner, await Press(owner));

                // Refused by the page's gate before the handler
                case Forbidden:
                    var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.190");
                    return new(admin, await Press(admin, "0"));

                case MadeUp:
                    return new(owner, await Press(owner, "zero"));

                case NotIn:
                    return new(owner, await Press(owner, "0", server: StatusSteps.Id(StatusSteps.SettingsOnlyId)), HttpStatusCode.Conflict, SwitchWords.NotInNow);

                case Unchanged:
                    return new(owner, await Press(owner, armor: true), HttpStatusCode.OK, SwitchWords.Unchanged);

                // What the form asks for was saved after the page was drawn from no switches: an earlier send of it, say
                case SwitchesAlready:
                    await PostSteps.SwitchesAsync(db, RequestSteps.ServerId, armor: false, maps: true, lobbies: true, now);
                    return new(owner, await Press(owner, "0"), HttpStatusCode.OK, SwitchWords.Already);

                case Stale:
                    await PostSteps.SwitchesAsync(db, RequestSteps.ServerId, armor: true, maps: false, lobbies: true, now);
                    return new(owner, await Press(owner, "0"), HttpStatusCode.Conflict, SwitchWords.Stale);

                case Limit:
                    for (var minute = 1; minute <= SwitchLimits.PerHour; minute++)
                    {
                        await db.Audit().AddAudit(new AuditEntry
                        {
                            at = now.AddMinutes(-minute),
                            actor_id = TestUsers.Owner.Id,
                            actor_name = "Test Owner",
                            actor_role = AuditRole.Owner,
                            action = AuditWords.SwitchesChange,
                            outcome = AuditOutcome.Ok,
                            guild_id = RequestSteps.ServerId
                        });
                    }

                    return new(owner, await Press(owner), HttpStatusCode.TooManyRequests, "You've changed switches 30 times in the last hour, so nothing was saved.");

                case SwitchesUnsure:
                    return new(owner, await Press(owner), HttpStatusCode.ServiceUnavailable, SwitchWords.Unsure);
            }

            throw new ArgumentException($"The switches' form has no press for {outcome}", nameof(outcome));
        }

        // The blacklist's add form, an Admin's, with the boxes a person typed: an account number, or a custom profile link,
        // which the bot looks up
        static async Task<Pressed> PressAddAsync(MariaDbFixture db, DashboardFactory factory, string outcome)
        {
            const string Link = "https://steamcommunity.com/id/gabelogannewell";
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.188");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            Task<HttpResponseMessage> Press(string steamId, ulong? lookup = null, string? key = null) =>
                PostFormAsync(admin, "/admin/blacklist/add", "/admin/blacklist/add", key, ("steamid", steamId), ("name", "Kept Name"), ("reason", "Kept Reason"),
                    ("expires", "2027-01-02"), ("notes", "Kept Notes"), ("lookup", lookup?.ToString(CultureInfo.InvariantCulture) ?? ""));

            // The link looked up, and the lookup's number
            async Task<ulong> LookedUpAsync()
            {
                await RequestSteps.AnsweredAsync(admin, await Press(Link));
                return Assert.Single(await db.SentAsync(), row => row.kind == RequestKinds.SteamLookup).id;
            }

            switch (outcome)
            {
                case Sent:
                    return new(admin, await Press("12345"));

                case MadeUp:
                    return new(admin, await PostFormAsync(admin, "/admin/blacklist/add", "/admin/blacklist/add", null, ("steamid", Link), ("lookup", "abc")));

                case Fields:
                    return new(admin, await Press("not an id"), HttpStatusCode.BadRequest, "That isn't a SteamID, an account number or a Steam profile link");

                case LookupNone:
                    var none = await LookedUpAsync();
                    await RequestSteps.ClaimAsync(db, none, now.AddSeconds(1));
                    await RequestSteps.FinishAsync(db, none, RequestStates.Done, "Steam has no profile with that custom link.", now.AddSeconds(2));
                    return new(admin, await Press(Link, none), HttpStatusCode.BadRequest, BlacklistWords.LookupNoProfile);

                case LookupWaiting:
                    return new(admin, await Press(Link, await LookedUpAsync()), HttpStatusCode.OK, BlacklistWords.LookupWaiting);

                case LookupSent:
                    return new(admin, await Press(Link), HttpStatusCode.OK, BlacklistWords.LookupSent);

                case LookupUnsure:
                    var unsure = RequestFields.NewKey();
                    return new(admin, await Press(Link, key: unsure), HttpStatusCode.ServiceUnavailable, RequestWords.Unsure, unsure);

                case Exists:
                    await db.AddEntriesAsync(BlacklistSteps.Entry(12345));
                    return new(admin, await Press("12345"), HttpStatusCode.Conflict, BlacklistWords.Exists);

                case AlreadyAdded:
                    await db.AddEntriesAsync(BlacklistSteps.Entry(12345, "Kept Name", "Kept Reason", new DateTime(2027, 1, 2), "Kept Notes"));
                    return new(admin, await Press("12345"), HttpStatusCode.Conflict, BlacklistWords.AlreadyAdded);

                case AddUnsure:
                    return new(admin, await Press("12345"), HttpStatusCode.ServiceUnavailable, BlacklistWords.AddUnsure);
            }

            throw new ArgumentException($"The add page has no press for {outcome}", nameof(outcome));
        }

        // A form whose POST fails is sent to the error page by GET, with its request's ID, never answered with the page, which
        // a reload would send again: one that met the database down sees the usual page while it stays down, since the
        // GET can't read its session either, and once it answers, the words for the form with that ID, which the error
        // log's line has; an unexpected failure, the same at 500. A body that couldn't be read keeps its page, since sending
        // it again gets the same answer. The error page shows a request ID it's given only when it looks like one.
        [Fact]
        public async Task AFormThatFailsIsARedirect()
        {
            var db = await fixture.ReadyAsync();
            SwitchedDatabase? database = null;
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services =>
                {
                    services.RemoveAll<ISQLConnectionFactory>();
                    services.AddSingleton<ISQLConnectionFactory>(provider =>
                        database = new SwitchedDatabase(new SQLConnectionFactory(provider.GetRequiredService<IDictionary<DBConnectionName, string>>())));

                    // The write's own transaction can't reach the database, so nothing was written
                    services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider => new UnavailableQueue(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider))));
                }
            });
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.182");

            var failed = await RequestSteps.PostControlAsync(owner, await RequestSteps.ControlsAsync(owner), ControlWords.ClearCaches);
            var address = SignInSteps.PathOf(failed.Headers.Location)!;
            database!.Down = true;
            var (down, id) = await RequestSteps.FailedAsync(owner, failed, ErrorModel.Database);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
            Assert.Contains(ErrorResponses.Unavailable, WebUtility.HtmlDecode(await down.Content.ReadAsStringAsync(Token)));

            database.Down = false;
            var back = await owner.Client.GetAsync(address, Token);
            var html = await back.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, back.StatusCode);
            Assert.Contains("<h1>Temporarily unavailable</h1>", html);
            Assert.Contains(ErrorResponses.UnavailableAfterForm, WebUtility.HtmlDecode(html));
            Assert.Equal(id, RequestId(html));
            Assert.Contains(factory.Logs.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("Request " + id + " failed", StringComparison.Ordinal));
            Assert.Empty(await db.SentAsync());
            PageTests.AssertAccessible("the error page for a form that met the database down", html);

            // A request ID that isn't in the shape of one isn't shown, and the page gives its own: markup, words made of the
            // characters an ID has, such as a host name in a link someone crafted, and an ID with a line break after it.
            // Fails if the page shows any of them.
            foreach (var (madeUp, word) in new[]
            {
                ("<b>made-up</b>", "made-up"),
                ("Sign-in-again-at.made-up.example", "made-up"),
                ("0HNA1B2C3D4E5:00000001\n", "0HNA1B2C3D4E5")
            })
            {
                var other = await owner.Client.GetAsync("/error?failed=database&request=" + Uri.EscapeDataString(madeUp), Token);
                var otherHtml = await other.Content.ReadAsStringAsync(Token);

                Assert.Equal(HttpStatusCode.ServiceUnavailable, other.StatusCode);
                Assert.DoesNotContain(word, otherHtml);
                Assert.Matches("^[0-9A-V]{13}(:[0-9A-F]{8})?\\z", RequestId(otherHtml));
            }

            // An unexpected failure
            var marker = "marker-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            await using var broken = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<RequestSender>(_ => throw new InvalidOperationException(marker)))
            });
            var admin = await SignedInAsync(broken, TestUsers.Admin, "198.51.100.183");
            var (page, failure) = await RequestSteps.FailedAsync(admin,
                await RequestSteps.PostControlAsync(admin, await RequestSteps.ControlsAsync(admin), ControlWords.ClearCaches), ErrorModel.Failure);
            var pageHtml = await page.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.InternalServerError, page.StatusCode);
            Assert.Contains("<h1>Something went wrong</h1>", pageHtml);
            Assert.Contains(ErrorResponses.FailedAfterForm, WebUtility.HtmlDecode(pageHtml));
            Assert.Equal(failure, RequestId(pageHtml));
            Assert.DoesNotContain(marker, pageHtml);
            Assert.Contains(broken.Logs.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("Request " + failure + " failed", StringComparison.Ordinal));

            // A body too big keeps its own page, and a form that met the database is sent on, as the handler answers each
            foreach (var (exception, status) in new (Exception, int)[]
            {
                (new BadHttpRequestException(marker, StatusCodes.Status413PayloadTooLarge), StatusCodes.Status413PayloadTooLarge),
                (new DashboardUnavailableException(), StatusCodes.Status303SeeOther)
            })
            {
                var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(), TraceIdentifier = "0HNTEST:00000001" };
                context.Request.Method = HttpMethods.Post;
                context.Response.Body = new MemoryStream();
                context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature { Error = exception, Path = "/admin/bot/controls" });

                await ErrorResponses.WriteAsync(context);

                var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
                Assert.Equal(status, context.Response.StatusCode);

                if (status == StatusCodes.Status303SeeOther)
                {
                    Assert.Equal("/error?failed=database&request=0HNTEST%3A00000001", context.Response.Headers.Location.ToString());
                    Assert.Empty(body);
                }
                else
                {
                    Assert.Contains(ErrorResponses.Unreadable, WebUtility.HtmlDecode(body));
                }
            }
        }

        // The error page sends a form that failed back to the page it came from, never to the page opened anew. After a save
        // the database didn't confirm, the notice draws the form with the key it was sent with, and pressed while the
        // database is down again, that form ends on the error page. Back, the notice's address draws the kept key, and
        // pressing it finds the first request, which landed and which the bot took meanwhile; the page opened anew draws a
        // new key, which would send a second clear. Each of the error page's answers to a form says so. Fails if one says to
        // open the page again.
        [Fact]
        public async Task AFormThatFailsIsSentBackToThePageItCameFrom()
        {
            var db = await fixture.ReadyAsync();
            SwitchedDatabase? database = null;
            var unconfirmed = true;
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services =>
                {
                    services.RemoveAll<ISQLConnectionFactory>();
                    services.AddSingleton<ISQLConnectionFactory>(provider =>
                        database = new SwitchedDatabase(new SQLConnectionFactory(provider.GetRequiredService<IDictionary<DBConnectionName, string>>())));

                    // The first press's commit lands without its answer, and every later one is confirmed
                    services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider =>
                    {
                        var real = ActivatorUtilities.CreateInstance<BotRequestQueue>(provider);
                        return unconfirmed ? new UnconfirmedQueue(real) : real;
                    }));
                }
            });
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.189");
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var page = await RequestSteps.ControlsAsync(owner);
            var key = page.Keys[ControlWords.ClearCaches];

            var notice = await RequestSteps.NoticeOfAsync(await RequestSteps.PostControlAsync(owner, page, ControlWords.ClearCaches));
            unconfirmed = false;
            var first = Assert.Single(await db.SentAsync());
            Assert.Equal(key, first.request_key);

            var unsure = await owner.Client.GetAsync(notice, Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unsure.StatusCode);
            var drawn = RequestSteps.ReadControls(await unsure.Content.ReadAsStringAsync(Token));
            Assert.Equal(key, drawn.Keys[ControlWords.ClearCaches]);

            await RequestSteps.ClaimAsync(db, first.id, now.AddSeconds(2));
            await RequestSteps.FinishAsync(db, first.id, RequestStates.Done, "Cleared the caches for a test.", now.AddSeconds(3));

            database!.Down = true;
            var failed = await RequestSteps.PostControlAsync(owner, drawn, ControlWords.ClearCaches);
            database.Down = false;
            var (error, _) = await RequestSteps.FailedAsync(owner, failed, ErrorModel.Database);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
            Assert.Contains(ErrorResponses.UnavailableAfterForm, WebUtility.HtmlDecode(await error.Content.ReadAsStringAsync(Token)));

            foreach (var words in new[] { ErrorResponses.UnavailableAfterForm, ErrorResponses.FailedAfterForm, RateLimits.RejectedForm })
            {
                Assert.Contains("go back to the page you sent it from", words, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("open the page", words, StringComparison.OrdinalIgnoreCase);
            }

            var back = RequestSteps.ReadControls(await (await owner.Client.GetAsync(notice, Token)).Content.ReadAsStringAsync(Token));
            Assert.Equal(key, back.Keys[ControlWords.ClearCaches]);
            Assert.NotEqual(key, (await RequestSteps.ControlsAsync(owner)).Keys[ControlWords.ClearCaches]);

            var found = await RequestSteps.PostControlAsync(owner, back, ControlWords.ClearCaches);
            Assert.Equal(HttpStatusCode.Redirect, found.StatusCode);
            Assert.Equal($"/admin/bot/controls?request={first.id}#result", SignInSteps.PathOf(found.Headers.Location));
            Assert.Single(await db.SentAsync());
        }

        // A form past the rate limit is sent to the error page by GET, with no body, never answered with the limiter's text,
        // which a reload would send again. That GET, in the same minute, is itself over the limit and gets the plain 429;
        // from an address under its limit, the page says what happened to the form, with Retry-After. Fails if the limiter
        // answers a form with its text.
        [Fact]
        public async Task ARateLimitedFormIsARedirect()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var visitor = factory.Visitor("203.0.113.26");

            for (var i = 0; i < RateLimits.VisitorPerMinute; i++)
                Assert.Equal(HttpStatusCode.OK, (await visitor.Client.GetAsync("/", Token)).StatusCode);

            var limited = await SignInSteps.PostAsync(visitor, "/admin/bot/controls?handler=ClearCaches", "made-up", origin: DashboardFactory.PublicOrigin,
                fields: [new("formkey", RequestFields.NewKey())]);

            Assert.Equal(HttpStatusCode.SeeOther, limited.StatusCode);
            Assert.Equal("/error?failed=limit", SignInSteps.PathOf(limited.Headers.Location));
            Assert.Empty(await limited.Content.ReadAsByteArrayAsync(Token));

            var again = await visitor.Client.GetAsync("/error?failed=limit", Token);
            Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);
            Assert.Equal(RateLimits.Rejection, await again.Content.ReadAsStringAsync(Token));
            Assert.NotEmpty(again.Headers.GetValues("Retry-After"));

            var page = await factory.Visitor("203.0.113.27").Client.GetAsync("/error?failed=limit", Token);
            var html = await page.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.TooManyRequests, page.StatusCode);
            Assert.Equal(["60"], page.Headers.GetValues("Retry-After"));
            Assert.Contains("<h1>Too many requests</h1>", html);
            Assert.Contains(RateLimits.RejectedForm, WebUtility.HtmlDecode(html));
            PageTests.AssertAccessible("the error page for a form past the rate limit", html);
        }

        static TimeoutException Late() => new("The database didn't answer within 15 seconds");

        // The real repositories, whose transactions commit and then time out, as a commit whose answer never came
        sealed class LateArmor(ArmorSubmissionData real) : IArmorSubmissionData
        {
            public async Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work)
            {
                await real.RunInTransaction(work);
                throw Late();
            }

            public Task<bool> ChangeStatus(long id, string? expectedStatus, string newStatus, DataTransaction? transaction = null) => real.ChangeStatus(id, expectedStatus, newStatus, transaction);
            public Task<ArmorSubmission?> GetSubmission(long id, DataTransaction? transaction = null) => real.GetSubmission(id, transaction);
            public Task<bool> IsApprovedNameTaken(string name, long exceptId, DataTransaction? transaction = null) => real.IsApprovedNameTaken(name, exceptId, transaction);
            public Task<Page<ArmorSubmission>> ListByStatus(string? status, int page, int pageSize) => real.ListByStatus(status, page, pageSize);
            public Task<Page<ArmorSubmission>> Search(string text, string? status, int page, int pageSize) => real.Search(text, status, page, pageSize);
        }

        sealed class LateBlacklist(BlacklistData real) : IBlacklistData
        {
            public async Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work)
            {
                await real.RunInTransaction(work);
                throw Late();
            }

            public Task<BlacklistModel?> GetBannedPlayer(uint steamID, DataTransaction? transaction = null) => real.GetBannedPlayer(steamID, transaction);
            public Task<IEnumerable<BlacklistModel>> GetBlacklist() => real.GetBlacklist();
            public Task<OutOfRangeBan?> GetOutOfRangeBan(decimal steamId, DataTransaction? transaction = null) => real.GetOutOfRangeBan(steamId, transaction);
            public Task InsertPlayerBan(BlacklistModel player, DataTransaction? transaction = null) => real.InsertPlayerBan(player, transaction);
            public Task<IReadOnlyList<OutOfRangeBan>> ListOutOfRangeBans() => real.ListOutOfRangeBans();
            public Task<IReadOnlyList<decimal>> ListOutOfRangeSteamIds() => real.ListOutOfRangeSteamIds();
            public Task<bool> RemoveOutOfRangeSteamId(decimal steamId, DataTransaction? transaction = null) => real.RemoveOutOfRangeSteamId(steamId, transaction);
            public Task<bool> RemovePlayerBan(uint steamID, DataTransaction? transaction = null) => real.RemovePlayerBan(steamID, transaction);
            public Task<Page<BlacklistModel>> SearchBlacklist(uint? accountNumber, string? username, int page, int pageSize) => real.SearchBlacklist(accountNumber, username, page, pageSize);
            public Task<bool> UpdatePlayerBan(BlacklistModel player, DataTransaction? transaction = null) => real.UpdatePlayerBan(player, transaction);
        }

        [GeneratedRegex("Request ID: <code>([^<]+)</code>")]
        private static partial Regex RequestIdText();
    }
}
