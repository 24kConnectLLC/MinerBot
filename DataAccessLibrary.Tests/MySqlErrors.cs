using System.Reflection;
using MySql.Data.MySqlClient;

namespace DataAccessLibrary.Tests
{
    // Errors as MySql.Data reports them, for tests that need one without making the server produce it
    internal static class MySqlErrors
    {
        public static MySqlException Deadlock() => Error((int)MySqlErrorCode.LockDeadlock, "Deadlock found when trying to get lock");

        // Any of MariaDB's errors, by its number, with a message such as a refusal's, which names the login and its host.
        // MySqlException is sealed and only has internal constructors.
        public static MySqlException Error(int number, string message) =>
            (MySqlException)typeof(MySqlException)
                .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string), typeof(int), typeof(Exception)])!
                .Invoke([message, number, null]);
    }
}
