using System.Net;
using Microsoft.Extensions.Options;

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

            // Empty means the default, as it does for Kestrel and the healthcheck command
            var listenAddress = string.IsNullOrWhiteSpace(options.ListenAddress) ? DashboardOptions.DefaultListenAddress : options.ListenAddress;

            if (!IPAddress.TryParse(listenAddress, out var address) || !IPAddress.IsLoopback(address))
                problems.Add("Dashboard:ListenAddress must be a loopback address such as 127.0.0.1, so that only nginx on the same server can reach the dashboard.");

            if (DashboardSettings.ListenPort(options.Port) == null)
                problems.Add("Dashboard:Port must be a whole number from 1 to 65535.");

            // Read here rather than through options, because DataAccessLibrary reads them as the app starts
            if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection")))
                problems.Add("ConnectionStrings:DefaultConnection isn't set. Give the connection string for the website's database, which holds the armor, the blacklist and each server's channels.");

            if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("BotConnection")))
                problems.Add("ConnectionStrings:BotConnection isn't set. Give the connection string for MinerBot's own database, which keeps the sessions and the audit log.");

            return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
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

            // Printable ASCII, which a header carries as it is
            if (!key.All(c => c is > ' ' and < '\x7f'))
                return "Dashboard:ProxyKey may only hold letters, digits and punctuation, with no spaces, because it travels in a header.";

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
}
