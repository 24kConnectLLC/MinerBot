using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DataAccessLibrary.Tests
{
    // Keeps what was logged, from any thread, for the test to read
    internal sealed class ListLogger : ILogger
    {
        readonly ConcurrentQueue<(LogLevel Level, string Message)> entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => entries.ToList();

        public bool Logged(LogLevel level, string text) => entries.Any(entry => entry.Level == level && entry.Message.Contains(text));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
