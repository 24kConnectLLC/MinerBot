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
    }
}
