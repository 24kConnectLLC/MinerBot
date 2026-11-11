using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DataAccessLibrary;
using MinerBot.Dashboard.Health;
using MinerBot.Dashboard.Settings;

namespace MinerBot.Dashboard.Hosting
{
    // The two jobs the deploy and the container run with the same program: "migrate" brings MinerBot's own database up to
    // date, and "healthcheck" asks the running dashboard whether it answers, because the runtime image has no curl. Neither
    // starts the web host, so neither needs the web settings or tries to take the port or socket the dashboard holds.
    // "check-settings" is the third command, but Program.cs runs it, because it needs the web host's registrations.
    internal static class DashboardCommands
    {
        public const string Migrate = "migrate";
        public const string HealthCheck = "healthcheck";

        // Builds the web host and checks its settings as a start would, then exits without starting it
        public const string CheckSettings = "check-settings";

        static readonly TimeSpan ConnectLimit = TimeSpan.FromSeconds(2);
        static readonly TimeSpan HealthLimit = TimeSpan.FromSeconds(5);

        // Only the first argument, and only these exact words, so the arguments WebApplicationFactory and the host pass,
        // such as --environment=Production, never start one
        public static string? Find(string[] args) =>
            args is [var first, ..] && (string.Equals(first, Migrate, StringComparison.Ordinal) || string.Equals(first, HealthCheck, StringComparison.Ordinal)) ? first : null;

        public static async Task<int> RunAsync(string command, string[] args)
        {
            // There's no appsettings file in the image, so the environment and the command line are all there is
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args[1..]).Build();

            if (command == HealthCheck)
                return await HealthCheckAsync(configuration, Console.Out, CancellationToken.None);

            // Disposed at the end, so its lines are written out before the process exits
            using var loggers = LoggerFactory.Create(logging => logging.AddSimpleConsole(console => console.SingleLine = true));
            return await MigrateAsync(configuration, loggers.CreateLogger("MinerBot.Dashboard.Migrate"), CancellationToken.None);
        }

        // With the bot's login, which can create tables. 0 when the database is up to date, otherwise 1 with one line saying why.
        internal static async Task<int> MigrateAsync(IConfiguration configuration, ILogger logger, CancellationToken cancellationToken)
        {
            var connection = configuration.GetConnectionString("BotConnection");

            if (string.IsNullOrWhiteSpace(connection))
            {
                logger.LogError("ConnectionStrings:BotConnection isn't set, so there's no database to migrate");
                return 1;
            }

            try
            {
                var result = await DatabaseMigrations.MigrateAsync(connection, logger, cancellationToken);
                logger.LogInformation("MinerBot's database is at version {Version}", result.Version);
                return 0;
            }
            catch (Exception e) when (e is InvalidOperationException or TimeoutException or ArgumentException)
            {
                // DataAccessLibrary's own words, which never name the login or the host
                logger.LogError("The migrations didn't run: {Reason}", e.Message);
                return 1;
            }
            catch (DbException e)
            {
                // The driver's text names the login and the host, so only its type and SQL state are shown
                logger.LogError("The migrations didn't run: the database answered with {Type}, SQL state {State}", e.GetType().Name, e.SqlState ?? "none");
                return 1;
            }
            catch (Exception e)
            {
                logger.LogError("The migrations didn't run: {Type}", e.GetType().Name);
                return 1;
            }
        }

        // Asks the dashboard where it listens, as the container's health check: on its Unix socket when Dashboard:UnixSocket
        // is set, otherwise on its address. Live, not ready, so a database outage doesn't mark the container unhealthy.
        internal static async Task<int> HealthCheckAsync(IConfiguration configuration, TextWriter output, CancellationToken cancellationToken)
        {
            var unixSocket = DashboardSettings.UnixSocket(configuration);
            var endpoint = DashboardSettings.ListenEndpoint(configuration);

            if (unixSocket != null && !DashboardSettings.IsUnixSocketPath(unixSocket))
            {
                await output.WriteLineAsync("Unhealthy: Dashboard:UnixSocket isn't valid");
                return 1;
            }

            if (unixSocket == null && endpoint == null)
            {
                await output.WriteLineAsync("Unhealthy: Dashboard:ListenAddress or Dashboard:Port isn't valid");
                return 1;
            }

            // No proxy, so an HTTP_PROXY variable can't send the check anywhere else
            using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = ConnectLimit };
            using var client = new HttpClient(handler) { Timeout = HealthLimit };
            string address;

            if (unixSocket != null)
            {
                // Every connection goes to the socket, whatever the address says
                handler.ConnectCallback = (_, token) => ConnectAsync(unixSocket, token);
                address = "http://localhost" + HealthEndpoints.LivePath;
            }
            else
            {
                // http://127.0.0.1:5080 unless the settings say otherwise, with an IPv6 address in brackets
                var host = endpoint!.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{endpoint.Address}]" : endpoint.Address.ToString();
                address = string.Create(CultureInfo.InvariantCulture, $"http://{host}:{endpoint.Port}{HealthEndpoints.LivePath}");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, address);

            // The health endpoints and the proxy key's exemption go by the Host header, not by the address
            request.Headers.Host = "localhost";

            try
            {
                using var response = await client.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.StatusCode == HttpStatusCode.OK && body == "Healthy")
                {
                    await output.WriteLineAsync("Healthy");
                    return 0;
                }

                await output.WriteLineAsync($"Unhealthy: {(int)response.StatusCode}");
                return 1;
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
            {
                await output.WriteLineAsync($"Unhealthy: {e.GetType().Name}");
                return 1;
            }
        }

        static async ValueTask<Stream> ConnectAsync(string unixSocket, CancellationToken cancellationToken)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(unixSocket), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}
