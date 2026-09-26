using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.RabbitMq;

namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Hosts the confirm-enabled Article Work response publisher. Does not own the RabbitMQ connection.
/// </summary>
/// <remarks>
/// Startup fails if a publish channel cannot be opened on the current generation.
/// After start, connection replacement rebuilds the publisher channel. This type
/// does not implement a second connection-recovery loop and never ACK/NACKs deliveries.
/// Completing <see cref="PublishAsync"/> means the broker confirmed. Confirmation is
/// not permission to ACK the original delivery.
/// </remarks>
public sealed class ArticleWorkResponsePublisher : IArticleWorkResponsePublisher, IHostedService, IAsyncDisposable
{
    private readonly IBackFillerRabbitMqService _connections;
    private readonly BackFillerRuntimeOptions _runtime;
    private readonly ILogger<ArticleWorkResponsePublisher> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly SemaphoreSlim _replaceGate = new(1, 1);
    private readonly CancellationTokenSource _runCts = new();

    private IBackFillerRabbitMqPublishChannel? _channel;
    private Task _replaceTask = Task.CompletedTask;
    private ArticleWorkResponsePublisherState _state = ArticleWorkResponsePublisherState.Created;
    private int _started;
    private int _disposed;
    private bool _stopping;

    /// <summary>
    /// Initializes a new response publisher.
    /// </summary>
    /// <param name="connections">Sole connection owner.</param>
    /// <param name="runtime">Validated runtime snapshot.</param>
    /// <param name="logger">Publisher logger.</param>
    public ArticleWorkResponsePublisher(
        IBackFillerRabbitMqService connections,
        BackFillerRuntimeOptions runtime,
        ILogger<ArticleWorkResponsePublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(logger);
        _connections = connections;
        _runtime = runtime;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CompletesSuccessPublication => true;

    /// <summary>Gets the current local lifecycle state.</summary>
    public ArticleWorkResponsePublisherState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Gets the publish-channel generation, or zero before start.</summary>
    public long Generation
    {
        get
        {
            lock (_gate)
            {
                return _channel?.Generation ?? 0;
            }
        }
    }

    /// <summary>Gets the caller-owned publish channel while the publisher is live (tests).</summary>
    internal IBackFillerRabbitMqPublishChannel? Channel
    {
        get
        {
            lock (_gate)
            {
                return _channel;
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

        lock (_gate)
        {
            _state = ArticleWorkResponsePublisherState.Starting;
        }

        await _replaceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _connections.ConnectionReplaced += OnConnectionReplaced;
            await ReplaceChannelAsync(cancellationToken, startup: true).ConfigureAwait(false);
            lock (_gate)
            {
                _state = ArticleWorkResponsePublisherState.Running;
            }

            ArticleWorkLogMessages.PublisherRunning(_logger, Generation);
        }
        catch (Exception ex)
        {
            ArticleWorkLogMessages.PublisherStartFailed(_logger, ex.Message);
            _connections.ConnectionReplaced -= OnConnectionReplaced;
            await DisposeChannelAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _state = ArticleWorkResponsePublisherState.Stopped;
            }

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
        await DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        lock (_gate)
        {
            _stopping = true;
            _state = ArticleWorkResponsePublisherState.Retiring;
        }

        ArticleWorkLogMessages.PublisherRetiring(_logger, Generation);
        _connections.ConnectionReplaced -= OnConnectionReplaced;
        await _runCts.CancelAsync().ConfigureAwait(false);

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
            ArticleWorkLogMessages.PublisherReplaceFailed(_logger, ex.Message);
        }

        await _replaceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeChannelAsync().ConfigureAwait(false);
            await WaitForPublishGateReleaseAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _state = ArticleWorkResponsePublisherState.Stopped;
            }

            ArticleWorkLogMessages.PublisherStopped(_logger, Generation);
        }
        finally
        {
            _replaceGate.Release();
            _replaceGate.Dispose();
            _publishGate.Dispose();
            _runCts.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task PublishAsync(ArticleWorkResponseIntent intent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ThrowIfNotRunning();
        if (string.IsNullOrWhiteSpace(intent.CorrelationId) || string.IsNullOrWhiteSpace(intent.ReplyTo))
        {
            throw new InvalidOperationException("Response publication requires CorrelationId and ReplyTo.");
        }

        var body = ArticleWorkResponseWireProtocol.SerializeV1(intent);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _runCts.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(_runtime.RabbitMq.PublishConfirmTimeoutSeconds));

        await _publishGate.WaitAsync(linked.Token).ConfigureAwait(false);
        var generation = 0L;
        try
        {
            ThrowIfNotRunning();
            var channel = CurrentOpenChannelOrThrow();
            generation = channel.Generation;
            if (!IsPublisherGenerationCurrent(generation))
            {
                throw new InvalidOperationException("RabbitMQ publish generation is no longer current.");
            }

            var publication = new BackFillerRabbitMqPublication(
                intent.ReplyTo,
                intent.CorrelationId,
                ArticleWorkResponseWireProtocol.JsonContentType,
                Guid.NewGuid().ToString("D"),
                intent.RequestId?.ToString("D"),
                ArticleWorkResponseWireProtocol.ExpirationMilliseconds,
                body);

            await channel.PublishConfirmedAsync(publication, linked.Token).ConfigureAwait(false);

            if (!IsPublisherGenerationCurrent(generation) || !channel.IsOpen || !ReferenceEquals(Channel, channel))
            {
                throw new InvalidOperationException(
                    "RabbitMQ publish generation became invalid during confirmation.");
            }

            ArticleWorkLogMessages.PublicationConfirmed(
                _logger,
                generation,
                intent.RequestId?.ToString("D") ?? "(none)",
                intent.CorrelationId,
                ArticleWorkResponseWireProtocol.OutcomeName(intent.Outcome));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ArticleWorkLogMessages.PublicationFailed(
                _logger,
                generation,
                intent.RequestId?.ToString("D") ?? "(none)",
                intent.Outcome.ToString(),
                ex.Message);
            throw;
        }
        finally
        {
            _publishGate.Release();
        }
    }

    /// <summary>
    /// Installs <paramref name="candidate"/> only when it is not older than the current channel.
    /// A stale candidate is disposed and cannot replace or dispose a newer channel.
    /// </summary>
    internal async Task InstallPublishChannelAsync(IBackFillerRabbitMqPublishChannel candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        IBackFillerRabbitMqPublishChannel? replaced = null;
        IBackFillerRabbitMqPublishChannel? stale = null;
        lock (_gate)
        {
            if (_channel is { } current && current.Generation > candidate.Generation)
            {
                stale = candidate;
            }
            else
            {
                replaced = _channel;
                _channel = candidate;
            }
        }

        if (stale is not null)
        {
            await stale.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (replaced is not null && !ReferenceEquals(replaced, candidate))
        {
            await replaced.DisposeAsync().ConfigureAwait(false);
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

            _replaceTask = RebuildAfterReplacementAsync();
        }
    }

    private async Task RebuildAfterReplacementAsync()
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

            await ReplaceChannelAsync(_runCts.Token, startup: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ArticleWorkLogMessages.PublisherReplaceFailed(_logger, ex.Message);
        }
        finally
        {
            _replaceGate.Release();
        }
    }

    private async Task ReplaceChannelAsync(CancellationToken cancellationToken, bool startup)
    {
        if (!_connections.TryGetCurrent(out var handle))
        {
            if (startup)
            {
                throw new InvalidOperationException("RabbitMQ connection is not ready for Article Work response publication.");
            }

            return;
        }

        ArticleWorkLogMessages.PublisherStarting(_logger, handle.Generation);
        var channel = await handle.CreatePublishChannelAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!handle.IsCurrent || channel.Generation != handle.Generation)
            {
                throw new InvalidOperationException("RabbitMQ publish channel generation is no longer current.");
            }

            await InstallPublishChannelAsync(channel).ConfigureAwait(false);
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task DisposeChannelAsync()
    {
        IBackFillerRabbitMqPublishChannel? channel;
        lock (_gate)
        {
            channel = _channel;
            _channel = null;
        }

        if (channel is not null)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task WaitForPublishGateReleaseAsync()
    {
        using var waitCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Max(1, _runtime.RabbitMq.PublishConfirmTimeoutSeconds)));
        try
        {
            await _publishGate.WaitAsync(waitCts.Token).ConfigureAwait(false);
            _publishGate.Release();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private IBackFillerRabbitMqPublishChannel CurrentOpenChannelOrThrow()
    {
        lock (_gate)
        {
            if (_channel is not { IsOpen: true } channel)
            {
                throw new InvalidOperationException("Article Work response publisher has no open publish channel.");
            }

            return channel;
        }
    }

    private bool IsPublisherGenerationCurrent(long generation) =>
        _connections.TryGetCurrent(out var handle)
        && handle.Generation == generation
        && handle.IsCurrent;

    private void ThrowIfNotRunning()
    {
        lock (_gate)
        {
            if (_stopping || _state != ArticleWorkResponsePublisherState.Running)
            {
                throw new InvalidOperationException("Article Work response publisher is not accepting publications.");
            }
        }
    }
}
