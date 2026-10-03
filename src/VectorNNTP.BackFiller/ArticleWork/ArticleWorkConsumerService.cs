using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.RabbitMq;

using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Hosts one consume session per desired NNTP slot when that backbone has usable NNTP capacity.
/// Does not own the RabbitMQ connection. Declares per-backbone ArticleWork topology only for
/// backbones that currently have usable capacity, immediately before consumers start.
/// </summary>
/// <remarks>
/// Membership is reconciled on start, on <see cref="ReconcileInterval"/>, after a usable-capacity snapshot,
/// and after a RabbitMQ connection replacement. Those paths share <see cref="_replaceGate"/> except
/// <see cref="RetireCapacityAsync"/>. Tracked sessions are guarded by <see cref="_gate"/>.
/// This type does not delete topology when capacity later drops to zero.
/// </remarks>
internal sealed class ArticleWorkConsumerService : IHostedService, IAsyncDisposable, IArticleWorkConsumerReconciliation
{
    /// <summary>Delay waited by the background reconcile loop between membership passes.</summary>
    internal static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(15);

    /// <summary>Process RabbitMQ connection owner. This service opens channels on it and does not open another connection.</summary>
    private readonly IRabbitMqService _connections;

    /// <summary>Validated runtime snapshot. Supplies prefetch, payload limit, and the disposal grace period.</summary>
    private readonly BackFillerRuntimeOptions _runtime;

    /// <summary>Admitted-work handler shared by pipelines created during reconcile.</summary>
    private readonly IArticleWorkHandler _handler;

    /// <summary>Response publisher shared by those pipelines. This service does not publish itself.</summary>
    private readonly IArticleWorkResponsePublisher _publisher;

    /// <summary>Consumer logger. This type does not write payloads or credentials.</summary>
    private readonly ILogger<ArticleWorkConsumerService> _logger;

    /// <summary>Provider catalog read when building desired consume slots. An omitted catalog is empty.</summary>
    private readonly IBackFillerProviderCatalog _catalog;

    /// <summary>Usable NNTP capacity gate. An omitted provider starts empty and raises no snapshots.</summary>
    private readonly IBackboneUsableCapacityProvider _capacity;

    /// <summary>Guards the session dictionary, the stopping flag, and publication of the replacement task.</summary>
    private readonly object _gate = new();

    /// <summary>Tracked sessions keyed by <see cref="ArticleWorkConsumerSession.SessionKey"/> using ordinal comparison.</summary>
    private readonly Dictionary<string, ArticleWorkConsumerSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>Serializes start, reconcile, connection replacement, and shutdown session replacement.</summary>
    private readonly SemaphoreSlim _replaceGate = new(1, 1);

    /// <summary>Cancelled to stop the background reconcile loop. Disposed by shutdown.</summary>
    private readonly CancellationTokenSource _reconcileCts = new();

    /// <summary>Latest connection-replacement rebuild. A later replacement can overwrite it before the previous task is observed.</summary>
    private Task _replaceTask = Task.CompletedTask;

    /// <summary>Background reconcile loop. Null until start assigns it. Shutdown awaits it.</summary>
    private Task? _reconcileLoop;

    /// <summary>One after <see cref="StartAsync"/> has entered startup. Cleared when that attempt fails.</summary>
    private int _started;

    /// <summary>One after <see cref="ShutdownAsync"/> has entered shutdown.</summary>
    private int _disposed;

    /// <summary>Set under <see cref="_gate"/> once shutdown has begun. Blocks new replacement work and session publication.</summary>
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
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="connections"/>, <paramref name="runtime"/>, <paramref name="handler"/>,
    /// <paramref name="publisher"/>, or <paramref name="logger"/> is null.
    /// </exception>
    /// <remarks>Does not subscribe, declare topology, or start sessions. Omitted collaborators are empty stand-ins, not the host singletons.</remarks>
    internal ArticleWorkConsumerService(
        IRabbitMqService connections,
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

    /// <summary>
    /// Gets whether startup has been claimed, shutdown has not begun, and disposal has not been claimed.
    /// </summary>
    /// <remarks>
    /// True while <see cref="StartAsync"/> is in progress, because the start flag is set before sessions exist and is cleared only if that attempt fails.
    /// The read takes <see cref="_gate"/>.
    /// </remarks>
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

    /// <summary>Gets a copy of the tracked sessions, taken under the session lock.</summary>
    /// <remarks>Intended for tests. The copy does not observe later dictionary changes.</remarks>
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

    /// <summary>
    /// Subscribes to connection replacement and capacity snapshots, reconciles sessions once, and starts the reconcile loop.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels the replacement-gate wait, the first reconcile, and topology declaration during that reconcile.
    /// A second call returns without waiting for an in-progress start.
    /// </param>
    /// <returns>
    /// A task that completes when the first reconcile has finished and the loop task has been stored,
    /// or immediately when start has already been entered.
    /// </returns>
    /// <remarks>
    /// On failure the subscriptions are removed, sessions started during the attempt are stopped, the start flag is cleared, and the exception propagates.
    /// The loop is not started when reconcile throws. A later call can retry.
    /// </remarks>
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

    /// <summary>Shuts the consumer host down and retires tracked sessions with <paramref name="cancellationToken"/>.</summary>
    /// <param name="cancellationToken">
    /// Passed to session retirement. The replacement-gate wait and the reconcile-loop wait are not cancelled by this token.
    /// </param>
    /// <returns>A task that completes when shutdown finishes. A second call returns a completed task.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await ShutdownAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shuts the consumer host down, bounding session retirement by <see cref="BackFillerShutdownRuntimeOptions.GracePeriod"/>.
    /// </summary>
    /// <returns>
    /// A task that completes when this call's shutdown finishes.
    /// When <see cref="ShutdownAsync"/> has already been entered, this call returns a completed task and does not wait for that earlier shutdown.
    /// </returns>
    /// <remarks>
    /// The grace token is created here and passed to <see cref="ShutdownAsync"/>. It is not the host stop token.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        using var grace = new CancellationTokenSource(_runtime.Shutdown.GracePeriod);
        await ShutdownAsync(grace.Token).ConfigureAwait(false);
    }

    /// <summary>Creates, keeps, or retires consume sessions to match desired membership.</summary>
    /// <param name="cancellationToken">Cancels the replacement-gate wait and the membership pass.</param>
    /// <returns>
    /// A task that completes when the pass finishes. Returns a completed task when <see cref="IsRunning"/> is false
    /// before the gate is taken or after it is acquired.
    /// </returns>
    /// <remarks>
    /// Serialized with start, connection replacement, and shutdown through <see cref="_replaceGate"/>.
    /// Does not catch exceptions from the membership pass.
    /// </remarks>
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

    /// <summary>
    /// Retires tracked sessions for <paramref name="backbone"/> whose connection number is greater than <paramref name="retainConnectionCount"/>.
    /// </summary>
    /// <param name="backbone">Provider backbone to match, ordinal ignore-case.</param>
    /// <param name="retainConnectionCount">Highest connection number to keep. Zero retires every positive slot for that backbone.</param>
    /// <param name="cancellationToken">Passed to each selected session's retirement. Disposal of the session then uses no extra budget.</param>
    /// <returns>A task that completes when each selected session has been retired and disposed.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backbone"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="retainConnectionCount"/> is negative.</exception>
    /// <remarks>
    /// Sessions at or below the retained number stay tracked. Removal happens under <see cref="_gate"/>; retirement happens outside it.
    /// This method does not take <see cref="_replaceGate"/> and does not require <see cref="IsRunning"/>.
    /// </remarks>
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

    /// <summary>
    /// Unsubscribes, stops the reconcile loop, waits for the tracked replacement, and retires every tracked session.
    /// </summary>
    /// <param name="cancellationToken">
    /// Passed only to session retirement. The loop wait and the replacement-gate wait do not use this token.
    /// </param>
    /// <returns>A task that completes when sessions are stopped and the gate and reconcile source are disposed. A second call returns immediately.</returns>
    /// <remarks>
    /// Sets <see cref="_stopping"/> before waiting. <see cref="OperationCanceledException"/> from the reconcile loop is swallowed.
    /// Other exceptions from that loop propagate. A faulted replacement task is logged with its message and shutdown continues.
    /// </remarks>
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

    /// <summary>Waits <see cref="ReconcileInterval"/> and then reconciles until <paramref name="cancellationToken"/> is cancelled.</summary>
    /// <param name="cancellationToken">Loop cancellation, owned by <see cref="_reconcileCts"/>.</param>
    /// <returns>A task that completes when the token cancels the delay or a reconcile. Other exceptions propagate and end the loop.</returns>
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

    /// <summary>Starts an untracked reconcile when the host is running.</summary>
    /// <param name="sender">Event sender. Not used.</param>
    /// <param name="eventArgs">Empty snapshot notice. Not used.</param>
    /// <remarks>The reconcile uses <see cref="CancellationToken.None"/>. Failures are logged by <see cref="ReconcileObservedAsync"/>.</remarks>
    private void OnCapacitySnapshotPublished(object? sender, EventArgs eventArgs)
    {
        if (!IsRunning)
        {
            return;
        }

        _ = ReconcileObservedAsync();
    }

    /// <summary>Reconciles after a capacity snapshot.</summary>
    /// <returns>
    /// A task that completes when <see cref="ReconcileAsync"/> finishes or its exception has been logged.
    /// The exception is not rethrown. Cancellation is not observed.
    /// </returns>
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

    /// <summary>
    /// Retires sessions that are no longer desired, declares topology for backbones that still need consumers, and starts the missing slots.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels retirement of stale sessions, topology declaration, and each new session start.
    /// </param>
    /// <returns>A task that completes when the pass has logged its tracked session count.</returns>
    /// <remarks>
    /// A tracked running session whose key is still desired is kept. Any other tracked session whose key is no longer desired is removed and retired.
    /// A desired key with no running tracked session is started. Prefetch is the configured positive count, otherwise 1.
    /// Sessions started in one pass share a new pipeline. A start failure disposes sessions started in that pass and propagates; they are not tracked.
    /// If shutdown is set before publication, those new sessions are disposed instead of tracked.
    /// When no desired backbone's topology is ready, the pass logs and returns without starting sessions.
    /// </remarks>
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
    /// Declares durable quorum fanout topology for each backbone that is about to receive
    /// consumers. Topology is never deleted when capacity later drops to zero.
    /// </summary>
    /// <param name="backbones">Backbones that still need consumers. Blank names are ignored. Comparison is ordinal ignore-case.</param>
    /// <param name="cancellationToken">Cancels channel creation and each declaration. Cancellation propagates.</param>
    /// <returns>
    /// Backbones whose declaration succeeded. Empty when there is nothing to declare, the connection is not ready, or every declaration failed.
    /// </returns>
    /// <remarks>
    /// When a current connection exists, one manual-ack channel is opened for the pass and disposed afterward. A failure for one backbone is logged and skipped.
    /// A failure to open the channel is logged for every backbone not already declared. <see cref="OperationCanceledException"/> is not logged.
    /// </remarks>
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

        IRabbitMqManualAckChannel? channel = null;
        try
        {
            channel = await handle.Connection
                .CreateManualAckChannelAsync(handle.Generation, cancellationToken)
                .ConfigureAwait(false);
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

    /// <summary>
    /// Builds one desired slot for each connection number from 1 through <see cref="BackFillerProviderDefinition.MaxSessions"/>
    /// when that provider has a positive limit and usable NNTP capacity.
    /// </summary>
    /// <returns>
    /// Desired slots keyed by <see cref="ArticleWorkConsumerSession.ComposeSessionKey"/>. Providers with no usable capacity are omitted.
    /// </returns>
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

    /// <summary>Removes every tracked session and retires then disposes each one.</summary>
    /// <param name="cancellationToken">Passed to retirement. The default is <see cref="CancellationToken.None"/>.</param>
    /// <returns>A task that completes when every removed session has been retired and disposed.</returns>
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

    /// <summary>Schedules a session rebuild when the event is a connection replacement and shutdown has not started.</summary>
    /// <param name="sender">Event sender. Not used.</param>
    /// <param name="eventArgs">Replacement notice. Non-replacement events are ignored.</param>
    /// <remarks>The scheduled task replaces <see cref="_replaceTask"/>. An earlier rebuild that is still running is no longer tracked.</remarks>
    private void OnConnectionReplaced(object? sender, RabbitMqConnectionReplacedEventArgs eventArgs)
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

    /// <summary>Stops every tracked session and reconciles again unless shutdown has started or start did not succeed.</summary>
    /// <returns>
    /// A task that completes when the rebuild attempt finishes. Failures are logged with the exception message and not rethrown.
    /// The reconcile uses <see cref="CancellationToken.None"/>.
    /// </returns>
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

    /// <summary>One desired consume slot derived from the provider catalog and usable capacity.</summary>
    /// <param name="Backbone">Provider backbone label, not case-folded.</param>
    /// <param name="ConnectionNumber">One-based slot in the range 1 through <paramref name="ConnectionLimit"/>.</param>
    /// <param name="ConnectionLimit">Provider <see cref="BackFillerProviderDefinition.MaxSessions"/> for this backbone.</param>
    private readonly record struct DesiredConsumer(string Backbone, int ConnectionNumber, int ConnectionLimit);
}
