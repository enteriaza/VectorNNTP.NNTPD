using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.RabbitMq;

namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Hosts one consume session per provider backbone. Does not own the RabbitMQ connection.
/// </summary>
/// <remarks>
/// Startup fails if the current connection cannot create every required consumer.
/// After start, connection replacement rebuilds sessions on the new generation.
/// This service does not implement a second connection-recovery loop.
/// </remarks>
public sealed class ArticleWorkConsumerService : IHostedService, IAsyncDisposable
{
    private readonly IBackFillerRabbitMqService _connections;
    private readonly BackFillerRuntimeOptions _runtime;
    private readonly IArticleWorkHandler _handler;
    private readonly IArticleWorkResponsePublisher _publisher;
    private readonly ILogger<ArticleWorkConsumerService> _logger;
    private readonly object _gate = new();
    private readonly List<ArticleWorkConsumerSession> _sessions = [];
    private readonly SemaphoreSlim _replaceGate = new(1, 1);

    private Task _replaceTask = Task.CompletedTask;
    private int _started;
    private int _disposed;
    private bool _stopping;

    /// <summary>
    /// Initializes a new consumer service.
    /// </summary>
    /// <param name="connections">Sole connection owner.</param>
    /// <param name="runtime">Validated runtime snapshot.</param>
    /// <param name="handler">Admitted-work handler.</param>
    /// <param name="publisher">Response-publish seam.</param>
    /// <param name="logger">Consumer logger.</param>
    public ArticleWorkConsumerService(
        IBackFillerRabbitMqService connections,
        BackFillerRuntimeOptions runtime,
        IArticleWorkHandler handler,
        IArticleWorkResponsePublisher publisher,
        ILogger<ArticleWorkConsumerService> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(logger);
        _connections = connections;
        _runtime = runtime;
        _handler = handler;
        _publisher = publisher;
        _logger = logger;
    }

    /// <summary>Gets a snapshot of live sessions (tests).</summary>
    internal IReadOnlyList<ArticleWorkConsumerSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions];
            }
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        await _replaceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _connections.ConnectionReplaced += OnConnectionReplaced;
            await StartSessionsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _connections.ConnectionReplaced -= OnConnectionReplaced;
            await StopSessionsAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _started, 0);
            throw;
        }
        finally
        {
            _replaceGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await ShutdownAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        using var grace = new CancellationTokenSource(_runtime.Shutdown.GracePeriod);
        await ShutdownAsync(grace.Token).ConfigureAwait(false);
    }

    private async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        lock (_gate)
        {
            _stopping = true;
        }

        _connections.ConnectionReplaced -= OnConnectionReplaced;
        Task replace;
        lock (_gate)
        {
            replace = _replaceTask;
        }

        try
        {
            await replace.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ArticleWorkLogMessages.ConsumerReplaceFailed(_logger, ex.Message);
        }

        await _replaceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopSessionsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _replaceGate.Release();
            _replaceGate.Dispose();
        }
    }

    private async Task StartSessionsAsync(CancellationToken cancellationToken)
    {
        var pipeline = new ArticleWorkDeliveryPipeline(
            _handler,
            _publisher,
            _runtime.RabbitMq.WorkRequestMaxPayloadBytes);
        var prefetch = _runtime.RabbitMq.ConsumerPrefetchCount is { } configured and > 0
            ? configured
            : (ushort)1;

        var started = new List<ArticleWorkConsumerSession>();
        try
        {
            foreach (var backbone in BackFillerRabbitMqTopology.ProviderBackbones)
            {
                var session = new ArticleWorkConsumerSession(
                    backbone,
                    prefetch,
                    pipeline,
                    _connections,
                    _logger,
                    _runtime.Shutdown);
                await session.StartAsync(cancellationToken).ConfigureAwait(false);
                started.Add(session);
            }
        }
        catch
        {
            foreach (var session in started)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }

        List<ArticleWorkConsumerSession> abandoned = [];
        lock (_gate)
        {
            if (_stopping)
            {
                abandoned = started;
            }
            else
            {
                _sessions.AddRange(started);
            }
        }

        foreach (var session in abandoned)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task StopSessionsAsync(CancellationToken cancellationToken = default)
    {
        List<ArticleWorkConsumerSession> sessions;
        lock (_gate)
        {
            sessions = [.. _sessions];
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            await session.RetireAsync(cancellationToken).ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnConnectionReplaced(object? sender, BackFillerRabbitMqConnectionReplacedEventArgs eventArgs)
    {
        if (!eventArgs.IsReplacement)
        {
            return;
        }

        lock (_gate)
        {
            if (_stopping)
            {
                return;
            }

            _replaceTask = ReplaceSessionsAsync();
        }
    }

    private async Task ReplaceSessionsAsync()
    {
        await _replaceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool stopping;
            lock (_gate)
            {
                stopping = _stopping;
            }

            if (stopping || Volatile.Read(ref _started) == 0)
            {
                return;
            }

            await StopSessionsAsync().ConfigureAwait(false);

            lock (_gate)
            {
                stopping = _stopping;
            }

            if (stopping)
            {
                return;
            }

            await StartSessionsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ArticleWorkLogMessages.ConsumerReplaceFailed(_logger, ex.Message);
        }
        finally
        {
            _replaceGate.Release();
        }
    }
}
