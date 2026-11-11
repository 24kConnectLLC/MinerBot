namespace MinerBot.Dashboard.Settings
{
    // The Dashboard section, from the environment on the server. DashboardOptionsValidator checks every value at start-up.
    internal sealed class DashboardOptions
    {
        public const string Section = "Dashboard";

        // nginx reaches the dashboard here, and the healthcheck command looks here too
        public const string DefaultListenAddress = "127.0.0.1";
        public const int DefaultPort = 5080;

        // Where people open the dashboard, such as https://bot.murderminershub.com, kept as scheme://host[:port]. Discord's
        // sign-in returns there, and changes must come from there.
        public string PublicOrigin { get; set; } = "";

        // The secret nginx adds to every request it passes on
        public string ProxyKey { get; set; } = "";

        // Numeric Discord user IDs separated by commas, semicolons or white space. Text, because environment variables are.
        public string Owners { get; set; } = "";
        public string Admins { get; set; } = "";

        // Where Data Protection keeps its keys, a volume in the container, so a restart signs nobody out
        public string KeysFolder { get; set; } = "";

        public string ListenAddress { get; set; } = DefaultListenAddress;

        // Text, like the rest, so a value that isn't a number is reported with the other problems instead of failing as
        // the section is read. Empty means the default.
        public string Port { get; set; } = "";

        // A Unix socket to listen on instead of ListenAddress and Port, such as /run/minerbot-dashboard/dashboard.sock. On
        // the server compose.yml sets it, in a folder only the dashboard can write to and only nginx's group can open, so no
        // other account can take the dashboard's place while it restarts, as one could with a TCP port. Empty keeps the TCP
        // address.
        public string UnixSocket { get; set; } = "";
    }
}
