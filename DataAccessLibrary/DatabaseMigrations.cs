using DataAccessLibrary.Migrations;
using Microsoft.Extensions.Logging;

namespace DataAccessLibrary
{
    // Version is the newest migration the database has after the run, and Applied names the ones this run applied
    public sealed record MigrationResult(int Version, IReadOnlyList<string> Applied);

    // A migration by its number, its file's name without .sql, such as 0001_core, and the checksum of its text: as this
    // build carries it, and as schema_version records one that ran
    public sealed record MigrationInfo(int Version, string Name, string Checksum);

    // Brings MinerBot's own database up to the newest migration in this build, under a time limit of its own. It needs a
    // login that can create tables there, and it logs what it applied, without the host, database or login.
    public static class DatabaseMigrations
    {
        public static Task<MigrationResult> MigrateAsync(string connectionString, ILogger logger, CancellationToken cancellationToken = default) =>
            MigrationRunner.RunAsync(connectionString, logger, MigrationScript.Embedded(), new MigrationOptions(), cancellationToken);

        // The migrations built into this version, in the order they run, for comparing with what a database has run
        public static IReadOnlyList<MigrationInfo> Expected() =>
            [.. MigrationScript.Embedded().Select(migration => new MigrationInfo(migration.Version, migration.Name, migration.Checksum))];
    }
}
