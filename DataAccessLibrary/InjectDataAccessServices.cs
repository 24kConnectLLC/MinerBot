using DataAccessLibrary.Armor;
using DataAccessLibrary.Blacklist;
using DataAccessLibrary.Discord;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DataAccessLibrary
{
    public static class InjectDataAccessServices
    {
        public static IServiceCollection AddDataAccessServices(this IServiceCollection services, IConfiguration configuration)
        {
            var connectionDict = new Dictionary<DBConnectionName, string>
            {
                { DBConnectionName.Armor,  configuration.GetConnectionString("DefaultConnection")},
                { DBConnectionName.Blacklist, configuration.GetConnectionString("DefaultConnection") },
                { DBConnectionName.Guild, configuration.GetConnectionString("DefaultConnection") }
            };

            services.AddSingleton<IDictionary<DBConnectionName, string>>(connectionDict);

            services.AddTransient<ISQLConnectionFactory, SQLConnectionFactory>();

            services.AddMemoryCache();

            return services;
        }

        public static IServiceCollection AddArmorAccess(this IServiceCollection services)
        {
            services.AddTransient<IArmorData, ArmorData>();
            return services;
        }

        public static IServiceCollection AddBlacklistAccess(this IServiceCollection services)
        {
            services.AddTransient<IBlacklistData, BlacklistData>();
            return services;
        }

        public static IServiceCollection AddGuildAccess(this IServiceCollection services)
        {
            services.AddTransient<IGuildData, GuildData>();
            return services;
        }
    }
}
