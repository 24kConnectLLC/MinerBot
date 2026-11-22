namespace DataAccessLibrary.Dashboard
{
    // Exactly what the web dashboard's database login may do: the rights deploy/dashboard-rights.sql gives it, as
    // deploy/rights-list.sql lists them, with {{SITE_DB}} and {{BOT_DB}} for the databases' names, and its two limits.
    // One list for both of its readers: DashboardRightsTests compares it with what the real file gives a login, and the
    // Sessions page with what the login reads of its own rights. It's here, in code, because the dashboard's image
    // carries no copy of the rights file. A pull request that adds a right adds its line here too, or the tests fail.
    public static class DashboardRights
    {
        // ALTER USER's limits in the file: at most 15 connections, and 20 seconds a statement
        public const int Connections = 15;
        public const int StatementSeconds = 20;

        public static IReadOnlyList<string> Lines { get; } =
        [
            "column INSERT {{BOT_DB}} bot_commands actor_id",
            "column INSERT {{BOT_DB}} bot_commands actor_name",
            "column INSERT {{BOT_DB}} bot_commands actor_role",
            "column INSERT {{BOT_DB}} bot_commands argument",
            "column INSERT {{BOT_DB}} bot_commands channel_id",
            "column INSERT {{BOT_DB}} bot_commands created_at",
            "column INSERT {{BOT_DB}} bot_commands expires_at",
            "column INSERT {{BOT_DB}} bot_commands guild_id",
            "column INSERT {{BOT_DB}} bot_commands kind",
            "column INSERT {{BOT_DB}} bot_commands request_key",
            "column INSERT {{BOT_DB}} bot_commands signature",
            "column SELECT {{SITE_DB}} ihvph_discord_servers guild_id",
            "column UPDATE {{SITE_DB}} ihvph_armor_styles Status",
            "column UPDATE {{SITE_DB}} ihvph_blacklist expire_date",
            "column UPDATE {{SITE_DB}} ihvph_blacklist notes",
            "column UPDATE {{SITE_DB}} ihvph_blacklist reason",
            "column UPDATE {{SITE_DB}} ihvph_blacklist username",
            "global USAGE",
            "table DELETE {{BOT_DB}} dashboard_sessions",
            "table DELETE {{SITE_DB}} ihvph_blacklist",
            "table INSERT {{BOT_DB}} armor_reviews",
            "table INSERT {{BOT_DB}} audit_log",
            "table INSERT {{BOT_DB}} dashboard_sessions",
            "table INSERT {{SITE_DB}} ihvph_blacklist",
            "table SELECT {{BOT_DB}} armor_reviews",
            "table SELECT {{BOT_DB}} audit_log",
            "table SELECT {{BOT_DB}} bot_commands",
            "table SELECT {{BOT_DB}} bot_events",
            "table SELECT {{BOT_DB}} bot_settings",
            "table SELECT {{BOT_DB}} bot_status",
            "table SELECT {{BOT_DB}} dashboard_sessions",
            "table SELECT {{BOT_DB}} guild_channels",
            "table SELECT {{BOT_DB}} guild_daily",
            "table SELECT {{BOT_DB}} guild_events",
            "table SELECT {{BOT_DB}} guilds",
            "table SELECT {{BOT_DB}} job_runs",
            "table SELECT {{BOT_DB}} job_status",
            "table SELECT {{BOT_DB}} schema_version",
            "table SELECT {{BOT_DB}} uptime_daily",
            "table SELECT {{BOT_DB}} uptime_samples",
            "table SELECT {{SITE_DB}} ihvph_armor_styles",
            "table SELECT {{SITE_DB}} ihvph_blacklist",
            "table SELECT {{SITE_DB}} ihvph_discord_servers",
            "table UPDATE {{BOT_DB}} dashboard_sessions"
        ];

        // With the databases' names as MariaDB reports them, unquoted, sorted as deploy.sh sorts the lines it compares
        public static IReadOnlyList<string> For(string site, string bot)
        {
            ArgumentException.ThrowIfNullOrEmpty(site);
            ArgumentException.ThrowIfNullOrEmpty(bot);

            return [.. Lines.Select(line => line.Replace("{{SITE_DB}}", site).Replace("{{BOT_DB}}", bot)).Order(StringComparer.Ordinal)];
        }
    }
}
