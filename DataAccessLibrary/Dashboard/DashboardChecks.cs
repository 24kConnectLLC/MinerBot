using DataAccessLibrary.Dashboard.Models;
using System.Globalization;

namespace DataAccessLibrary.Dashboard
{
    // The Sessions and security page's reads of the dashboard's own database login and of MinerBot's database, on MinerBot's
    // own connection, so as that login. Its rights come from information_schema, which shows any login its own, in
    // deploy/rights-list.sql's lines, and its limits from its session, never from the statement that lists a login's
    // grants, which prints the password's hash. The migrations come from schema_version, which the login may read since
    // the Sessions page came.
    public class DashboardChecks : DbBaseConnection, IDashboardChecks
    {
        // rights-list.sql's first four statements, which read only information_schema, for the account given. Its others
        // read MariaDB's own tables, which this login can't, and it holds no routine, proxy or role there.
        const string Privileges = @"SELECT CONCAT_WS(' ', 'global', PRIVILEGE_TYPE, IF(IS_GRANTABLE = 'YES', 'grantable', NULL)) FROM information_schema.USER_PRIVILEGES WHERE GRANTEE = @grantee
            UNION ALL SELECT CONCAT_WS(' ', 'schema', PRIVILEGE_TYPE, TABLE_SCHEMA, IF(IS_GRANTABLE = 'YES', 'grantable', NULL)) FROM information_schema.SCHEMA_PRIVILEGES WHERE GRANTEE = @grantee
            UNION ALL SELECT CONCAT_WS(' ', 'table', PRIVILEGE_TYPE, TABLE_SCHEMA, TABLE_NAME, IF(IS_GRANTABLE = 'YES', 'grantable', NULL)) FROM information_schema.TABLE_PRIVILEGES WHERE GRANTEE = @grantee
            UNION ALL SELECT CONCAT_WS(' ', 'column', PRIVILEGE_TYPE, TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, IF(IS_GRANTABLE = 'YES', 'grantable', NULL)) FROM information_schema.COLUMN_PRIVILEGES WHERE GRANTEE = @grantee";

        // The account's limits, as its session has them. From the view rather than as @@ variables, so MySql.Data meets no
        // @ but a parameter's.
        const string Limits = @"SELECT VARIABLE_NAME, VARIABLE_VALUE
                                FROM information_schema.SESSION_VARIABLES
                                WHERE VARIABLE_NAME IN ('MAX_USER_CONNECTIONS', 'MAX_STATEMENT_TIME')";

        private readonly DatabaseNames _names;

        public DashboardChecks(ISQLConnectionFactory connectionFactory, DatabaseNames names) : base(connectionFactory, DBConnectionName.Bot)
        {
            _names = names;
        }

        // The login's rights and limits, and what this version expects of them with this install's database names
        public async Task<OwnRights> ListOwnRights()
        {
            // The names first, so a missing connection string fails with its reason before any connection is tried. The
            // website's database is the one Armor, Blacklist and Guild all use.
            var bot = _names.Unquoted(DBConnectionName.Bot);
            var expected = DashboardRights.For(_names.Unquoted(DBConnectionName.Armor), bot);

            var account = await LoadFirstData<string?, object?>("SELECT CURRENT_USER()", null).ConfigureAwait(false);
            var lines = await LoadDataEnum<string, dynamic>(Privileges, new { grantee = Grantee(account) }).ConfigureAwait(false);
            var limits = (await LoadDataEnum<(string Name, string Value), object?>(Limits, null).ConfigureAwait(false))
                .ToDictionary(limit => limit.Name, limit => limit.Value, StringComparer.OrdinalIgnoreCase);

            return new OwnRights([.. lines.Order(StringComparer.Ordinal)], expected,
                limits.TryGetValue("MAX_USER_CONNECTIONS", out var connections) && long.TryParse(connections, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                    ? count : null,
                limits.TryGetValue("MAX_STATEMENT_TIME", out var time) && decimal.TryParse(time, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
                    ? seconds : null);
        }

        // The migrations MinerBot's database has run, in order, as the migration runner records them
        public async Task<IReadOnlyList<MigrationInfo>> ListMigrations()
        {
            string sql = $@"SELECT `version`, `name`, `checksum`
                            FROM {_names.Bot}.schema_version
                            ORDER BY `version`";

            var rows = await LoadDataEnum<MigrationInfo, object?>(sql, null).ConfigureAwait(false);
            return rows.ToList();
        }

        // CURRENT_USER() names the account the login matched, such as minerbot-dashboard@127.0.0.1, and information_schema
        // quotes each part, 'minerbot-dashboard'@'127.0.0.1'. A host never holds an @, so the last one splits them.
        internal static string Grantee(string? account)
        {
            var at = account?.LastIndexOf('@') ?? -1;

            if (at < 1)
                throw new InvalidOperationException("MariaDB didn't name the login's account as a user and a host");

            return $"'{account![..at]}'@'{account[(at + 1)..]}'";
        }
    }
}
