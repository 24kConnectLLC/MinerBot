using System.Security.Cryptography;
using DataAccessLibrary.Migrations;
using MySql.Data.MySqlClient;

namespace DataAccessLibrary.Tests.Database
{
    // The web dashboard's database login, as setup-dashboard.sh makes it with no rights and deploy.sh then gives it
    // deploy/dashboard-rights.sql, applied as root statement by statement. Here, beside the fixture, so the dashboard's tests
    // link it as they link the fixture, for a page that has to run as that login. Each test that takes it gets it fresh, with
    // a password of its own, and it's dropped afterwards with every right it had, because the other database tests share
    // these databases. Each test project copies the rights file into its output's deploy folder.
    internal static class DashboardLogin
    {
        public const string Login = "minerbot-dashboard";

        // % rather than the server's localhost and 127.0.0.1, because the tests reach MariaDB through a mapped port
        public const string Grantee = "'" + Login + "'@'%'";

        public static string DeployFile(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "deploy", name));

        // With this run's names, as deploy.sh renders it
        public static string Rendered(string sql) => sql
            .Replace("{{SITE_DB}}", MariaDbFixture.SiteDatabase)
            .Replace("{{BOT_DB}}", MariaDbFixture.BotDatabase)
            .Replace("{{GRANTEES}}", Grantee);

        // The login made, then the file applied. Dropped afterwards whatever happened, which takes every right it had with it.
        public static async Task AsDashboardAsync(MariaDbFixture db, Func<string, Task> test)
        {
            var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

            await db.RootExecuteAsync($"DROP USER IF EXISTS {Grantee}");
            await db.RootExecuteAsync($"CREATE USER {Grantee} IDENTIFIED BY '{password}'");

            try
            {
                await ApplyAsync(db);
                await test(password);
            }
            finally
            {
                await db.RootExecuteAsync($"DROP USER IF EXISTS {Grantee}");
            }
        }

        public static async Task ApplyAsync(MariaDbFixture db)
        {
            foreach (var statement in MigrationScript.Split(Rendered(DeployFile("dashboard-rights.sql"))))
                await db.RootExecuteAsync(statement);
        }

        // The login's own connection, never pooled, so nothing of it stays open once the login is dropped. Its user
        // variables are allowed, so MySql.Data leaves an @ in a statement alone.
        public static string ConnectionAs(MariaDbFixture db, string password, string database) =>
            new MySqlConnectionStringBuilder(db.ConnectionAs(Login, password, database)) { Pooling = false, AllowUserVariables = true }.ConnectionString;
    }
}
