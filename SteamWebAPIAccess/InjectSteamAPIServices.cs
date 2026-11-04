using Microsoft.Extensions.DependencyInjection;

namespace SteamWebAPIAccess
{
    public static class InjectSteamAPIServices
    {
        public static void AddSteamAPIServices(this IServiceCollection services)
        {
            services.AddSingleton<ISteamAPIAccess, SteamAPIAccess>();
            services.AddSingleton<IGetData, GetData>();
        }

        // The one HttpClient of the bot, on the handler that every web call shares
        public static IServiceCollection AddWebClient(this IServiceCollection services)
        {
            services.AddSingleton<WebRetryHandler>();
            services.AddSingleton(provider =>
            {
                var handler = provider.GetRequiredService<WebRetryHandler>();
                return new HttpClient(handler, disposeHandler: false) { Timeout = handler.ClientTimeout };
            });

            return services;
        }
    }
}
