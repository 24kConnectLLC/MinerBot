using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Hosting;

// The settings rule, also in deploy/README.md: rollback.sh keeps the current dashboard.env, so the previous image must
// still start with it. A setting is never renamed or removed in the pull request that stops using it: the old name stays
// accepted and written for one more pull request. A new setting gets an empty-means-off default wherever it can, and
// setup-dashboard.sh never writes a value in a form the previous image's validator refuses. Every validator here ignores
// settings it doesn't know, so a setting a newer pull request added never stops an older image.

namespace MinerBot.Dashboard.Settings
{
    // Checks the Dashboard section and the two connection strings at start-up, and again whenever the settings reload.
    // Every problem is reported at once, each naming its key and what it's for, and no message ever repeats a value,
    // because a value may be a secret.
    internal sealed class DashboardOptionsValidator(IConfiguration configuration) : IValidateOptions<DashboardOptions>
    {
        // 128 bits as hex, which nobody can guess
        internal const int MinProxyKeyLength = 32;

        // Not .xml, so Data Protection never takes it for a key
        internal const string ProbeName = ".write-check";

        public ValidateOptionsResult Validate(string? name, DashboardOptions options)
        {
            if (name != Options.DefaultName)
                return ValidateOptionsResult.Skip;

            var problems = new List<string>();

            if (OriginProblem(options.PublicOrigin, out _) is { } origin)
                problems.Add(origin);

            if (ProxyKeyProblem(options.ProxyKey) is { } proxyKey)
                problems.Add(proxyKey);

            ListProblems(options, problems);

            if (KeysFolderProblem(options.KeysFolder) is { } keysFolder)
                problems.Add(keysFolder);

            if (TempFolderProblem() is { } tempFolder)
                problems.Add(tempFolder);

            // Empty means the default, as it does for Kestrel and the healthcheck command
            var listenAddress = string.IsNullOrWhiteSpace(options.ListenAddress) ? DashboardOptions.DefaultListenAddress : options.ListenAddress;

            if (!IPAddress.TryParse(listenAddress, out var address) || !IPAddress.IsLoopback(address))
                problems.Add("Dashboard:ListenAddress must be a loopback address such as 127.0.0.1, so that only nginx on the same server can reach the dashboard.");

            if (DashboardSettings.ListenPort(options.Port) == null)
                problems.Add("Dashboard:Port must be a whole number from 1 to 65535.");

            if (UnixSocketProblem(options.UnixSocket) is { } unixSocket)
                problems.Add(unixSocket);

            // Read here rather than through options, because DataAccessLibrary reads them as the app starts
            ConnectionStringProblem("DefaultConnection", "the website's database, which holds the armor, the blacklist and each server's channels", problems);
            ConnectionStringProblem("BotConnection", "MinerBot's own database, which keeps the sessions and the audit log", problems);

            return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
        }

        // Set, readable by the driver and naming its database. A string without one would pass the readiness check, which
        // needs no database, and then fail every sign-in.
        void ConnectionStringProblem(string key, string database, List<string> problems)
        {
            var value = configuration.GetConnectionString(key);

            if (string.IsNullOrWhiteSpace(value))
                problems.Add($"ConnectionStrings:{key} isn't set. Give the connection string for {database}.");
            else if (DatabaseNames.Problem(value) is { } problem)
                problems.Add($"ConnectionStrings:{key} {problem}. Give the connection string for {database}, as dashboard.env.example shows.");
        }

        // Keeps the origin as scheme://host[:port], so it compares with a browser's Origin header as it is. One that
        // doesn't pass is left alone, so the validator reports it.
        internal static void Normalize(DashboardOptions options)
        {
            if (OriginProblem(options.PublicOrigin, out var origin) == null)
                options.PublicOrigin = origin;
        }

        // Null when the origin is fine, which then comes out normalised
        internal static string? OriginProblem(string? text, out string origin)
        {
            origin = "";

            if (string.IsNullOrWhiteSpace(text))
                return "Dashboard:PublicOrigin isn't set. Give the address people open the dashboard at, such as https://bot.murderminershub.com, because Discord's sign-in comes back there.";

            if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
                return "Dashboard:PublicOrigin isn't a whole address. Give one such as https://bot.murderminershub.com.";

            // Plain http only for localhost, where a local run's health checks and public pages work without https. The
            // session and form cookies only travel over https, so signing in needs https even there.
            var local = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

            if (uri.Scheme != Uri.UriSchemeHttps && !(local && uri.Scheme == Uri.UriSchemeHttp))
                return "Dashboard:PublicOrigin must start with https://, because the sign-in cookies only travel over https. Plain http is allowed only for localhost, and even there it can't sign anyone in.";

            if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
                return "Dashboard:PublicOrigin must be only the scheme and the host name, such as https://bot.murderminershub.com, with no path, query or user name.";

            origin = uri.GetLeftPart(UriPartial.Authority);
            return null;
        }

        static string? ProxyKeyProblem(string? key)
        {
            if (string.IsNullOrEmpty(key))
                return "Dashboard:ProxyKey isn't set. Give the secret that nginx adds to every request it passes on, or the dashboard answers nothing.";

            // What hex and base64url keys are made of. nginx's configuration, which has to send the key, reads $ as the start
            // of a variable and quotes and backslashes as escapes, so a key with them may be sent as something else.
            if (!key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                return "Dashboard:ProxyKey may only hold letters, digits, - and _, because nginx's configuration reads other characters as variables or escapes. Use 64 random hex characters.";

            if (key.Length < MinProxyKeyLength)
                return $"Dashboard:ProxyKey is shorter than {MinProxyKeyLength} characters. Use 64 random hex characters, so that it can't be guessed.";

            return null;
        }

        static void ListProblems(DashboardOptions options, List<string> problems)
        {
            var ownersRead = DiscordIds.TryParse(options.Owners, out var owners, out var ownersProblem);
            var adminsRead = DiscordIds.TryParse(options.Admins, out var admins, out var adminsProblem);

            if (!ownersRead)
                problems.Add($"Dashboard:Owners can't be read: {ownersProblem}. Copy each ID in Discord with Developer Mode on, and separate them with commas.");
            else if (owners.Count == 0)
                problems.Add("Dashboard:Owners isn't set. List the owner's numeric Discord user ID, or nobody can sign in.");

            if (!adminsRead)
                problems.Add($"Dashboard:Admins can't be read: {adminsProblem}. Copy each ID in Discord with Developer Mode on, and separate them with commas.");

            if (owners.Overlaps(admins))
                problems.Add("An ID is in both Dashboard:Owners and Dashboard:Admins. Put each person in one list only.");
        }

        static string? KeysFolderProblem(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return "Dashboard:KeysFolder isn't set. Give the folder that keeps the sign-in keys between restarts, such as /app/keys, or every restart signs everyone out.";

            if (!Path.IsPathFullyQualified(folder))
                return "Dashboard:KeysFolder must be a full path, such as /app/keys.";

            if (File.Exists(folder))
                return "Dashboard:KeysFolder names a file. It must be a folder the dashboard can write to.";

            // Written and deleted here, so a folder the app can't write to stops it now, not at the first sign-in
            try
            {
                Directory.CreateDirectory(folder);
                var probe = Path.Combine(folder, ProbeName);
                File.WriteAllBytes(probe, []);
                File.Delete(probe);
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return "Dashboard:KeysFolder can't be written to, so the sign-in keys couldn't be kept. Give the dashboard's user write access to it.";
            }
        }

        // Empty means the TCP address, as it does for Kestrel and the healthcheck command. A socket that's already there was
        // left by a dashboard that was killed, and the start replaces it, so only another kind of file stops it here. The
        // folder isn't checked: compose.yml won't start the container without it, and check-settings runs without it.
        static string? UnixSocketProblem(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            if (!DashboardSettings.IsUnixSocketPath(path))
                return $"Dashboard:UnixSocket must be the full path of the socket file, such as /run/minerbot-dashboard/dashboard.sock, with no spaces and at most {DashboardSettings.MaxUnixSocketBytes} bytes.";

            if (Path.Exists(path) && !UnixSockets.IsSocket(path))
                return "Dashboard:UnixSocket names a file or folder that isn't a socket, so the dashboard can't listen there. It never deletes one, so point the setting at a path that's free.";

            return null;
        }

        // Data Protection writes each new key to the system's temporary folder first, off Windows, and then moves it into
        // the keys folder. A read-only container without a /tmp of its own would start and pass its health check, then
        // fail every sign-in once it needed a new key, possibly months later.
        internal static string? TempFolderProblem()
        {
            try
            {
                File.Delete(Path.GetTempFileName());
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return "The system's temporary folder can't be written to, and each new sign-in key is written there first. Run the container with --tmpfs /tmp.";
            }
        }
    }

    internal sealed class DiscordSignInOptionsValidator : IValidateOptions<DiscordSignInOptions>
    {
        public ValidateOptionsResult Validate(string? name, DiscordSignInOptions options)
        {
            if (name != Options.DefaultName)
                return ValidateOptionsResult.Skip;

            var problems = new List<string>();

            if (string.IsNullOrWhiteSpace(options.ClientId))
                problems.Add("Discord:ClientId isn't set. Give the MinerBot Dashboard application's client ID from the Discord Developer Portal.");
            else if (options.ClientId.Length is < 17 or > 20 || !options.ClientId.All(char.IsAsciiDigit))
                problems.Add("Discord:ClientId must be the application's numeric client ID, 17 to 20 digits.");

            if (string.IsNullOrEmpty(options.ClientSecret))
                problems.Add("Discord:ClientSecret isn't set. Give the MinerBot Dashboard application's client secret from the Discord Developer Portal.");
            else if (options.ClientSecret.Any(char.IsWhiteSpace))
                problems.Add("Discord:ClientSecret has white space in it. Paste it again without spaces or line breaks.");

            CheckEndpoint(options.AuthorizationEndpoint, "Discord:AuthorizationEndpoint", problems);
            CheckEndpoint(options.TokenEndpoint, "Discord:TokenEndpoint", problems);
            CheckEndpoint(options.RevocationEndpoint, "Discord:RevocationEndpoint", problems);
            CheckEndpoint(options.UserInformationEndpoint, "Discord:UserInformationEndpoint", problems);

            return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
        }

        // https without a user name, so a typo can't send the client secret or a token unencrypted
        static void CheckEndpoint(string? address, string key, List<string> problems)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || string.IsNullOrEmpty(uri.Host))
                problems.Add($"{key} must be an https address without a user name, so that the client secret and tokens are never sent unencrypted.");
        }
    }

    // Both empty leaves the Cloudflare Access check off, and one without the other is refused. Each one that's set must have
    // the one form setup-dashboard.sh writes, so a typo stops the dashboard here instead of refusing every admin page later.
    internal sealed partial class CloudflareAccessOptionsValidator : IValidateOptions<CloudflareAccessOptions>
    {
        public ValidateOptionsResult Validate(string? name, CloudflareAccessOptions options)
        {
            if (name != Options.DefaultName)
                return ValidateOptionsResult.Skip;

            var teamSet = !string.IsNullOrEmpty(options.TeamDomain);
            var audienceSet = !string.IsNullOrEmpty(options.Audience);
            var problems = new List<string>();

            if (teamSet != audienceSet)
                problems.Add("CloudflareAccess:TeamDomain and CloudflareAccess:Audience go together: set both to turn the Cloudflare Access check on, or neither to leave it off.");

            // Cloudflare's own form: the team's name, which DNS allows up to 63 characters, then cloudflareaccess.com
            if (teamSet && !TeamDomain().IsMatch(options.TeamDomain))
                problems.Add("CloudflareAccess:TeamDomain must be just the team's host name in small letters, such as yourteam.cloudflareaccess.com.");

            if (audienceSet && !Audience().IsMatch(options.Audience))
                problems.Add("CloudflareAccess:Audience must be the Access application's AUD tag: 64 characters, 0-9 and a-f.");

            return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
        }

        [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.cloudflareaccess\.com\z", RegexOptions.CultureInvariant)]
        private static partial Regex TeamDomain();

        [GeneratedRegex(@"^[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
        private static partial Regex Audience();
    }
}
