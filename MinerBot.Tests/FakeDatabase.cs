using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using DataAccessLibrary;
using MySql.Data.MySqlClient;

namespace MinerBot.Tests
{
    // Stands in for MySQL under Dapper's real async calls. Each command takes the next answer of the test:
    // rows, an error, no answer at all, or a thread that blocks before it answers.
    internal sealed class FakeDatabase : ISQLConnectionFactory
    {
        public static readonly IReadOnlyList<string> ArmorNames = ["Armor One", "Armor Two"];

        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

        readonly ConcurrentQueue<Func<Task>> answers = new();
        readonly TaskCompletionSource firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls;

        public int Calls => Volatile.Read(ref calls);

        // Done once the first command arrives. A call that was cut off can fail before its command gets this far.
        public Task FirstCall => firstCall.Task;

        // A blocked command waits here until the test sets it
        public ManualResetEventSlim Gate { get; } = new();

        public FakeDatabase Rows() => Add(() => Task.CompletedTask);

        public FakeDatabase Error(Exception error) => Add(() => Task.FromException(error));

        // MySql.Data's calls don't end on a silent server, whatever their token says
        public FakeDatabase Never() => Add(() => new TaskCompletionSource().Task);

        public FakeDatabase Block() => Add(() =>
        {
            Gate.Wait(WaitLimit);
            return Task.CompletedTask;
        });

        FakeDatabase Add(Func<Task> answer)
        {
            answers.Enqueue(answer);
            return this;
        }

        public IDbConnection Create(DBConnectionName connectionName) => new FakeConnection(this);

        // MySqlException is sealed and only has internal constructors
        public static MySqlException MySqlError(int number, Exception? inner = null) =>
            (MySqlException)typeof(MySqlException)
                .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string), typeof(int), typeof(Exception)])!
                .Invoke([$"MySQL error {number}", number, inner]);

        async Task<T> AnswerAsync<T>(Func<T> result)
        {
            Interlocked.Increment(ref calls);
            firstCall.TrySetResult();

            if (!answers.TryDequeue(out var answer))
                throw new InvalidOperationException("The test has no answer left for this call");

            await answer();
            return result();
        }

        static DataTableReader ArmorNameRows()
        {
            var table = new DataTable();
            table.Columns.Add("ArmorName", typeof(string));

            foreach (var name in ArmorNames)
                table.Rows.Add(name);

            return table.CreateDataReader();
        }

        // Dapper's async calls need a DbConnection whose commands are DbCommands
        sealed class FakeConnection(FakeDatabase database) : DbConnection
        {
            ConnectionState state;

            [AllowNull]
            public override string ConnectionString { get; set; } = "fake";
            public override string Database => "fake";
            public override string DataSource => "fake";
            public override string ServerVersion => "fake";
            public override ConnectionState State => state;

            public override void Open() => state = ConnectionState.Open;
            public override void Close() => state = ConnectionState.Closed;
            public override void ChangeDatabase(string databaseName) => throw new NotImplementedException();
            protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotImplementedException();
            protected override DbCommand CreateDbCommand() => new FakeCommand(database) { Connection = this };
        }

        sealed class FakeCommand(FakeDatabase database) : DbCommand
        {
            // A real collection, because Dapper adds the parameters of the query to it
            readonly MySqlParameterCollection parameters = new MySqlCommand().Parameters;

            [AllowNull]
            public override string CommandText { get; set; } = "";
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbConnection? DbConnection { get; set; }
            protected override DbParameterCollection DbParameterCollection => parameters;
            protected override DbTransaction? DbTransaction { get; set; }

            protected override DbParameter CreateDbParameter() => new MySqlParameter();

            protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) =>
                database.AnswerAsync<DbDataReader>(ArmorNameRows);

            public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => database.AnswerAsync(() => 1);

            public override void Cancel() { }
            public override void Prepare() { }
            public override int ExecuteNonQuery() => throw new NotImplementedException();
            public override object? ExecuteScalar() => throw new NotImplementedException();
            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotImplementedException();
        }
    }
}
