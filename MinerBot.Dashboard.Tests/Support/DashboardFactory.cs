using System.Net;
using System.Security.Cryptography;
using DataAccessLibrary.Tests.Database;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Services;

namespace MinerBot.Dashboard.Tests.Support
{
    // What a test can change about the app it starts
    internal sealed record DashboardFactoryOptions
    {
        // Shared by two factories that stand for one dashboard restarting. Without one, each factory has its own.
        public string? KeysFolder { get; init; }

        // The fixture's databases unless given, such as DeadDatabase's
        public string? SiteConnection { get; init; }
        public string? BotConnection { get; init; }

        // Over the defaults. An empty value stands for a setting that's missing.
        public IReadOnlyDictionary<string, string?> Settings { get; init; } = new Dictionary<string, string?>();

        // After the factory's own, such as a repository that fails
        public Action<IServiceCollection>? Services { get; init; }
    }

    // The dashboard as Program.cs builds it, as Production, on TestServer, with the fixture's MariaDB, a fake Discord, a
    // fake clock and made-up secrets of its own. Clients reach it the way nginx would.
    internal sealed class DashboardFactory : WebApplicationFactory<Program>
    {
        public const string PublicOrigin = "https://dashboard.test";
        public const string ClientId = "1000000000000000009";

        // A .test host keeps the made-up name out of real DNS, and differs from the health checks' localhost
        public static readonly Uri PublicAddress = new(PublicOrigin);
        public static readonly Uri LocalAddress = new("https://localhost");

        static readonly TimeSpan ClientLimit = TimeSpan.FromSeconds(30);

        readonly DashboardFactoryOptions options;
        readonly bool ownsKeysFolder;

        public DashboardFactory(MariaDbFixture database, DashboardFactoryOptions? options = null)
        {
            this.options = options ?? new DashboardFactoryOptions();

            ownsKeysFolder = this.options.KeysFolder == null;
            KeysFolder = this.options.KeysFolder ?? NewKeysFolder();

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Dashboard:PublicOrigin"] = PublicOrigin,
                ["Dashboard:ProxyKey"] = ProxyKey,
                ["Dashboard:Owners"] = TestUsers.Owner.IdText,
                ["Dashboard:Admins"] = $"{TestUsers.Admin.IdText},{TestUsers.SecondAdmin.IdText}",
                ["Dashboard:KeysFolder"] = KeysFolder,
                ["Discord:ClientId"] = ClientId,
                ["Discord:ClientSecret"] = ClientSecret,
                ["ConnectionStrings:DefaultConnection"] = this.options.SiteConnection ?? database.SiteConnection,
                ["ConnectionStrings:BotConnection"] = this.options.BotConnection ?? database.BotConnection
            };

            foreach (var (key, value) in this.options.Settings)
                values[key] = value;

            Settings = new TestSettings(values);
            Discord = new FakeDiscord(ClientId, ClientSecret, PublicOrigin + DashboardAuth.CallbackPath);
        }

        // Made up for each factory, as 64 hex characters like the real one
        public string ProxyKey { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        public string ClientSecret { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

        public string KeysFolder { get; }
        public TestSettings Settings { get; }
        public FakeDiscord Discord { get; }
        public FakeClock Clock { get; } = new();
        public RecordingLoggerProvider Logs { get; } = new();

        // Under the test's own output folder, never the system's temp folder
        public static string NewKeysFolder() => Path.Combine(AppContext.BaseDirectory, "test-keys", Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // The factory's own default is Development, which would add the developer exception page
            builder.UseEnvironment(Environments.Production);

            // Through UseSetting, which reaches Program.cs as arguments from its first line, because AddDataAccessServices
            // reads the connection strings the moment it's called. The same values come last again, where a test can change them.
            foreach (var (key, value) in Settings.Values)
                builder.UseSetting(key, value);

            builder.ConfigureAppConfiguration(configuration => configuration.Add(Settings));

            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(Logs);
                logging.SetMinimumLevel(LogLevel.Debug);
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);

                // The OAuth handler builds its client from this in a PostConfigure, so it's set with Configure
                services.Configure<OAuthOptions>(DashboardAuth.DiscordScheme, oauth => oauth.BackchannelHttpHandler = Discord);
                services.AddHttpClient<DiscordUserApi>().ConfigurePrimaryHttpMessageHandler(() => new NonDisposingHandler(Discord));
                services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(() => new NonDisposingHandler(Discord)));

                // The Access check's client sets its own handler, which would win over the defaults, so it's sent to the fake
                // Discord too, which throws for an address it doesn't serve. A test that turns the check on installs
                // FakeCloudflareAccess below, which comes later and wins, and one that forgot can't reach Cloudflare.
                services.AddHttpClient(CloudflareAccessKeys.ClientName).ConfigurePrimaryHttpMessageHandler(() => new NonDisposingHandler(Discord));

                // First, so it runs before host filtering and before anything that reads the remote address
                services.Insert(0, ServiceDescriptor.Singleton<IStartupFilter>(new ClientAddressFilter()));

                options.Services?.Invoke(services);
            });
        }

        // A browser behind nginx: its own cookie jar, the proxy key, the visitor's address in X-Forwarded-For, and https.
        // remote stands in for whatever connected to Kestrel, which is nginx on 127.0.0.1 unless a test says otherwise.
        public TestBrowser Visitor(string? address = "203.0.113.10", bool proxyKey = true, Uri? baseAddress = null, string? remote = null)
        {
            var jar = new CookieContainer();
            var client = CreateDefaultClient(baseAddress ?? PublicAddress, new CookieContainerHandler(jar), new NginxHeaders(proxyKey ? ProxyKey : null, address, remote));
            client.Timeout = ClientLimit;
            return new TestBrowser(client, jar);
        }

        // The container's own health check: Host localhost, no key, no nginx
        public HttpClient Local()
        {
            var client = CreateDefaultClient(LocalAddress);
            client.Timeout = ClientLimit;
            return client;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing && ownsKeysFolder)
                DeleteFolder(KeysFolder);
        }

        public static void DeleteFolder(string folder)
        {
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // Left for the next build's clean-up; the folder is under the test output
            }
        }
    }

    // One client with its own cookies, as one browser
    internal sealed class TestBrowser(HttpClient client, CookieContainer jar)
    {
        public HttpClient Client => client;
        public CookieContainer Jar => jar;

        public string? Cookie(string name) => jar.GetCookies(DashboardFactory.PublicAddress)[name]?.Value;

        public string? SessionKey => Cookie(DashboardAuth.SessionCookie);
    }
}
