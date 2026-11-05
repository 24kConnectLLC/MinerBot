using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Settings;

namespace MinerBot.Dashboard.Hosting
{
    // Kestrel, the proxy in front of it, Data Protection and the rate limiter, each set from the checked settings
    internal static class DashboardHosting
    {
        // Data Protection keys made under this name open only for it, wherever the app's files are
        public const string ApplicationName = "MinerBot.Dashboard";

        // The only form is sign-out. nginx caps bodies at 256 KB, four times this, so a body in between gets this app's own
        // page. A later page that needs more can raise it.
        public const long MaxRequestBodySize = 64 * 1024;

        public static IServiceCollection AddDashboardHosting(this IServiceCollection services)
        {
            // Kestrel is made as the app is built, before the settings are checked, so it reads its address itself. One that
            // doesn't parse falls back to the default here, and never binds: the check right after the build stops the app.
            services.AddOptions<KestrelServerOptions>().Configure<IConfiguration>((kestrel, configuration) =>
                ConfigureKestrel(kestrel, DashboardSettings.ListenEndpoint(configuration) ?? new IPEndPoint(IPAddress.Loopback, DashboardOptions.DefaultPort)));
            services.Configure<ForwardedHeadersOptions>(ConfigureForwardedHeaders);
            services.AddOptions<HostFilteringOptions>().Configure<IOptions<DashboardOptions>>((filtering, settings) => ConfigureHostFiltering(filtering, settings.Value));

            services.AddDataProtection().SetApplicationName(ApplicationName);

            // The folder is read through options once they're built and checked. PersistKeysToFileSystem would need it now.
            services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(provider => new ConfigureOptions<KeyManagementOptions>(options =>
                options.XmlRepository = new FileSystemXmlRepository(
                    new DirectoryInfo(provider.GetRequiredService<IOptions<DashboardOptions>>().Value.KeysFolder),
                    provider.GetRequiredService<ILoggerFactory>())));

            services.AddRateLimiter(RateLimits.Configure);
            return services;
        }

        internal static void ConfigureKestrel(KestrelServerOptions kestrel, IPEndPoint endpoint)
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = MaxRequestBodySize;

            // ASP.NET Core also binds any endpoint in the Kestrel section of the configuration, on any address, beside the
            // ones set here. An empty configuration in its place drops them, so the dashboard listens only where its own
            // settings say, whatever the environment holds.
            kestrel.Configure();

            // An endpoint set in code wins over ASPNETCORE_HTTP_PORTS and ASPNETCORE_URLS, so the aspnet image's 8080 can
            // never bind every address. nginx talks HTTP/1.1, so there's no reason to offer h2c.
            kestrel.Listen(endpoint, listen => listen.Protocols = HttpProtocols.Http1);
        }

        internal static void ConfigureForwardedHeaders(ForwardedHeadersOptions options)
        {
            // The visitor's address and https, from nginx alone, one hop. X-Forwarded-Host is never trusted, because
            // Discord's redirect address is built from the host.
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;

            // The defaults, set again on purpose: ASPNETCORE_FORWARDEDHEADERS_ENABLED=true clears them, which trusts any
            // address, and this runs after that and puts the loopback limit back
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("127.0.0.0/8"));
            options.KnownProxies.Add(IPAddress.IPv6Loopback);
        }

        internal static void ConfigureHostFiltering(HostFilteringOptions options, DashboardOptions settings)
        {
            // Set in code, so AllowedHosts=* in the environment can't widen it
            options.AllowedHosts = [new Uri(settings.PublicOrigin).Host, "localhost"];
            options.AllowEmptyHosts = false;
            options.IncludeFailureMessage = false;
        }

        // Whether the settings stop the dashboard, having said why: one plain line for each problem
        public static bool StoppedBySettings(IServiceProvider services, TextWriter errors)
        {
            var problems = SettingsProblems(services);

            if (problems.Count == 0)
                return false;

            errors.WriteLine("The dashboard didn't start, because of its settings:");

            foreach (var problem in problems)
                errors.WriteLine("- " + problem);

            return true;
        }

        // The host's own start-up check of every section with ValidateOnStart: one OptionsValidationException for one
        // section, or an AggregateException of them when both fail
        internal static IReadOnlyList<string> SettingsProblems(IServiceProvider services)
        {
            try
            {
                services.GetService<IStartupValidator>()?.Validate();
                return [];
            }
            catch (OptionsValidationException e)
            {
                return e.Failures.ToList();
            }
            catch (AggregateException e) when (e.InnerExceptions.All(inner => inner is OptionsValidationException))
            {
                return e.InnerExceptions.Cast<OptionsValidationException>().SelectMany(inner => inner.Failures).ToList();
            }
        }
    }
}
