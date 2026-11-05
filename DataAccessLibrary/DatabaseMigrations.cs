using DataAccessLibrary.Migrations;
using Microsoft.Extensions.Logging;

namespace DataAccessLibrary
{
    // Version is the newest migration the database has after the run, and Applied names the ones this run applied
    public sealed record MigrationResult(int Version, IReadOnlyList<string> Applied);

    // Brings MinerBot's own database up to the newest migration in this build, under a time limit of its own. It needs a
    // login that can create tables there, and it logs what it applied, without the host, database or login.
    public static class DatabaseMigrations
    {
        public static Task<MigrationResult> MigrateAsync(string connectionString, ILogger logger, CancellationToken cancellationToken = default) =>
            MigrationRunner.RunAsync(connectionString, logger, MigrationScript.Embedded(), new MigrationOptions(), cancellationToken);
    }
}
