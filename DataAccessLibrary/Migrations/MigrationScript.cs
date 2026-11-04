using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DataAccessLibrary.Migrations
{
    // One file from the Migrations folder. Name is the file's name without .sql, such as 0001_core.
    internal sealed record Migration(int Version, string Name, string Sql)
    {
        public string Checksum => MigrationScript.Checksum(Sql);

        public IReadOnlyList<string> Statements => MigrationScript.Split(Sql);
    }

    internal static class MigrationScript
    {
        const string Folder = "Migrations/";

        static readonly Regex ResourceName = new(@"^Migrations/([0-9]{4})_[a-z0-9_]+\.sql\z", RegexOptions.CultureInvariant);

        // The files built into DataAccessLibrary, in the order they run
        public static IReadOnlyList<Migration> Embedded()
        {
            var assembly = typeof(MigrationScript).Assembly;

            return Load(assembly.GetManifestResourceNames(), resource =>
            {
                using var stream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream, Encoding.UTF8);

                return reader.ReadToEnd();
            });
        }

        // The project builds in every file of the Migrations folder but its code, so a file named the wrong way, such as
        // 0002_Bot_Status.sql or 0002-bot.SQL, stops every run, and the tests, instead of shipping and never running
        internal static IReadOnlyList<Migration> Load(IEnumerable<string> resources, Func<string, string> read)
        {
            var migrations = new List<Migration>();

            foreach (var resource in resources)
            {
                if (!resource.StartsWith(Folder, StringComparison.Ordinal))
                    continue;

                var match = ResourceName.Match(resource);

                if (!match.Success)
                    throw new InvalidOperationException($"{resource} is in the migrations folder but isn't named like a migration: four digits, an underscore, then small letters, digits or underscores, and .sql, such as 0002_bot_status.sql");

                var version = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                migrations.Add(new Migration(version, resource[Folder.Length..^".sql".Length], read(resource)));
            }

            migrations.Sort((first, second) => first.Version.CompareTo(second.Version));

            for (var i = 1; i < migrations.Count; i++)
            {
                if (migrations[i].Version == migrations[i - 1].Version)
                    throw new InvalidOperationException($"Migrations {migrations[i - 1].Name} and {migrations[i].Name} have the same version");
            }

            return migrations;
        }

        // Every migration file follows these rules, which keep it simple to split without parsing SQL, and safe to run again:
        // - a semicolon at the end of a line ends a statement, and nothing else does
        // - a comment sits on a line of its own, starting with --
        // - there's no DELIMITER, procedure or trigger
        // - every statement can run twice: CREATE TABLE, CREATE INDEX and ADD COLUMN with IF NOT EXISTS, and INSERT IGNORE
        // - times are DATETIME(3) in UTC, never TIMESTAMP, which ends in 2038
        // - there's no CHAR(36), which MySql.Data reads as a Guid
        // - once a file has run anywhere it never changes, not even a comment, because that database refuses a new checksum,
        //   so a change goes in a new file. MigrationScriptTests holds every file's checksum, so an edit fails the build first.
        // - a file that stopped partway has no checksum recorded and can be fixed, but only in the statement that stopped and
        //   the ones after it, which the runner's log names: MariaDB keeps what the first ones made, and their IF NOT EXISTS
        //   would skip any change to them
        public static IReadOnlyList<string> Split(string sql)
        {
            var statements = new List<string>();
            var statement = new StringBuilder();

            foreach (var line in Normalize(sql).Split('\n'))
            {
                var trimmed = line.Trim();

                if (trimmed.Length == 0 || trimmed.StartsWith("--", StringComparison.Ordinal))
                    continue;

                var text = line.TrimEnd();

                if (!text.EndsWith(';'))
                {
                    statement.Append(text).Append('\n');
                    continue;
                }

                statement.Append(text, 0, text.Length - 1);

                var complete = statement.ToString().Trim();
                statement.Clear();

                if (complete.Length == 0)
                    throw new FormatException("The SQL has a semicolon with no statement before it");

                statements.Add(complete);
            }

            if (statement.Length > 0)
                throw new FormatException("The SQL ends without a semicolon after its last statement");

            if (statements.Count == 0)
                throw new FormatException("The SQL has no statements, and so ends without a semicolon");

            return statements;
        }

        // Taken over the text, not the bytes, with line ends and a byte order mark evened out, so a Windows checkout and
        // the Linux image agree
        public static string Checksum(string sql) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(sql))));

        static string Normalize(string sql) => (sql.StartsWith('\uFEFF') ? sql[1..] : sql).Replace("\r\n", "\n");
    }
}
