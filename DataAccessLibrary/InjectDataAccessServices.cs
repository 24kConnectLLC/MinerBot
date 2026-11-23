using Dapper;
using DataAccessLibrary.Armor;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Blacklist;
using DataAccessLibrary.Dashboard;
using DataAccessLibrary.Discord;
using DataAccessLibrary.Feed;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Health;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Status;
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
                { DBConnectionName.Feed, configuration.GetConnectionString("DefaultConnection") },
                // MinerBot's own database, where the bot records its status and history. It may be missing: the bot then
                // records nothing and runs on, and only a call that needs it fails.
                { DBConnectionName.Bot, configuration.GetConnectionString("BotConnection") ?? "" }
            };

            services.AddSingleton<IDictionary<DBConnectionName, string>>(connectionDict);
            services.AddSingleton<DatabaseNames>();

            SqlMapper.Settings.CommandTimeout = 60;

            services.AddSingleton<ISQLConnectionFactory, SQLConnectionFactory>();

            services.AddMemoryCache();

            return services;
        }

        // With the reset of the cached armor names, which the bot's cached maps share, so one request clears both
        public static IServiceCollection AddArmorAccess(this IServiceCollection services)
        {
            services.AddTransient<IArmorData, ArmorData>();
            services.AddSingleton<CacheReset>();
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

        public static IServiceCollection AddDatabaseHealthAccess(this IServiceCollection services)
        {
            services.AddTransient<IDatabaseHealthData, DatabaseHealthData>();
            return services;
        }

        public static IServiceCollection AddBotStatusAccess(this IServiceCollection services)
        {
            services.AddTransient<IBotStatusData, BotStatusData>();
            return services;
        }

        public static IServiceCollection AddRetentionAccess(this IServiceCollection services)
        {
            services.AddTransient<IRetentionData, RetentionData>();
            return services;
        }

        // MinerBot's own copy of the servers the bot is in, apart from the website's ihvph_discord_servers in AddGuildAccess
        public static IServiceCollection AddBotGuildAccess(this IServiceCollection services)
        {
            services.AddTransient<IBotGuildData, BotGuildData>();
            return services;
        }

        // The dashboard's reads of the bot's status and history, without the bot's writes
        public static IServiceCollection AddBotStatusReads(this IServiceCollection services)
        {
            services.AddTransient<IBotStatusReads, BotStatusReads>();
            return services;
        }

        // The dashboard's reads of the bot's copy of its servers, without the bot's writes
        public static IServiceCollection AddBotGuildReads(this IServiceCollection services)
        {
            services.AddTransient<IBotGuildReads, BotGuildReads>();
            return services;
        }

        // The dashboard's reads of the website's channel settings, without GuildData's saves and deletes
        public static IServiceCollection AddGuildSettingsReads(this IServiceCollection services)
        {
            services.AddTransient<IGuildSettingsReads, GuildSettingsReads>();
            return services;
        }

        // The bot's side of the dashboard's requests: reading, deciding, taking and finishing them, and its settings
        public static IServiceCollection AddBotRequestAccess(this IServiceCollection services)
        {
            services.AddTransient<IBotRequestData, BotRequestData>();
            return services;
        }

        // The open lobbies and the latest maps the bot writes for the website's pages, in the website's database
        public static IServiceCollection AddWebsiteFeedAccess(this IServiceCollection services)
        {
            services.AddTransient<IWebsiteFeedData, WebsiteFeedData>();
            return services;
        }

        // The dashboard's side of its requests to the bot: adding one with its audit row, and reading them back, without
        // the bot's writes. Its pages' reads come from the same class, on an interface of their own.
        public static IServiceCollection AddBotRequestQueue(this IServiceCollection services)
        {
            services.AddTransient<IBotRequestQueue, BotRequestQueue>();
            services.AddTransient<IBotRequestReads, BotRequestQueue>();
            return services;
        }

        // The Sessions and security page's reads of the dashboard's own database login and of the migrations MinerBot's
        // database has run
        public static IServiceCollection AddDashboardChecks(this IServiceCollection services)
        {
            services.AddTransient<IDashboardChecks, DashboardChecks>();
            return services;
        }
    }
}
