using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MinerBot.Dashboard.Tests.Support
{
    // Keeps every line the app logs, after its own filters, with any exception as text, for the tests to read
    internal sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public sealed record Entry(string Category, LogLevel Level, string Message, string? Exception);

        readonly ConcurrentQueue<Entry> entries = new();

        public IReadOnlyList<Entry> Entries => [.. entries];

        // Everything as one text, to look for what must never be there
        public string Text => string.Join('\n', entries.Select(entry => $"{entry.Category} {entry.Level} {entry.Message} {entry.Exception}"));

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, entries);

        // The app's host disposes its providers, and the test reads the lines after that
        public void Dispose()
        {
        }

        sealed class Logger(string category, ConcurrentQueue<Entry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new Entry(category, logLevel, formatter(state, exception), exception?.ToString()));
        }
    }
}
