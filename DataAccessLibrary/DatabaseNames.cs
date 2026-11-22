using MySql.Data.MySqlClient;

namespace DataAccessLibrary
{
    // The databases' names, for SQL that has to name a database.
    // Every query on MinerBot's own tables names them with their database, so the same SQL works on the bot's own
    // connection and inside a transaction on the website's connection, where a change to a website table and its audit row
    // commit together. The website's tables stay unqualified, as in the rest of the SQL.
    // The names come from the connection strings rather than the SQL, so another install and the tests can use others.
    // They're quoted here, because a database name can't be a parameter, and only a name that passed this check is ever
    // put into SQL text.
    public sealed class DatabaseNames(IDictionary<DBConnectionName, string> connections)
    {
        // A name of more than 64 characters, or one that ends in a space, is one MariaDB refuses
        const int MaxNameLength = 64;

        string? bot;

        // Worked out on first use, so the bot runs without BotConnection, and only records nothing
        internal string Bot => bot ??= Of(DBConnectionName.Bot);

        internal string Of(DBConnectionName name) => QuoteIdentifier(Unquoted(name));

        // The name as the connection string gives it, for comparing with the names MariaDB reports, such as a login's rights
        // in information_schema. Never for SQL text, which takes only Of's.
        internal string Unquoted(DBConnectionName name)
        {
            var setting = name == DBConnectionName.Bot ? "BotConnection" : "DefaultConnection";
            var database = name == DBConnectionName.Bot ? "MinerBot's own database" : "the website's database";

            if (!connections.TryGetValue(name, out var connectionString) || string.IsNullOrEmpty(connectionString))
                throw new InvalidOperationException($"ConnectionStrings:{setting} isn't set, so {database} can't be used");

            // The builder reads Database, Initial Catalog and the other spellings, in any letter case
            var databaseName = new MySqlConnectionStringBuilder(connectionString).Database;

            if (string.IsNullOrEmpty(databaseName))
                throw new InvalidOperationException($"ConnectionStrings:{setting} names no database, so {database} can't be used");

            return databaseName;
        }

        // What stops a connection string from being used, for the dashboard's start-up check and the bot's status
        // writer: null when nothing does, otherwise the reason in a few words. The reason never quotes the string, which
        // holds a password, and neither does the driver's own message, which is why that isn't passed on.
        public static string? Problem(string? connectionString)
        {
            string databaseName;

            try
            {
                databaseName = new MySqlConnectionStringBuilder(connectionString ?? "").Database;
            }
            // Anything the driver throws as it reads the string, which it only parses: ArgumentException,
            // FormatException, InvalidCastException or OverflowException for a value it can't take, MySqlException for a
            // part it can't split, such as the rest of a password after an unquoted semicolon, which its message quotes,
            // and PlatformNotSupportedException for an option .NET doesn't have. The bot asks as it starts, where an
            // exception would stop it.
            catch (Exception)
            {
                return "can't be read";
            }

            if (string.IsNullOrEmpty(databaseName))
                return "names no database";

            try
            {
                QuoteIdentifier(databaseName);
                return null;
            }
            catch (ArgumentException)
            {
                return "names a database that MariaDB or MySql.Data can't use";
            }
        }

        // In backticks, with any backtick inside doubled, so the name can't end the quoting early. MariaDB allows a
        // backslash in a name, but MySql.Data's own reading of the SQL takes it as an escape inside the backticks, runs the
        // name on past them and then misses every parameter after it, so a name with one is refused too.
        internal static string QuoteIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name.Contains('\0') || name.EndsWith(' '))
                throw new ArgumentException("That isn't a database name MariaDB allows", nameof(name));

            if (name.Contains('\\'))
                throw new ArgumentException("A database name with a backslash can't be used, because MySql.Data would misread the SQL around it", nameof(name));

            return $"`{name.Replace("`", "``")}`";
        }
    }
}
