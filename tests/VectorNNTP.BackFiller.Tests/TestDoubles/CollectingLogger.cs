using Microsoft.Extensions.Logging;

namespace VectorNNTP.BackFiller.Tests.TestDoubles
{
    internal readonly record struct CollectedLog(LogLevel Level, EventId EventId, string Message);

    internal sealed class CollectingLogger<T> : ILogger<T>
    {
        private readonly List<CollectedLog> _entries = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries.Select(static entry => entry.Message)];
                }
            }
        }

        public IReadOnlyList<CollectedLog> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            var message = formatter(state, exception);
            lock (_entries)
            {
                _entries.Add(new CollectedLog(logLevel, eventId, message));
                if (exception is not null)
                {
                    _entries.Add(new CollectedLog(logLevel, eventId, exception.Message));
                }
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
