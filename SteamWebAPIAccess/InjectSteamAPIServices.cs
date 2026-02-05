using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
