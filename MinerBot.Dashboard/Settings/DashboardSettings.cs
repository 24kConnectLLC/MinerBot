using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MinerBot.Dashboard.Settings
{
    internal static class DashboardSettings
    {
        // Settings are only ever read through options, after they've been checked, never here. The one exception is the
        // connection strings, which DataAccessLibrary's AddDataAccessServices reads the moment it's called.
        public static IServiceCollection AddDashboardSettings(this IServiceCollection services)
        {
            services.AddOptions<DashboardOptions>().BindConfiguration(DashboardOptions.Section).ValidateOnStart();
            services.AddOptions<DiscordSignInOptions>().BindConfiguration(DiscordSignInOptions.Section).ValidateOnStart();
            services.AddOptions<CloudflareAccessOptions>().BindConfiguration(CloudflareAccessOptions.Section).ValidateOnStart();
            services.PostConfigure<DashboardOptions>(DashboardOptionsValidator.Normalize);

            services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DashboardOptions>, DashboardOptionsValidator>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DiscordSignInOptions>, DiscordSignInOptionsValidator>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CloudflareAccessOptions>, CloudflareAccessOptionsValidator>());

            services.TryAddSingleton(TimeProvider.System);
            return services;
        }

        // Where the dashboard listens, read straight from the configuration rather than through the checked options:
        // Kestrel is made as the app is built, before the check, and the healthcheck command has no options at all. Null
        // when either setting is there but doesn't parse. The check refuses those, so a wrong one never binds.
        internal static IPEndPoint? ListenEndpoint(IConfiguration configuration)
        {
            var section = configuration.GetSection(DashboardOptions.Section);
            var address = section[nameof(DashboardOptions.ListenAddress)];
            var endpoint = new IPEndPoint(IPAddress.Parse(DashboardOptions.DefaultListenAddress), DashboardOptions.DefaultPort);

            if (!string.IsNullOrWhiteSpace(address))
            {
                if (!IPAddress.TryParse(address, out var ip))
                    return null;

                endpoint.Address = ip;
            }

            if (ListenPort(section[nameof(DashboardOptions.Port)]) is not { } port)
                return null;

            endpoint.Port = port;
            return endpoint;
        }

        // Dashboard:Port as Kestrel, the healthcheck command and the check all read it: the default when it's empty,
        // otherwise a whole number from 1 to 65535. Null when it's neither.
        internal static int? ListenPort(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return DashboardOptions.DefaultPort;

            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535 ? port : null;
        }

        // A socket's address holds 108 bytes on Linux and 104 on some other systems, with a zero at the end, so this fits
        // everywhere
        internal const int MaxUnixSocketBytes = 100;

        // Dashboard:UnixSocket, read straight from the configuration for the same reasons as the address. Null when it's
        // empty, and the dashboard then listens on ListenEndpoint. A path it can't listen on comes back as it is: the check
        // refuses it, so it never binds.
        internal static string? UnixSocket(IConfiguration configuration) =>
            configuration.GetSection(DashboardOptions.Section)[nameof(DashboardOptions.UnixSocket)] is { } path && !string.IsNullOrWhiteSpace(path) ? path : null;

        // The full path of the socket file, with no white space or control characters, in at most MaxUnixSocketBytes
        internal static bool IsUnixSocketPath(string path) =>
            Path.IsPathFullyQualified(path)
            && !Path.EndsInDirectorySeparator(path)
            && !path.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            && Encoding.UTF8.GetByteCount(path) <= MaxUnixSocketBytes;
    }
}
