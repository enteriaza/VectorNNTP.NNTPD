using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.RabbitMq;

namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Hosts one consume session per desired NNTP slot when that backbone has usable NNTP capacity.
/// Does not own the RabbitMQ connection. Declares per-backbone ArticleWork topology only for
/// backbones that currently have usable capacity, immediately before consumers start.
/// </summary>
public sealed class ArticleWorkConsumerService : IHostedService, IAsyncDisposable, IArticleWorkConsumerReconciliation
{
    /// <summary>Old-worker consumer reconcile cadence.</summary>
    internal static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(15);

    private readonly IBackFillerRabbitMqService _connections;
    private readonly BackFillerRuntimeOptions _runtime;
    private readonly IArticleWorkHandler _handler;
    private readonly IArticleWorkResponsePublisher _publisher;
    private readonly ILogger<ArticleWorkConsumerService> _logger;
    private readonly IBackFillerProviderCatalog _catalog;
    private readonly IBackboneUsableCapacityProvider _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, ArticleWorkConsumerSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _replaceGate = new(1, 1);
    private readonly CancellationTokenSource _reconcileCts = new();

    private Task _replaceTask = Task.CompletedTask;
    private Task? _reconcileLoop;
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
    /// <param name="catalog">Current provider snapshot. Empty when omitted.</param>
    /// <param name="capacity">Published usable NNTP capacity. Empty when omitted.</param>
    public ArticleWorkConsumerService(
        IBackFillerRabbitMqService connections,
        BackFillerRuntimeOptions runtime,
        IArticleWorkHandler handler,
        IArticleWorkResponsePublisher publisher,
        ILogger<ArticleWorkConsumerService> logger,
        IBackFillerProviderCatalog? catalog = null,
        IBackboneUsableCapacityProvider? capacity = null)
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
        _catalog = catalog ?? new StaticBackFillerProviderCatalog();
        _capacity = capacity ?? new BackboneUsableCapacityState();
    }

    /// <inheritdoc />
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return Volatile.Read(ref _started) == 1 && !_stopping && Volatile.Read(ref _disposed) == 0;
            }
        }
    }

    /// <summary>Gets a snapshot of live sessions (tests).</summary>
    internal IReadOnlyList<ArticleWorkConsumerSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions.Values];
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
            _capacity.SnapshotPublished += OnCapacitySnapshotPublished;
            await ReconcileSessionsAsync(cancellationToken).ConfigureAwait(false);
            _reconcileLoop = RunReconcileLoopAsync(_reconcileCts.Token);
        }
        catch
        {
            _connections.ConnectionReplaced -= OnConnectionReplaced;
            _capacity.SnapshotPublished -= OnCapacitySnapshotPublished;
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

    /// <inheritdoc />
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        if (!IsRunning)
        {
            return;
        }

        await _replaceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRunning)
            {
                return;
            }

            await ReconcileSessionsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _replaceGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RetireCapacityAsync(
        string backbone,
        int retainConnectionCount,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backbone);
        ArgumentOutOfRangeException.ThrowIfNegative(retainConnectionCount);

        List<ArticleWorkConsumerSession> retire = [];
        lock (_gate)
        {
            foreach (var pair in _sessions.ToArray())
            {
                if (!string.Equals(pair.Value.Backbone, backbone, StringComparison.OrdinalIgnoreCase)
                    || pair.Value.ConnectionNumber <= retainConnectionCount)
                {
                    continue;
                }

                _sessions.Remove(pair.Key);
                retire.Add(pair.Value);
            }
        }

        foreach (var session in retire)
        {
            await session.RetireAsync(cancellationToken).ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }
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
        _capacity.SnapshotPublished -= OnCapacitySnapshotPublished;
        await _reconcileCts.CancelAsync().ConfigureAwait(false);
        var loop = _reconcileLoop;
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

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
            _reconcileCts.Dispose();
        }
    }

    private async Task RunReconcileLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(ReconcileInterval, cancellationToken).ConfigureAwait(false);
                await ReconcileAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void OnCapacitySnapshotPublished(object? sender, EventArgs eventArgs)
    {
        if (!IsRunning)
        {
            return;
        }

        _ = ReconcileObservedAsync();
    }

    private async Task ReconcileObservedAsync()
    {
        try
        {
            await ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ArticleWorkLogMessages.ConsumerReplaceFailed(_logger, ex.Message);
        }
    }

    private async Task ReconcileSessionsAsync(CancellationToken cancellationToken)
    {
        var pipeline = new ArticleWorkDeliveryPipeline(
            _handler,
            _publisher,
            _runtime.RabbitMq.WorkRequestMaxPayloadBytes);
        var prefetch = _runtime.RabbitMq.ConsumerPrefetchCount is { } configured and > 0
            ? configured
            : (ushort)1;

        var desired = BuildDesiredSessions();
        var started = new List<ArticleWorkConsumerSession>();
        List<ArticleWorkConsumerSession> stale = [];
        lock (_gate)
        {
            foreach (var pair in _sessions.ToArray())
            {
                if (desired.ContainsKey(pair.Key) && pair.Value.State == ArticleWorkConsumerState.Running)
                {
                    desired.Remove(pair.Key);
                    continue;
                }

                if (!desired.ContainsKey(pair.Key))
                {
                    _sessions.Remove(pair.Key);
                    stale.Add(pair.Value);
                }
            }
        }

        foreach (var session in stale)
        {
            await session.RetireAsync(cancellationToken).ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }

        if (desired.Count > 0)
        {
            var readyBackbones = await EnsureProviderTopologyAsync(
                    desired.Values.Select(static item => item.Backbone),
                    cancellationToken)
                .ConfigureAwait(false);
            if (readyBackbones.Count == 0)
            {
                ArticleWorkLogMessages.ConsumerReconcileCompleted(_logger, Sessions.Count);
                return;
            }

            foreach (var pair in desired.ToArray())
            {
                if (!readyBackbones.Contains(pair.Value.Backbone))
                {
                    desired.Remove(pair.Key);
                }
            }
        }

        try
        {
            foreach (var identity in desired.Values.OrderBy(static item => item.Backbone, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(static item => item.ConnectionNumber))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = new ArticleWorkConsumerSession(
                    identity.Backbone,
                    prefetch,
                    pipeline,
                    _connections,
                    _logger,
                    _runtime.Shutdown,
                    identity.ConnectionNumber,
                    identity.ConnectionLimit);
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

        lock (_gate)
        {
            if (_stopping)
            {
                stale = started;
            }
            else
            {
                foreach (var session in started)
                {
                    _sessions[session.SessionKey] = session;
                }

                stale = [];
            }
        }

        foreach (var session in stale)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        ArticleWorkLogMessages.ConsumerReconcileCompleted(_logger, Sessions.Count);
    }

    /// <summary>
    /// Declares classic durable fanout topology for each backbone that is about to receive
    /// consumers. Topology is never deleted when capacity later drops to zero.
    /// </summary>
    private async Task<HashSet<string>> EnsureProviderTopologyAsync(
        IEnumerable<string> backbones,
        CancellationToken cancellationToken)
    {
        var ready = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var backbone in backbones)
        {
            if (!string.IsNullOrWhiteSpace(backbone))
            {
                unique.Add(backbone);
            }
        }

        if (unique.Count == 0)
        {
            return ready;
        }

        if (!_connections.TryGetCurrent(out var handle))
        {
            foreach (var backbone in unique)
            {
                ArticleWorkLogMessages.ProviderTopologyDeclareFailed(
                    _logger,
                    backbone,
                    BackFillerRabbitMqTopology.ComposeProviderEntity(backbone),
                    "RabbitMQ connection is not ready for topology declaration.");
            }

            return ready;
        }

        IBackFillerRabbitMqChannel? channel = null;
        try
        {
            channel = await handle.CreateChannelAsync(cancellationToken).ConfigureAwait(false);
            foreach (var backbone in unique.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var queue = BackFillerRabbitMqTopology.ComposeProviderEntity(backbone);
                try
                {
                    await BackFillerArticleWorkTopology
                        .DeclareProviderEndpointAsync(channel, backbone, cancellationToken)
                        .ConfigureAwait(false);
                    ready.Add(backbone);
                    ArticleWorkLogMessages.ProviderTopologyDeclared(_logger, backbone, queue);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ArticleWorkLogMessages.ProviderTopologyDeclareFailed(
                        _logger,
                        backbone,
                        queue,
                        ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var backbone in unique)
            {
                if (ready.Contains(backbone))
                {
                    continue;
                }

                ArticleWorkLogMessages.ProviderTopologyDeclareFailed(
                    _logger,
                    backbone,
                    BackFillerRabbitMqTopology.ComposeProviderEntity(backbone),
                    ex.Message);
            }
        }
        finally
        {
            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
        }

        return ready;
    }

    private Dictionary<string, DesiredConsumer> BuildDesiredSessions()
    {
        var desired = new Dictionary<string, DesiredConsumer>(StringComparer.Ordinal);
        foreach (var provider in _catalog.Providers)
        {
            if (provider.MaxSessions <= 0)
            {
                continue;
            }

            if (!_capacity.HasUsableCapacityForBackbone(provider.Backbone))
            {
                continue;
            }

            for (var connectionNumber = 1; connectionNumber <= provider.MaxSessions; connectionNumber++)
            {
                var key = ArticleWorkConsumerSession.ComposeSessionKey(provider.Backbone, connectionNumber);
                desired[key] = new DesiredConsumer(provider.Backbone, connectionNumber, provider.MaxSessions);
            }
        }

        return desired;
    }

    private async Task StopSessionsAsync(CancellationToken cancellationToken = default)
    {
        List<ArticleWorkConsumerSession> sessions;
        lock (_gate)
        {
            sessions = [.. _sessions.Values];
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

            await ReconcileSessionsAsync(CancellationToken.None).ConfigureAwait(false);
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

    private readonly record struct DesiredConsumer(string Backbone, int ConnectionNumber, int ConnectionLimit);
}
