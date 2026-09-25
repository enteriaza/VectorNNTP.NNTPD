using VectorNNTP.NNTPD.NntpDb;

namespace VectorNNTP.NNTPD.Tests.TestDoubles;

/// <summary>In-memory MySQL connection factory for offline NntpDB tests.</summary>
internal sealed class FakeNntpDbConnectionFactory : INntpDbConnectionFactory
{
    private readonly object _sync = new();
    private readonly List<FakeNntpDbConnection> _connections = [];

    public int OpenCount { get; private set; }

    public Exception? OpenException { get; set; }

    public int RemainingOpenFailures { get; set; }

    public TaskCompletionSource? BlockOpen { get; set; }

    public TaskCompletionSource? OpenStarted { get; set; }

    public TaskCompletionSource? BlockSelectOne { get; set; }

    public TaskCompletionSource? SelectOneStarted { get; set; }

    public int NextHealthCheckResult { get; set; } = 1;

    public Exception? NextHealthCheckException { get; set; }

    public IReadOnlyList<FakeNntpDbConnection> Connections
    {
        get
        {
            lock (_sync)
            {
                return _connections.ToArray();
            }
        }
    }

    public async Task<INntpDbConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        OpenStarted?.TrySetResult();
        if (BlockOpen is not null)
        {
            await BlockOpen.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (RemainingOpenFailures > 0)
        {
            RemainingOpenFailures--;
            throw new NntpDbUnavailableException("down");
        }

        if (OpenException is not null)
        {
            throw OpenException;
        }

        var connection = new FakeNntpDbConnection
        {
            HealthCheckResult = NextHealthCheckResult,
            HealthCheckException = NextHealthCheckException,
            BlockSelectOne = BlockSelectOne,
            SelectOneStarted = SelectOneStarted,
        };
        lock (_sync)
        {
            OpenCount++;
            _connections.Add(connection);
        }

        return connection;
    }
}

/// <summary>In-memory logical MySQL connection with failure injection.</summary>
internal sealed class FakeNntpDbConnection : INntpDbConnection
{
    public int SelectOneCount { get; private set; }

    public int DisposeCount { get; private set; }

    public int HealthCheckResult { get; set; } = 1;

    public Exception? HealthCheckException { get; set; }

    public TaskCompletionSource? BlockSelectOne { get; set; }

    public TaskCompletionSource? SelectOneStarted { get; set; }

    public async ValueTask<int> SelectOneAsync(CancellationToken cancellationToken)
    {
        SelectOneStarted?.TrySetResult();
        if (BlockSelectOne is not null)
        {
            await BlockSelectOne.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        SelectOneCount++;
        if (HealthCheckException is not null)
        {
            throw HealthCheckException;
        }

        return HealthCheckResult;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}
