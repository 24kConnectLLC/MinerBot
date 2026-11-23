using System.Data.Common;
using System.Globalization;
using System.Text;
using DataAccessLibrary;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Requests.Models;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Settings;
using MinerBot.Dashboard.Status;
using MinerBot.Shared.Requests;
using MinerBot.Shared.Steam;
using MySql.Data.MySqlClient;

namespace MinerBot.Dashboard.Requests
{
    // How long the request command waits for the bot, which the tests shorten
    internal sealed record RequestWaits
    {
        // How often it reads the request back
        public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(1);

        // How long past its expiry a request that's still queued is waited for, since the bot may have taken it just in time
        public TimeSpan QueuedGrace { get; init; } = TimeSpan.FromSeconds(20);

        // How long past its kind's action limit a request the bot took is waited for
        public TimeSpan RunningGrace { get; init; } = TimeSpan.FromSeconds(30);

        // After a restart: how often the bot's status is read, and for how long, until a new run of the bot is on Discord.
        // The bot writes done first, then waits for a notification run the schedule started meanwhile, which JobHandler cuts
        // off at 270 seconds, before it stops, so the wait covers that and the restart itself.
        public TimeSpan RestartPoll { get; init; } = TimeSpan.FromSeconds(2);
        public TimeSpan RestartLimit { get; init; } = TimeSpan.FromMinutes(6);
    }

    // The request command: one request to the bot from the server, for the live check and for reaching the bot when the
    // dashboard's pages can't, sent through BotRequests, the code the pages call, with the dashboard's own database login.
    // deploy/request.sh runs it in the dashboard's image. Its settings come from the environment only, never from its
    // command line, which the process list shows: ConnectionStrings__BotConnection, Dashboard__RequestSigningKey and
    // Dashboard__Owners, and the request's text in MINERBOT_REQUEST_TEXT, so a text that starts with -- is never an option.
    // It asks as the first of the Owners, named as request.sh on the server, then waits for the bot's answer and prints it.
    // It never prints a key, a signature or a connection string.
    // Exit code: 0 done, and back on Discord after a restart; 1 refused, failed, expired, unknown, or no answer; 2 a bad
    // argument or setting, with nothing sent.
    internal static class RequestCommand
    {
        public const string Name = "request";
        public const string TextVariable = "MINERBOT_REQUEST_TEXT";

        // Who the request says asked, as the owner's direct message and the audit row show it
        public const string ActorName = "request.sh on the server";

        public const string Usage = "Usage: request <kind> [--guild <server ID>] [--channel <channel ID>], with the text in " + TextVariable + ", or request --list";

        // Its own pool of at most two connections, so beside the running dashboard's two pools of 6 it stays within the
        // login's 15
        const int PoolSize = 2;

        // A Discord ID, as the settings' lists take one
        const int MinIdDigits = 17;
        const int MaxIdDigits = 20;

        sealed record Asked(RequestKind Kind, ulong? GuildId, ulong? ChannelId);

        // As the image runs it: the environment, standard output, and the log for the database's own failures, as migrate's
        public static async Task<int> RunAsync(string[] args)
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

            // Disposed at the end, so its lines are written out before the process exits
            using var loggers = LoggerFactory.Create(logging => LogLevels(logging.AddSimpleConsole(console => console.SingleLine = true)));
            return await RunAsync(configuration, args[1..], Console.Out, loggers, TimeProvider.System, new RequestWaits(), CancellationToken.None);
        }

        // The log is for the database's warnings and failures, which the command's own lines point to with "(above)". Its own
        // lines already say what it does, so an information line, such as BotRequests' "is queued for the bot", which reads
        // "(request (null))" here, with no web request behind it, would only say it again.
        internal static ILoggingBuilder LogLevels(ILoggingBuilder logging) => logging.SetMinimumLevel(LogLevel.Warning);

        internal static async Task<int> RunAsync(IConfiguration configuration, string[] args, TextWriter output, ILoggerFactory loggers, TimeProvider time,
            RequestWaits waits, CancellationToken stop)
        {
            if (args is ["--list"])
            {
                await output.WriteAsync(List());
                return 0;
            }

            if (!TryParse(args, out var asked, out var argumentProblem))
            {
                await output.WriteLineAsync(argumentProblem);
                await output.WriteLineAsync(Usage);
                return 2;
            }

            if (SettingsProblem(configuration, out var connection, out var owner) is { } settingsProblem)
            {
                await output.WriteLineAsync(settingsProblem);
                return 2;
            }

            var text = configuration[TextVariable];

            if (asked.Kind.Argument == RequestArgument.CustomLinkName && text != null)
            {
                text = LookupName(text, out var linkProblem);

                if (text == null)
                {
                    await output.WriteLineAsync(linkProblem);
                    return 2;
                }
            }

            var services = new ServiceCollection();
            services.AddSingleton(loggers);
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            services.AddSingleton(time);
            services.AddSingleton(Options.Create(new DashboardOptions { RequestSigningKey = configuration["Dashboard:RequestSigningKey"] ?? "" }));
            services.AddDataAccessServices(Pooled(connection))
                    .AddBotRequestQueue()
                    .AddAuditLogAccess()
                    .AddBotStatusReads();
            services.AddBotRequests();

            await using var provider = services.BuildServiceProvider();
            var queue = provider.GetRequiredService<IBotRequestQueue>();
            var kind = asked.Kind;
            QueuedRequest queued;

            try
            {
                queued = await provider.GetRequiredService<BotRequests>().SendAsync(new RequestOrder(kind.Word, asked.GuildId, asked.ChannelId, text),
                    new RequestActor(owner, ActorName, RequestRoles.Owner), ip: null, traceId: null);
            }
            catch (RequestRefusedException e)
            {
                await output.WriteLineAsync($"The request wasn't sent, because the bot would refuse it: {e.Problem}.");
                return 2;
            }
            catch (DashboardUnavailableException)
            {
                await output.WriteLineAsync("The request wasn't sent: MinerBot's database didn't answer (above).");
                return 1;
            }

            if (queued.Confirmed)
                await output.WriteLineAsync(Queued(queued.Id, kind, owner, queued.ExpiresAt));
            else
                await output.WriteLineAsync($"The request ({kind.Label}) may or may not have been saved: MinerBot's database didn't confirm it. Looking for it.");

            var row = await WaitForTheBotAsync(queue, queued, kind, owner, output, time, waits, stop);

            if (row == null)
                return 1;

            await output.WriteLineAsync($"{StateWord(row.state)} at {Clock(row.ended_at ?? time.GetUtcNow().UtcDateTime)} UTC: {Printable(row.result ?? "")}");

            if (row.state == RequestStates.Done && row.result_number is { } number && kind.Word == RequestKinds.SteamLookup)
                await output.WriteLineAsync("Account number: " + number.ToString(CultureInfo.InvariantCulture));

            if (row.state == RequestStates.Done && row.result_number is { } message && kind.Word == RequestKinds.GuildTestMessage)
                await output.WriteLineAsync("Message ID: " + message.ToString(CultureInfo.InvariantCulture));

            var exit = row.state == RequestStates.Done ? 0 : 1;

            if (kind.Word == RequestKinds.BotRestart && row.state == RequestStates.Done
                && !await BackOnDiscordAsync(provider.GetRequiredService<IBotStatusReads>(), row, output, time, waits, stop))
                exit = 1;

            if (kind.Word is RequestKinds.PostsPause or RequestKinds.PostsResume or RequestKinds.StatusSet or RequestKinds.BotRestart)
                await output.WriteLineAsync(await SettingsAsync(queue));

            return exit;
        }

        // The kind first, then --guild and --channel, each at most once. Nothing else, and never a setting.
        static bool TryParse(string[] args, out Asked asked, out string problem)
        {
            asked = null!;
            problem = "";

            if (args.Length == 0 || args[0].StartsWith('-'))
            {
                problem = "Name the kind of request first: request --list shows them.";
                return false;
            }

            if (RequestKinds.Find(args[0]) is not { } kind)
            {
                problem = "That isn't a kind of request the bot takes: request --list shows them.";
                return false;
            }

            ulong? guild = null;
            ulong? channel = null;

            for (var i = 1; i < args.Length; i += 2)
            {
                var option = args[i];

                if (option is not ("--guild" or "--channel") || (option == "--guild" ? guild : channel) != null)
                {
                    problem = "Only --guild <server ID> and --channel <channel ID> go after the kind, each once.";
                    return false;
                }

                if (i + 1 >= args.Length || !TryDiscordId(args[i + 1], out var id))
                {
                    problem = $"{option} takes a numeric Discord ID, {MinIdDigits} to {MaxIdDigits} digits.";
                    return false;
                }

                if (option == "--guild")
                    guild = id;
                else
                    channel = id;
            }

            asked = new Asked(kind, guild, channel);
            return true;
        }

        static bool TryDiscordId(string text, out ulong id)
        {
            id = 0;
            return text.Length is >= MinIdDigits and <= MaxIdDigits && text.All(char.IsAsciiDigit)
                && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0;
        }

        // What stops the request being sent, in the settings' own names, never their values: MinerBot's database, the key,
        // and an owner to ask as
        static string? SettingsProblem(IConfiguration configuration, out string connection, out ulong owner)
        {
            connection = configuration.GetConnectionString("BotConnection") ?? "";
            owner = 0;

            if (string.IsNullOrWhiteSpace(connection))
                return "ConnectionStrings:BotConnection isn't set, so there's no database to leave the request in.";

            if (DatabaseNames.Problem(connection) is { } problem)
                return $"ConnectionStrings:BotConnection {problem}, so there's no database to leave the request in.";

            var key = configuration["Dashboard:RequestSigningKey"];

            if (string.IsNullOrEmpty(key))
                return "Requests to the bot are off: the dashboard has no request-signing key. Run ./setup-dashboard.sh --no-questions.";

            if (DashboardOptionsValidator.RequestSigningKeyProblem(key) is { } keyProblem)
                return keyProblem;

            var owners = configuration["Dashboard:Owners"];

            if (!DiscordIds.TryParse(owners, out var ids, out _) || ids.Count == 0)
                return "Dashboard:Owners has no owner's Discord user ID, so the request has no one to say it's from.";

            // The first as the setting lists them, which a set doesn't keep
            owner = ulong.Parse(owners!.Replace(',', ' ').Replace(';', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0],
                NumberStyles.None, CultureInfo.InvariantCulture);
            return null;
        }

        // A lookup takes a custom profile link's name, or the whole link, which goes through SteamIds as the blacklist's
        // page reads one. Null, with why, for a link that isn't a custom one.
        static string? LookupName(string text, out string problem)
        {
            problem = "";

            if (!text.Contains('/'))
                return text;

            var result = SteamIds.Parse(text);

            switch (result.Kind)
            {
                case SteamIdKind.NeedsLookup:
                    return result.VanityName;
                case SteamIdKind.AccountNumber:
                    problem = $"That profile link names the account itself, [U:1:{result.AccountNumber.ToString(CultureInfo.InvariantCulture)}], so there's nothing to look up.";
                    return null;
                default:
                    problem = $"That profile link can't be looked up: {result.Error}.";
                    return null;
            }
        }

        // The connection string with a pool of its own size, whatever it had
        internal static IConfiguration Pooled(string connection)
        {
            var pooled = new MySqlConnectionStringBuilder(connection) { MaximumPoolSize = PoolSize }.ConnectionString;
            return new ConfigurationBuilder().AddInMemoryCollection([KeyValuePair.Create<string, string?>("ConnectionStrings:BotConnection", pooled)]).Build();
        }

        static string Queued(ulong id, RequestKind kind, ulong owner, DateTime expiresAt) =>
            string.Create(CultureInfo.InvariantCulture, $"Request {id} ({kind.Label}) is queued, asked by {ActorName} (Owner {owner}). It expires at {Clock(expiresAt)} UTC.");

        // Reads the request back until the bot has ended it, which works whether or not its commit was confirmed. Null when
        // no answer came in time: 20 seconds past its expiry while it's queued, or its kind's action limit and 30 seconds
        // past the bot taking it, each of which it says. A look MinerBot's database doesn't answer keeps what the last one
        // found, so a request seen running is still waited for by its action limit, and named by its number.
        internal static async Task<BotRequestRow?> WaitForTheBotAsync(IBotRequestQueue queue, QueuedRequest queued, RequestKind kind, ulong owner, TextWriter output,
            TimeProvider time, RequestWaits waits, CancellationToken stop)
        {
            var seen = queued.Confirmed;
            var running = false;
            var answered = false;
            var id = queued.Id;
            BotRequestRow? last = null;
            string? failure = null;

            while (true)
            {
                BotRequestRow? row;

                try
                {
                    row = await queue.GetRequestByKey(queued.RequestKey);
                    failure = null;
                    answered = true;
                }
                catch (Exception e) when (e is DbException or TimeoutException)
                {
                    failure = Failure(e);
                    row = last;
                }

                last = row;
                id = row?.id ?? id;

                var now = time.GetUtcNow().UtcDateTime;
                var unanswered = failure == null ? "" : $" MinerBot's database didn't answer the last look ({failure}).";

                if (row != null && !seen)
                {
                    await output.WriteLineAsync(Queued(row.id, kind, owner, row.expires_at));
                    seen = true;
                }

                if (row?.state is { } state && state != RequestStates.Running)
                    return row;

                if (row?.state == RequestStates.Running && row.claimed_at is { } claimed)
                {
                    if (!running)
                    {
                        await output.WriteLineAsync($"Running since {Clock(claimed)} UTC.");
                        running = true;
                    }

                    if (now > claimed + kind.ActionLimit + waits.RunningGrace)
                    {
                        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                            $"No answer from the bot by {Clock(now)} UTC: request {row.id} has run since {Clock(claimed)} UTC, past its {kind.ActionLimit.TotalSeconds:0}-second limit. The Bot page shows how it ends.{unanswered}"));
                        return null;
                    }
                }
                else if (now > (row?.expires_at ?? queued.ExpiresAt) + waits.QueuedGrace)
                {
                    if (row == null && !seen && !answered)
                        await output.WriteLineAsync($"Whether the request was saved isn't known: MinerBot's database answered none of the looks for it by {Clock(now)} UTC, past its expiry ({failure}).");
                    else if (row == null && !seen)
                        await output.WriteLineAsync($"The request isn't in MinerBot's database by {Clock(now)} UTC, past its expiry, so it wasn't saved and the bot never had it.{unanswered}");
                    else
                        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                            $"No answer from the bot by {Clock(now)} UTC: request {id} {(failure == null ? "is" : "was")} still queued, past its expiry at {Clock(row?.expires_at ?? queued.ExpiresAt)} UTC. ./status.sh says whether the bot runs.{unanswered}"));

                    return null;
                }

                await Task.Delay(waits.Poll, time, stop);
            }
        }

        // After a restart the bot took: true once a new run of the bot is up on Discord, which its status row says, false
        // when none is within the limit. Then the time it was back, as the box gives it (RequestViews.BackOnDiscord): at the
        // bot's record of the run's first connection, read once and never waited for, or by the row's sample while that
        // isn't written or can't be read. The exit code and request.sh's history line don't depend on which.
        internal static async Task<bool> BackOnDiscordAsync(IBotStatusReads status, BotRequestRow row, TextWriter output, TimeProvider time, RequestWaits waits,
            CancellationToken stop)
        {
            await output.WriteLineAsync($"Waiting for the bot to be back on Discord, for up to {Span(waits.RestartLimit)}.");
            var until = time.GetUtcNow() + waits.RestartLimit;

            while (true)
            {
                try
                {
                    if (await status.GetStatus() is { } current && current.run_id != row.run_id && current.discord_state == DiscordState.Connected
                        && current.stopped_at == null)
                    {
                        var (at, exact) = RequestViews.BackOnDiscord(current, await ConnectedAsync(status, current));
                        await output.WriteLineAsync(exact
                            ? string.Create(CultureInfo.InvariantCulture,
                                $"The bot was back on Discord at {Clock(at)} UTC, {RequestViews.SecondsAfter(row.created_at, at)} s after the request: its new run started at {Clock(current.started_at)} UTC.")
                            : $"The bot was back on Discord by {Clock(at)} UTC: its new run started at {Clock(current.started_at)} UTC.");
                        return true;
                    }
                }
                catch (Exception e) when (e is DbException or TimeoutException)
                {
                    // Looked at again at the next poll, until the limit
                }

                if (time.GetUtcNow() >= until)
                {
                    await output.WriteLineAsync($"The bot isn't back on Discord {Span(waits.RestartLimit)} after the restart. ./status.sh says whether it runs, and docker logs --tail 50 minerbot-bot what it last did.");
                    return false;
                }

                await Task.Delay(waits.RestartPoll, time, stop);
            }
        }

        // The new run's first connection to Discord, as the bot recorded it, from the run's start on: null while it isn't
        // written, and when MinerBot's database doesn't answer, which never turns a bot that's back into a failure
        static async Task<BotEventRow?> ConnectedAsync(IBotStatusReads status, BotStatusRow current)
        {
            try
            {
                return await status.FirstEventAfter(EventWords.DiscordConnected, new BotEventRow { at = current.started_at });
            }
            catch (Exception e) when (e is DbException or TimeoutException)
            {
                return null;
            }
        }

        // As the bot follows them: a stored status text that breaks the rules, after a rules change or written by hand, isn't
        // shown, and the bot shows its default status, which this says, with the rule but never the text
        internal static async Task<string> SettingsAsync(IBotRequestQueue queue)
        {
            BotSettingsRow? settings;

            try
            {
                settings = await queue.GetSettings();
            }
            catch (Exception e) when (e is DbException or TimeoutException)
            {
                return $"The bot's settings couldn't be read ({Failure(e)}).";
            }

            var posts = settings is { posts_paused: true } ? $"posts paused since {Day(settings.pause_changed_at)} UTC" : "posts aren't paused";
            var status = settings?.status_text is not { } text ? "the default status"
                : RequestRules.StatusTextProblem(text) is { } problem ? $"the default status, since the stored status text breaks the rules ({problem})"
                : $"status text \"{Printable(text)}\"";
            return $"The bot's settings now: {posts}; {status}.";
        }

        // Every kind, who may ask for it, how often the bot takes it, how long it waits to be taken, and what it needs
        internal static string List()
        {
            var list = new StringBuilder("The requests the bot takes, with who may ask, how often the bot takes each, how long one waits to be taken, and what it needs:\n");

            foreach (var kind in RequestKinds.All)
            {
                var needs = new List<string>();

                if (kind.NeedsServer)
                    needs.Add("--guild <server ID>");

                if (kind.NeedsChannel)
                    needs.Add("--channel <channel ID>");

                needs.Add(kind.Argument switch
                {
                    RequestArgument.Reason => "--text <the reason>",
                    RequestArgument.StatusText => "--text <the status text>, or none for the default",
                    RequestArgument.CustomLinkName => "--text <a custom profile link or its name>",
                    _ => ""
                });

                var what = string.Join(", ", needs.Where(need => need.Length > 0));
                list.Append(CultureInfo.InvariantCulture,
                    $"  {kind.Word,-20} {kind.Label}. {char.ToUpperInvariant(kind.WhoMayAsk[0])}{kind.WhoMayAsk[1..]}. {RequestWords.Limit(kind)}. Expires after {kind.Expiry.TotalMinutes:0} minutes.{(what.Length > 0 ? " Needs " + what + "." : "")}\n");
            }

            return list.ToString();
        }

        // The state's word as the line that ends a request starts with it, which request.sh reads
        static string StateWord(string? state) => state switch
        {
            RequestStates.Done => "Done",
            RequestStates.Failed => "Failed",
            RequestStates.Refused => "Refused",
            RequestStates.Expired => "Expired",
            _ => "Unknown"
        };

        // The type and SQL state, as migrate shows a failure: the driver's message names the login and the host
        static string Failure(Exception e) => $"{e.GetType().Name}, SQL state {(e as DbException)?.SqlState ?? "none"}";

        static string Clock(DateTime time) => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        // As in "3 minutes", or "5 seconds" for the tests' shorter waits
        static string Span(TimeSpan span) => span >= TimeSpan.FromMinutes(1)
            ? string.Create(CultureInfo.InvariantCulture, $"{span.TotalMinutes:0.#} minutes")
            : string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:0.#} seconds");

        static string Day(DateTime? time) => time?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "an unknown time";

        // A text from the table, with any control character shown as ?, so a row written by hand can't drive the terminal
        static string Printable(string text) => string.Concat(text.Select(c => char.IsControl(c) ? '?' : c));
    }
}
