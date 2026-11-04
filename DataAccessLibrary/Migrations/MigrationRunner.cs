using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DataAccessLibrary.Migrations
{
    // Shorter in tests
    internal sealed class MigrationOptions
    {
        // A run's own limit, in place of the 15 seconds of an ordinary call, because building tables can take longer
        public TimeSpan RunLimit { get; init; } = TimeSpan.FromMinutes(2);

        // How long a run waits for another one to end
        public TimeSpan LockWait { get; init; } = TimeSpan.FromSeconds(30);

        // How long one statement may wait for a table or a row that another session holds, after which the server ends it
        public TimeSpan StatementLimit { get; init; } = TimeSpan.FromSeconds(60);
    }

    // Applies the migrations a database doesn't have yet, in order, and records each one in schema_version once all of its
    // statements went through. MariaDB commits a CREATE TABLE as it goes, so a file that fails halfway stays unrecorded
    // and runs again from the top next time, which its IF NOT EXISTS statements allow.
    internal static class MigrationRunner
    {
        // GET_LOCK's names are shared by the whole server and hold at most 64 characters
        const int MaxLockName = 64;
        const string LockPrefix = "minerbot_migrations.";

        // The server ends its own waits, so each command has that long and this much more. MySql.Data's async calls don't
        // stop at CommandTimeout, but an answer that arrives after it counts as a timeout, and MySql.Data then tries to kill
        // a statement that has already ended and waits for an answer that never comes.
        static readonly TimeSpan ServerMargin = TimeSpan.FromSeconds(15);

        const string SchemaVersionTable = """
            CREATE TABLE IF NOT EXISTS schema_version (
              `version` INT NOT NULL,
              `name` VARCHAR(100) NOT NULL,
              `checksum` CHAR(64) NOT NULL,
              applied_at DATETIME(3) NOT NULL,
              PRIMARY KEY (`version`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci
            """;

        public static async Task<MigrationResult> RunAsync(string connectionString, ILogger logger, IReadOnlyList<Migration> migrations, MigrationOptions options, CancellationToken cancellationToken)
        {
            var settings = new MySqlConnectionStringBuilder(connectionString);

            if (string.IsNullOrEmpty(settings.Database))
                throw new ArgumentException("The connection string names no database to migrate", nameof(connectionString));

            // The lock belongs to the server's session, and a pooled connection keeps its session when it's closed, so a
            // lock left on one would hold up later runs in this process. An unpooled connection's session ends with it.
            settings.Pooling = false;

            // Cancelled when the caller stops waiting, so the run stops before its next statement. It isn't disposed,
            // because a run that was given up on may still read it.
            var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var running = Task.Run(() => Run(settings.ConnectionString, settings.Database, logger, migrations, options, stop.Token), CancellationToken.None);

            try
            {
                return await running.WaitAsync(options.RunLimit, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException e) when (!running.IsCompleted)
            {
                // Not awaited, so a callback that stops a statement on the server can't hold the caller
                _ = stop.CancelAsync();
                throw new TimeoutException($"The migrations didn't finish within {options.RunLimit.TotalSeconds:0.#} seconds", e);
            }
        }

        // One lock for each database, shortened to a hash of the database's name when it would be too long
        internal static string LockName(string database)
        {
            var name = LockPrefix + database;

            if (name.Length <= MaxLockName)
                return name;

            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(database)));
            return LockPrefix + hash[..(MaxLockName - LockPrefix.Length)];
        }

        // One connection for the whole run, because the lock belongs to it
        static async Task<MigrationResult> Run(string connectionString, string database, ILogger logger, IReadOnlyList<Migration> migrations, MigrationOptions options, CancellationToken token)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(token).ConfigureAwait(false);

            // MySql.Data doesn't end an async statement at its CommandTimeout, and giving up on the run doesn't stop one either,
            // so the server has to. An ALTER behind another session's open transaction would otherwise wait for a day, the
            // server's default, with every new query on that table queued behind it.
            await Execute(connection, "SET SESSION lock_wait_timeout = @seconds, SESSION innodb_lock_wait_timeout = @seconds",
                [("@seconds", Seconds(options.StatementLimit))], options, token).ConfigureAwait(false);

            await TakeLock(connection, database, options, token).ConfigureAwait(false);

            try
            {
                return await Migrate(connection, logger, migrations, options, token).ConfigureAwait(false);
            }
            finally
            {
                await ReleaseLock(connection, database, logger).ConfigureAwait(false);
            }
        }

        static async Task TakeLock(MySqlConnection connection, string database, MigrationOptions options, CancellationToken token)
        {
            // The server waits for the lock itself, so the command has that long and a margin
            await using var command = new MySqlCommand("SELECT GET_LOCK(@name, @wait)", connection)
            {
                CommandTimeout = Seconds(options.LockWait + ServerMargin)
            };

            command.Parameters.AddWithValue("@name", LockName(database));
            command.Parameters.AddWithValue("@wait", options.LockWait.TotalSeconds);

            var locked = await command.ExecuteScalarAsync(token).ConfigureAwait(false);

            // NULL means the server stopped the wait, such as when the session was killed
            if (locked is null or DBNull)
                throw new InvalidOperationException("The migration lock couldn't be taken");

            if (Convert.ToInt64(locked, CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("Another migration run holds the lock; try again when it ends");
        }

        // The lock ends with the session anyway, so a failure here only means it ends when the connection closes
        static async Task ReleaseLock(MySqlConnection connection, string database, ILogger logger)
        {
            try
            {
                await using var command = new MySqlCommand("DO RELEASE_LOCK(@name)", connection);
                command.Parameters.AddWithValue("@name", LockName(database));

                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is MySqlException or InvalidOperationException)
            {
                logger.LogWarning("Couldn't release the migration lock, so it ends when the connection closes");
            }
        }

        static async Task<MigrationResult> Migrate(MySqlConnection connection, ILogger logger, IReadOnlyList<Migration> migrations, MigrationOptions options, CancellationToken token)
        {
            await Execute(connection, SchemaVersionTable, [], options, token).ConfigureAwait(false);

            // Read only now that this run holds the lock, so two runs can't both decide a file is new
            var recorded = await ReadChecksums(connection, token).ConfigureAwait(false);
            var highestRecorded = recorded.Count == 0 ? 0 : recorded.Keys.Max();
            var highestKnown = migrations.Count == 0 ? 0 : migrations.Max(migration => migration.Version);

            // Before anything is applied, so a database that no longer matches its files is left as it is
            foreach (var migration in migrations)
            {
                if (recorded.TryGetValue(migration.Version, out var checksum) && checksum != migration.Checksum)
                    throw new InvalidOperationException($"Migration {migration.Name} was changed after it ran, so the database may not match it. Put the file back as it was, and add a new migration instead");

                if (!recorded.ContainsKey(migration.Version) && migration.Version < highestRecorded)
                    throw new InvalidOperationException($"Migration {migration.Name} never ran, but later ones did, so it can't be applied in order");
            }

            // An older build, such as after a rollback. Migrations only ever add, so it keeps working with the newer tables.
            if (highestRecorded > highestKnown)
                logger.LogWarning("The database is at version {Version}, newer than this build's {Known}, so nothing was applied", highestRecorded, highestKnown);

            var applied = new List<string>();
            var version = highestRecorded;

            foreach (var migration in migrations.Where(migration => migration.Version > highestRecorded))
            {
                var watch = Stopwatch.StartNew();
                var statements = migration.Statements;

                for (var index = 0; index < statements.Count; index++)
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        await Execute(connection, statements[index], [], options, token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        LogStopped(logger, migration, index, statements.Count);
                        throw;
                    }
                }

                token.ThrowIfCancellationRequested();
                await Execute(connection, "INSERT INTO schema_version (`version`, `name`, `checksum`, applied_at) VALUES (@version, @name, @checksum, UTC_TIMESTAMP(3))",
                    [("@version", migration.Version), ("@name", migration.Name), ("@checksum", migration.Checksum)], options, token).ConfigureAwait(false);

                applied.Add(migration.Name);
                version = migration.Version;

                logger.LogInformation("Applied migration {Name} ({Count} statements) in {Ms} ms", migration.Name, statements.Count, watch.ElapsedMilliseconds);
            }

            if (applied.Count == 0 && highestRecorded <= highestKnown)
                logger.LogInformation("The database is up to date at version {Version}", version);

            return new MigrationResult(version, applied);
        }

        // The file stays unrecorded and runs again from the top, but MariaDB keeps what its statements before this one made,
        // and their IF NOT EXISTS would then skip it. So an edit to those statements would never reach the database, and
        // whoever fixes the file needs to know which ran.
        static void LogStopped(ILogger logger, Migration migration, int index, int count)
        {
            if (index == 0)
                logger.LogError("Migration {Name} stopped at its first statement, so nothing in it had run", migration.Name);
            else
                logger.LogError("Migration {Name} stopped at statement {Number} of {Count}, after the {Done} before it had run. MariaDB keeps what those made, so if the file changes before it runs again, change only statement {From} and later ones",
                    migration.Name, index + 1, count, index, index + 1);
        }

        static async Task<Dictionary<int, string>> ReadChecksums(MySqlConnection connection, CancellationToken token)
        {
            var checksums = new Dictionary<int, string>();

            await using var command = new MySqlCommand("SELECT `version`, `checksum` FROM schema_version", connection);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

            while (await reader.ReadAsync(token).ConfigureAwait(false))
                checksums[reader.GetInt32(0)] = reader.GetString(1);

            return checksums;
        }

        static async Task Execute(MySqlConnection connection, string sql, (string Name, object Value)[] parameters, MigrationOptions options, CancellationToken token)
        {
            // The server ends a wait for a lock at the statement limit, so the command has a margin over it
            await using var command = new MySqlCommand(sql, connection) { CommandTimeout = Seconds(options.StatementLimit + ServerMargin) };

            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);

            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        static int Seconds(TimeSpan time) => (int)Math.Ceiling(time.TotalSeconds);
    }
}
