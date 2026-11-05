using Dapper;
using DataAccessLibrary.Armor;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Blacklist;
using DataAccessLibrary.Dashboard;
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
                { DBConnectionName.Guild, configuration.GetConnectionString("DefaultConnection") },
                // MinerBot's own database. Nothing in the bot reads it yet, so it may be missing, and only a call that needs it fails.
                { DBConnectionName.Bot, configuration.GetConnectionString("BotConnection") ?? "" }
            };

            services.AddSingleton<IDictionary<DBConnectionName, string>>(connectionDict);
            services.AddSingleton<DatabaseNames>();

            SqlMapper.Settings.CommandTimeout = 60;

            services.AddSingleton<ISQLConnectionFactory, SQLConnectionFactory>();

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

        public static IServiceCollection AddArmorSubmissionAccess(this IServiceCollection services)
        {
            services.AddTransient<IArmorSubmissionData, ArmorSubmissionData>();
            return services;
        }

        public static IServiceCollection AddArmorReviewAccess(this IServiceCollection services)
        {
            services.AddTransient<IArmorReviewData, ArmorReviewData>();
            return services;
        }

        public static IServiceCollection AddAuditLogAccess(this IServiceCollection services)
        {
            services.AddTransient<IAuditLogData, AuditLogData>();
            return services;
        }

        public static IServiceCollection AddDashboardSessionAccess(this IServiceCollection services)
        {
            services.AddTransient<IDashboardSessionData, DashboardSessionData>();
            return services;
        }
    }
}
