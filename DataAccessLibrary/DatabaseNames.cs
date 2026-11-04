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

        // Worked out on first use, so the bot, which never uses it, runs without BotConnection
        internal string Bot => bot ??= Of(DBConnectionName.Bot);

        internal string Of(DBConnectionName name)
        {
            var setting = name == DBConnectionName.Bot ? "BotConnection" : "DefaultConnection";
            var database = name == DBConnectionName.Bot ? "MinerBot's own database" : "the website's database";

            if (!connections.TryGetValue(name, out var connectionString) || string.IsNullOrEmpty(connectionString))
                throw new InvalidOperationException($"ConnectionStrings:{setting} isn't set, so {database} can't be used");

            // The builder reads Database, Initial Catalog and the other spellings, in any letter case
            var databaseName = new MySqlConnectionStringBuilder(connectionString).Database;

            if (string.IsNullOrEmpty(databaseName))
                throw new InvalidOperationException($"ConnectionStrings:{setting} names no database, so {database} can't be used");

            return QuoteIdentifier(databaseName);
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
