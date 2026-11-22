using System.Globalization;
using DataAccessLibrary;
using DataAccessLibrary.Dashboard;
using DataAccessLibrary.Dashboard.Models;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Requests.Models;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Settings;
using MinerBot.Dashboard.Status;
using MinerBot.Shared.Requests;
using MySql.Data.MySqlClient;

namespace MinerBot.Dashboard.Security
{
    // How a check came out: fine, worth knowing, worth a look soon, or something to put right
    public enum CheckResult
    {
        Ok,
        Note,
        Warning,
        Problem
    }

    // One of the Sessions page's checks: what it checked, how it came out, and why, in fixed words that never hold a key,
    // a token, a password's hash or an exception's message
    public sealed record SelfCheck(string Name, CheckResult Result, string Detail)
    {
        public string Label => Result switch
        {
            CheckResult.Ok => "OK",
            CheckResult.Note => "Note",
            CheckResult.Warning => "Warning",
            _ => "Problem"
        };

        public string Marker => Result switch
        {
            CheckResult.Ok => "marker-ok",
            CheckResult.Problem => "marker-block",
            _ => "marker-warn"
        };
    }

    // The Sessions and security page's seven checks, each run when the Owner opens the page, as the dashboard's own login:
    // its database rights, the sign-in keys' folder, MinerBot's database structure, Cloudflare Access, requests to the bot,
    // the owner's direct messages and the sign-in messages. Each is caught on its own, so one that fails says so and the
    // others still show; it says the exception's type, and MariaDB's error number, never the message, which can name the
    // login and the host. The rules and words are pure, so each is tested without the app.
    internal static class SelfChecks
    {
        public const string Rights = "Database rights";
        public const string Keys = "Sign-in keys";
        public const string Structure = "Database structure";
        public const string Access = "Cloudflare Access";
        public const string Requests = "Requests to the bot";
        public const string DirectMessages = "Direct messages to the owner";
        public const string SigninMessages = "Sign-in messages";

        // MariaDB's refusal for a table the login has no right on
        public const int NoTableRight = 1142;

        // The login's rights against the list this version uses, and its two limits against deploy's
        public static SelfCheck OfRights(OwnRights rights)
        {
            ArgumentNullException.ThrowIfNull(rights);

            var found = new List<(CheckResult Result, string Words)>();

            if (rights.Missing.Count > 0)
                found.Add((CheckResult.Problem, $"The login lacks rights this version uses: {string.Join(", ", rights.Missing)}. Pages that need them fail. "
                    + "./setup-dashboard.sh --no-questions applies them again."));

            if (rights.Extra.Count > 0)
                found.Add((CheckResult.Note, $"The login has rights this version doesn't use: {string.Join(", ", rights.Extra)}. deploy.sh keeps a previous version's "
                    + "rights for a rollback for a deploy or two after one is dropped; ./setup-dashboard.sh --no-questions takes away one added by hand."));

            if (rights.Connections != DashboardRights.Connections || rights.StatementSeconds != DashboardRights.StatementSeconds)
                found.Add((CheckResult.Problem, $"The login has {Connections(rights.Connections)} and {Seconds(rights.StatementSeconds)}, where this version expects "
                    + $"{Connections(DashboardRights.Connections)} and {Seconds(DashboardRights.StatementSeconds)}. ./setup-dashboard.sh --no-questions sets them again."));

            return Of(Rights, found, $"The login has exactly the rights this version uses, with {Connections(DashboardRights.Connections)} and "
                + $"{Seconds(DashboardRights.StatementSeconds)}.");
        }

        static string Connections(long? count) => count switch
        {
            null => "a connection limit MariaDB doesn't give as a number",
            0 => "no connection limit",
            _ => $"at most {count.Value.ToString(CultureInfo.InvariantCulture)} connections"
        };

        static string Seconds(decimal? seconds) => seconds switch
        {
            null => "a statement time limit MariaDB doesn't give as a number",
            0 => "no statement time limit",
            _ => $"{seconds.Value.ToString("0.######", CultureInfo.InvariantCulture)} seconds a statement"
        };

        // The keys folder and the temporary folder each new key passes through, as the settings' check finds them, with
        // how many keys the folder holds
        public static SelfCheck OfKeys(string folder, string? folderProblem, string? tempProblem, int keys)
        {
            var problems = new[] { folderProblem, tempProblem }.OfType<string>().ToList();

            return problems.Count > 0
                ? new SelfCheck(Keys, CheckResult.Problem, string.Join(" ", problems))
                : new SelfCheck(Keys, CheckResult.Ok, $"Writable: {folder}, with {TimeWords.Count(keys, "sign-in key")}, and so is the temporary folder each new key passes through.");
        }

        // The migrations MinerBot's database recorded against the files built into this version
        public static SelfCheck OfStructure(IReadOnlyList<MigrationInfo> expected, IReadOnlyList<MigrationInfo> recorded)
        {
            ArgumentNullException.ThrowIfNull(expected);
            ArgumentNullException.ThrowIfNull(recorded);

            var found = new List<(CheckResult Result, string Words)>();

            foreach (var migration in expected)
            {
                var ran = recorded.FirstOrDefault(row => row.Version == migration.Version);

                if (ran == null)
                    found.Add((CheckResult.Problem, $"Migration {migration.Name} hasn't run. deploy.sh runs the migrations before it swaps the dashboard, so ./status.sh "
                        + "says what's deployed."));
                else if (!string.Equals(ran.Checksum, migration.Checksum, StringComparison.OrdinalIgnoreCase))
                    found.Add((CheckResult.Problem, $"Migration {migration.Name} in the database isn't this version's file, so the database may not match the code."));
            }

            foreach (var ahead in recorded.Where(row => expected.All(migration => migration.Version != row.Version)))
                found.Add((CheckResult.Note, $"MinerBot's database has migration {ahead.Version.ToString(CultureInfo.InvariantCulture)}, which this version doesn't: a "
                    + "newer version ran it, as after ./rollback.sh dashboard. Migrations only add, so this version still works."));

            var range = expected.Count switch
            {
                0 => "no migrations",
                1 => $"migration {expected[0].Version.ToString(CultureInfo.InvariantCulture)}",
                _ => $"migrations {expected[0].Version.ToString(CultureInfo.InvariantCulture)} to {expected[^1].Version.ToString(CultureInfo.InvariantCulture)}"
            };

            return Of(Structure, found, $"Up to date: {range}.");
        }

        public static SelfCheck StructureUnreadable() =>
            new(Structure, CheckResult.Problem, "The login can't read schema_version. ./setup-dashboard.sh --no-questions applies its rights again.");

        // On, this page came through Access, since the check runs on every /admin request before the sign-in. Keys older than
        // an hour mean every fetch since has failed: a stale set is fetched again by the next request that needs a key.
        public static SelfCheck OfAccess(bool enabled, string teamDomain, (int Count, DateTimeOffset FetchedAt)? held, CloudflareAccessLimits limits, DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(limits);

            if (!enabled)
                return new SelfCheck(Access, CheckResult.Note, "Off: /admin opens with a Discord sign-in alone. The plan recommends Access.");

            if (held is not { } keys)
                return new SelfCheck(Access, CheckResult.Ok, $"On, for {teamDomain}: this page came through Cloudflare Access.");

            var fetched = TimeWords.Utc(keys.FetchedAt.UtcDateTime);

            return now - keys.FetchedAt < limits.FreshFor
                ? new SelfCheck(Access, CheckResult.Ok, $"On, for {teamDomain}: this page came through Cloudflare Access, checked with {TimeWords.Count(keys.Count, "key")} "
                    + $"fetched at {fetched}.")
                : new SelfCheck(Access, CheckResult.Warning, $"On, but Cloudflare's keys were fetched at {fetched} and every fetch since has failed. Once they're "
                    + $"{TimeWords.Count((long)limits.UsableFor.TotalHours, "hour")} old, /admin refuses everyone until a fetch works.");
        }

        // The dashboard's key, and the bot's own word on requests: unable to check them since this run started, or refusing
        // one as not the dashboard's in the last day, as banner 4 words it, an earlier one it didn't sign included. Never a
        // key, nor whether the two match, which only status.sh can see.
        public static SelfCheck OfRequests(bool requestsOn, BotStatusRow? status, BotEventRow? requestsOff, BotEventRow? rejected, DateTime now, BotEventRow? unsigned = null)
        {
            var problems = new List<string>();

            if (!requestsOn)
                problems.Add(RequestBanners.DashboardOff);

            if (RequestBanners.BotOff(status, requestsOff) is { } botOff)
                problems.Add(botOff);

            if (RequestBanners.Rejected(rejected, now, unsigned) is { } refused)
                problems.Add(refused);

            var on = requestsOn ? "On: the dashboard signs its requests." : "";

            return problems.Count == 0
                ? new SelfCheck(Requests, CheckResult.Ok, on)
                : new SelfCheck(Requests, CheckResult.Problem, string.Join(" ", problems.Prepend(on).Where(words => words.Length > 0)));
        }

        // The bot's last direct message to the owner, whether it went
        public static SelfCheck OfDirectMessages(RequestPageRow? latest)
        {
            if (latest == null)
                return new SelfCheck(DirectMessages, CheckResult.Note, "The bot hasn't recorded a direct message to the owner yet.");

            var at = latest.ended_at is { } ended ? TimeWords.Utc(ended) : RequestWords.UnreadableTime;
            var request = latest.id.ToString(CultureInfo.InvariantCulture);

            return latest.dm_state == RequestDmStates.Failed
                ? new SelfCheck(DirectMessages, CheckResult.Problem, $"The bot's last direct message to the owner failed at {at} (request {request}). Discord refuses them "
                    + "while the owner doesn't allow direct messages from members of a server they share with MinerBot.")
                : new SelfCheck(DirectMessages, CheckResult.Ok, $"The bot's last direct message to the owner went at {at} (request {request}).");
        }

        // Whether sign-ins ask the bot to tell the owner, how many did in the last hour against the bot's limit, and how the
        // latest went: left to expire a warning. One the bot refused in the last day is a problem, the latest or one before
        // it, since every sign-in after it, the owner's own too, writes a later one.
        public static SelfCheck OfSigninMessages(bool requestsOn, long lastHour, RequestPageRow? latest, RequestPageRow? refused, DateTime now)
        {
            var limit = RequestKinds.Find(RequestKinds.OwnerSigninAlert)!.Limit;
            var words = new List<string>
            {
                requestsOn
                    ? "On: every sign-in asks the bot to send the owner a direct message."
                    : "Off: the dashboard has no request-signing key, so sign-ins send the owner no message.",
                $"{TimeWords.Count(lastHour, "sign-in")} asked for a message in the last hour, of the bot's {limit.ToString(CultureInfo.InvariantCulture)}."
            };
            var result = requestsOn ? CheckResult.Ok : CheckResult.Problem;

            if (latest == null)
            {
                words.Add("No sign-in has asked for one yet.");
                return new SelfCheck(SigninMessages, result, string.Join(" ", words));
            }

            var view = RequestViews.Of(latest, null, now, RequestViews.Waits);
            words.Add($"The latest, request {view.IdText} at {view.AskedAtUtc}: {view.StageLabel}.");

            if (view.Stage == RequestStage.Expired)
            {
                words.Add("It expired without the bot taking it, so its message never went.");
                result = result == CheckResult.Ok ? CheckResult.Warning : result;
            }

            // As banner 7 counts it: one whose time can't be read may be recent
            var refusedAt = refused?.ended_at ?? refused?.created_at;

            if (refused is { state: RequestStates.Refused } && (refusedAt == null || now - refusedAt.Value <= RequestBanners.RefusalsFor))
            {
                var earlier = RequestViews.Of(refused, null, now, RequestViews.Waits);
                words.Add(refused.id == latest.id
                    ? $"The bot refused it: {refused.result ?? "no reason is recorded"}"
                    : $"The bot refused an earlier one, request {earlier.IdText} at {earlier.AskedAtUtc}: {refused.result ?? "no reason is recorded"}");
                result = CheckResult.Problem;
            }

            return new SelfCheck(SigninMessages, result, string.Join(" ", words));
        }

        // A check that threw: its type, and MariaDB's error number for one of MariaDB's errors, never its message
        public static SelfCheck Failed(string name, Exception failure)
        {
            ArgumentNullException.ThrowIfNull(failure);

            var number = failure is MySqlException mariaDb ? $", error {mariaDb.Number.ToString(CultureInfo.InvariantCulture)}" : "";
            return new SelfCheck(name, CheckResult.Problem, $"Couldn't check: {failure.GetType().Name}{number}.");
        }

        // The worst of what was found, in the order found, or the words for none
        static SelfCheck Of(string name, IReadOnlyList<(CheckResult Result, string Words)> found, string fine) => found.Count == 0
            ? new SelfCheck(name, CheckResult.Ok, fine)
            : new SelfCheck(name, found.Max(item => item.Result), string.Join(" ", found.Select(item => item.Words)));
    }

    // The checks' reads, made when the Owner opens the page: about nine small reads and two tiny files written and deleted.
    // Each check runs on its own, and any exception but a cancellation becomes that check's "Couldn't check".
    internal sealed class SelfCheckReads(IDashboardChecks checks, IBotStatusReads status, IBotRequestReads requests, IOptions<DashboardOptions> dashboard,
        IOptions<CloudflareAccessOptions> access, CloudflareAccessKeys keys, CloudflareAccessLimits limits, TimeProvider time, ILogger<SelfCheckReads> logger)
    {
        public async Task<IReadOnlyList<SelfCheck>> RunAsync()
        {
            var now = time.GetUtcNow();
            var requestsOn = RequestSigning.ParseKey(dashboard.Value.RequestSigningKey) != null;

            return
            [
                await RunAsync(SelfChecks.Rights, async () => SelfChecks.OfRights(await checks.ListOwnRights())),
                await RunAsync(SelfChecks.Keys, () => Task.FromResult(KeysCheck())),
                await RunAsync(SelfChecks.Structure, StructureAsync),
                await RunAsync(SelfChecks.Access, () => Task.FromResult(SelfChecks.OfAccess(access.Value.Enabled, access.Value.TeamDomain, keys.Held(), limits, now))),
                await RunAsync(SelfChecks.Requests, async () =>
                {
                    var rejected = await status.LatestEvent(EventWords.RequestRejected);

                    return SelfChecks.OfRequests(requestsOn, await status.GetStatus(), await status.LatestEvent(EventWords.RequestsOff), rejected, now.UtcDateTime,
                        await RequestBanners.UnsignedBefore(status, rejected));
                }),
                await RunAsync(SelfChecks.DirectMessages, async () => SelfChecks.OfDirectMessages(await requests.LatestDm())),
                await RunAsync(SelfChecks.SigninMessages, async () => SelfChecks.OfSigninMessages(requestsOn,
                    await requests.CountAlertsSince((now - TimeSpan.FromHours(1)).UtcDateTime),
                    (await requests.ListLatest([RequestKinds.OwnerSigninAlert])).FirstOrDefault(), await requests.LatestRefusedAlert(), now.UtcDateTime))
            ];
        }

        // Writes and deletes the validator's probe file in the keys folder, and a temporary file, as the start-up check does
        SelfCheck KeysCheck()
        {
            var folder = dashboard.Value.KeysFolder;
            var folderProblem = DashboardOptionsValidator.KeysFolderProblem(folder);
            var keyCount = folderProblem == null ? Directory.GetFiles(folder, "key-*.xml").Length : 0;

            return SelfChecks.OfKeys(folder, folderProblem, DashboardOptionsValidator.TempFolderProblem(), keyCount);
        }

        async Task<SelfCheck> StructureAsync()
        {
            try
            {
                return SelfChecks.OfStructure(DatabaseMigrations.Expected(), await checks.ListMigrations());
            }
            catch (MySqlException e) when (e.Number == SelfChecks.NoTableRight)
            {
                return SelfChecks.StructureUnreadable();
            }
        }

        async Task<SelfCheck> RunAsync(string name, Func<Task<SelfCheck>> check)
        {
            try
            {
                return await check();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning("The Sessions page's check of {Check} failed with {Type}", name, e.GetType().Name);
                return SelfChecks.Failed(name, e);
            }
        }
    }

    internal static class SelfCheckServices
    {
        public static IServiceCollection AddSelfChecks(this IServiceCollection services) => services.AddTransient<SelfCheckReads>();
    }
}
