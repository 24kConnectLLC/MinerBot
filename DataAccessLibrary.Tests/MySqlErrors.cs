using System.Reflection;
using MySql.Data.MySqlClient;

namespace DataAccessLibrary.Tests
{
    // Errors as MySql.Data reports them, for tests that need one without making the server produce it
    internal static class MySqlErrors
    {
        // MySqlException is sealed and only has internal constructors
        public static MySqlException Deadlock() =>
            (MySqlException)typeof(MySqlException)
                .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string), typeof(int), typeof(Exception)])!
                .Invoke(["Deadlock found when trying to get lock", (int)MySqlErrorCode.LockDeadlock, null]);
    }
}
