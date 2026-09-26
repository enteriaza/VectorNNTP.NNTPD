using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.RabbitMq;

namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// One backbone-scoped consumer session that owns a single consume channel.
/// </summary>
/// <remarks>
/// Delivery states:
/// <list type="bullet">
/// <item><description>Broker-delivered: the fake/broker invoked the consumer callback. Not yet admitted.</description></item>
/// <item><description>Admitted/queued: admission succeeded and the delivery is waiting for the dispatch lock. This is queued work.</description></item>
/// <item><description>Active: the delivery holds the dispatch lock and has entered <c>ProcessAsync</c>.</description></item>
/// <item><description>Completed/settled: the pipeline has attempted settlement on the original channel, or skipped it as stale.</description></item>
/// </list>
/// RabbitMQ prefetch is not admission. Prefetched deliveries that arrive after retirement are not admitted and are not settled here; channel dispose lets the broker redeliver.
/// </remarks>
public sealed class ArticleWorkConsumerSession : IAsyncDisposable
{
    private static readonly BackFillerShutdownRuntimeOptions DefaultShutdown = new(
        TimeSpan.FromSeconds(30),
        DrainQueuedWork: true,
        FinishActiveArticles: true);

    private readonly string _backbone;
    private readonly string _queue;
    private readonly ushort _prefetch;
    private readonly ArticleWorkDeliveryPipeline _pipeline;
    private readonly IBackFillerRabbitMqService _connections;
    private readonly ILogger _logger;
    private readonly BackFillerShutdownRuntimeOptions _shutdown;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _dispatch = new(1, 1);
    private readonly CancellationTokenSource _queueCts = new();
    private readonly CancellationTokenSource _workCts = new();

    private IBackFillerRabbitMqChannel? _channel;
    private string? _consumerTag;
    private int _inFlight;
    private int _active;
    private TaskCompletionSource _drained = NewDrainSource();
    private ArticleWorkConsumerState _state = ArticleWorkConsumerState.Created;
    private Task? _retireTask;
    private int _disposed;

    /// <summary>
    /// Initializes a new session with default drain-and-finish shutdown policy.
    /// </summary>
    /// <param name="backbone">Provider backbone label.</param>
    /// <param name="prefetch">Per-channel prefetch. Phase 3 uses 1 unless configured.</param>
    /// <param name="pipeline">Parse/classify/settle pipeline.</param>
    /// <param name="connections">Process connection owner. Must not be used to open a second connection.</param>
    /// <param name="logger">Session logger.</param>
    public ArticleWorkConsumerSession(
        string backbone,
        ushort prefetch,
        ArticleWorkDeliveryPipeline pipeline,
        IBackFillerRabbitMqService connections,
        ILogger logger)
        : this(backbone, prefetch, pipeline, connections, logger, DefaultShutdown)
    {
    }

    /// <summary>
    /// Initializes a new session with a validated shutdown snapshot.
    /// </summary>
    /// <param name="backbone">Provider backbone label.</param>
    /// <param name="prefetch">Per-channel prefetch. Phase 3 uses 1 unless configured.</param>
    /// <param name="pipeline">Parse/classify/settle pipeline.</param>
    /// <param name="connections">Process connection owner. Must not be used to open a second connection.</param>
    /// <param name="logger">Session logger.</param>
    /// <param name="shutdown">Immutable runtime shutdown policy. Must not be re-read from options.</param>
    public ArticleWorkConsumerSession(
        string backbone,
        ushort prefetch,
        ArticleWorkDeliveryPipeline pipeline,
        IBackFillerRabbitMqService connections,
        ILogger logger,
        BackFillerShutdownRuntimeOptions shutdown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backbone);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(shutdown);
        if (prefetch == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(prefetch));
        }

        _backbone = backbone;
        _queue = RabbitMq.BackFillerRabbitMqTopology.ComposeProviderEntity(backbone);
        _prefetch = prefetch;
        _pipeline = pipeline;
        _connections = connections;
        _logger = logger;
        _shutdown = shutdown;
    }

    /// <summary>Gets the backbone context for this session.</summary>
    public string Backbone => _backbone;

    /// <summary>Gets the consume queue name.</summary>
    public string Queue => _queue;

    /// <summary>Gets the captured shutdown snapshot used by retirement.</summary>
    internal BackFillerShutdownRuntimeOptions Shutdown => _shutdown;

    /// <summary>Gets admitted deliveries that have not yet entered <c>ProcessAsync</c>.</summary>
    internal int QueuedCount
    {
        get
        {
            lock (_gate)
            {
                return Math.Max(0, _inFlight - Volatile.Read(ref _active));
            }
        }
    }

    /// <summary>Gets deliveries that currently hold dispatch and are inside <c>ProcessAsync</c>.</summary>
    internal int ActiveCount => Volatile.Read(ref _active);

    /// <summary>Gets the token cancelled when queued work must not start.</summary>
    internal CancellationToken QueueCancellationToken => _queueCts.Token;

    /// <summary>Gets the token cancelled when active work must stop.</summary>
    internal CancellationToken WorkCancellationToken => _workCts.Token;

    /// <summary>Gets the current local lifecycle state.</summary>
    public ArticleWorkConsumerState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Gets the channel generation, or zero before start.</summary>
    public long Generation { get; private set; }

    /// <summary>Gets the caller-owned consume channel while the session is live.</summary>
    internal IBackFillerRabbitMqChannel? Channel
    {
        get
        {
            lock (_gate)
            {
                return _channel;
            }
        }
    }

    /// <summary>
    /// Optional test seam signaled after the dispatch lock is acquired and before the
    /// delivery is classified as active.
    /// </summary>
    internal TaskCompletionSource? DispatchAcquired { get; set; }

    /// <summary>
    /// Optional test seam that holds a delivery in the queued-to-active transition
    /// until the caller releases it. Wait is still subject to the queued-work policy.
    /// </summary>
    internal TaskCompletionSource? DispatchAcquireHold { get; set; }

    /// <summary>
    /// Opens a caller-owned consume channel on the current connection generation.
    /// </summary>
    /// <param name="cancellationToken">Startup cancellation.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_state != ArticleWorkConsumerState.Created)
            {
                return;
            }

            _state = ArticleWorkConsumerState.Starting;
        }

        if (!_connections.TryGetCurrent(out var handle))
        {
            lock (_gate)
            {
                _state = ArticleWorkConsumerState.Stopped;
            }

            throw new InvalidOperationException("RabbitMQ connection is not ready for Article Work consume.");
        }

        ArticleWorkLogMessages.ConsumerStarting(_logger, _backbone, _queue, handle.Generation);
        IBackFillerRabbitMqChannel? channel = null;
        try
        {
            channel = await handle.CreateChannelAsync(cancellationToken).ConfigureAwait(false);
            var tag = await channel
                .BasicConsumeAsync(_queue, _prefetch, OnDeliveryAsync, cancellationToken)
                .ConfigureAwait(false);

            lock (_gate)
            {
                _channel = channel;
                _consumerTag = tag;
                Generation = handle.Generation;
                _state = ArticleWorkConsumerState.Running;
                channel = null;
            }

            ArticleWorkLogMessages.ConsumerRunning(_logger, _backbone, _queue, Generation, tag);
        }
        catch (Exception ex)
        {
            ArticleWorkLogMessages.ConsumerStartFailed(_logger, _backbone, _queue, ex.Message);
            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }

            lock (_gate)
            {
                _state = ArticleWorkConsumerState.Stopped;
            }

            throw;
        }
    }

    /// <summary>
    /// Cancels the consumer, applies the captured shutdown policy, waits for admitted work
    /// inside the supplied budget, and disposes the channel.
    /// </summary>
    public Task RetireAsync() => RetireAsync(CancellationToken.None);

    /// <summary>
    /// Cancels the consumer, applies the captured shutdown policy, and waits for admitted
    /// work until <paramref name="shutdownToken"/> is cancelled.
    /// </summary>
    /// <param name="shutdownToken">
    /// Host shutdown budget. When cancelled, remaining owned work is forced toward
    /// cancellation and the channel is released. This is not a second Article Work timeout.
    /// </param>
    public Task RetireAsync(CancellationToken shutdownToken)
    {
        lock (_gate)
        {
            _retireTask ??= RetireCoreAsync(shutdownToken);
            return _retireTask;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await RetireAsync().ConfigureAwait(false);
        _queueCts.Dispose();
        _workCts.Dispose();
        _dispatch.Dispose();
    }

    private async Task RetireCoreAsync(CancellationToken shutdownToken)
    {
        IBackFillerRabbitMqChannel? channel;
        string? tag;
        lock (_gate)
        {
            if (_state == ArticleWorkConsumerState.Stopped)
            {
                return;
            }

            _state = ArticleWorkConsumerState.Retiring;
            channel = _channel;
            tag = _consumerTag;
        }

        ArticleWorkLogMessages.ConsumerRetiring(_logger, _backbone, Generation);
        ArticleWorkLogMessages.ConsumerShutdownPolicy(
            _logger,
            _backbone,
            Generation,
            _shutdown.DrainQueuedWork,
            _shutdown.FinishActiveArticles,
            (int)_shutdown.GracePeriod.TotalSeconds);

        if (channel is not null && tag is not null && channel.IsOpen)
        {
            try
            {
                await channel.BasicCancelAsync(tag, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        if (!_shutdown.DrainQueuedWork)
        {
            await _queueCts.CancelAsync().ConfigureAwait(false);
        }

        if (!_shutdown.FinishActiveArticles)
        {
            await _workCts.CancelAsync().ConfigureAwait(false);
        }

        using var grace = shutdownToken.Register(OnShutdownGraceExpired);

        try
        {
            await WaitForDrainAsync().WaitAsync(shutdownToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            await _queueCts.CancelAsync().ConfigureAwait(false);
            await _workCts.CancelAsync().ConfigureAwait(false);
        }

        if (channel is not null)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        lock (_gate)
        {
            _channel = null;
            _consumerTag = null;
            _state = ArticleWorkConsumerState.Stopped;
        }

        ArticleWorkLogMessages.ConsumerStopped(_logger, _backbone, Generation);
    }

    private void OnShutdownGraceExpired()
    {
        ArticleWorkLogMessages.ConsumerShutdownGraceExpired(_logger, _backbone, Generation);
        _queueCts.Cancel();
        _workCts.Cancel();
    }

    private async Task OnDeliveryAsync(BackFillerRabbitMqConsumedDelivery delivery)
    {
        if (!TryAdmit())
        {
            return;
        }

        try
        {
            try
            {
                await _dispatch.WaitAsync(_queueCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await SettleCancelledAsync(delivery).ConfigureAwait(false);
                return;
            }
            catch (ObjectDisposedException)
            {
                await SettleCancelledAsync(delivery).ConfigureAwait(false);
                return;
            }

            try
            {
                DispatchAcquired?.TrySetResult();
                if (DispatchAcquireHold is not null)
                {
                    try
                    {
                        await DispatchAcquireHold.Task.WaitAsync(_queueCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        await SettleCancelledAsync(delivery).ConfigureAwait(false);
                        return;
                    }
                }

                Interlocked.Increment(ref _active);
                try
                {
                    await ProcessCurrentAsync(delivery, _workCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _active);
                }
            }
            finally
            {
                try
                {
                    _ = _dispatch.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
        finally
        {
            ReleaseAdmission();
        }
    }

    private async Task ProcessCurrentAsync(BackFillerRabbitMqConsumedDelivery delivery, CancellationToken cancellationToken)
    {
        IBackFillerRabbitMqChannel? channel;
        lock (_gate)
        {
            channel = _channel;
        }

        if (channel is null || !channel.IsOpen || channel.Generation != delivery.Generation)
        {
            ArticleWorkLogMessages.StaleSettlementSkipped(_logger, _backbone, delivery.Generation, delivery.DeliveryTag);
            return;
        }

        bool ChannelStillCurrent() =>
            _connections.TryGetCurrent(out var handle)
            && handle.Generation == delivery.Generation
            && handle.IsCurrent
            && ReferenceEquals(_channel, channel)
            && channel.IsOpen;

        var outcome = await _pipeline
            .ProcessAsync(delivery, _backbone, channel, ChannelStillCurrent, cancellationToken)
            .ConfigureAwait(false);
        if (outcome == ArticleWorkOutcome.InvalidRequest)
        {
            var parsed = ArticleWorkRequestParser.Parse(
                delivery,
                _backbone,
                int.MaxValue);
            ArticleWorkLogMessages.RequestRejected(
                _logger,
                _backbone,
                delivery.Generation,
                delivery.DeliveryTag,
                parsed.Failure?.Reason ?? "InvalidRequest");
        }
    }

    private Task SettleCancelledAsync(BackFillerRabbitMqConsumedDelivery delivery) =>
        ProcessCurrentAsync(delivery, new CancellationToken(canceled: true));

    private bool TryAdmit()
    {
        lock (_gate)
        {
            if (_state != ArticleWorkConsumerState.Running)
            {
                return false;
            }

            _inFlight++;
            return true;
        }
    }

    private void ReleaseAdmission()
    {
        lock (_gate)
        {
            _inFlight--;
            if (_inFlight <= 0 && _state == ArticleWorkConsumerState.Retiring)
            {
                _drained.TrySetResult();
            }
        }
    }

    private Task WaitForDrainAsync()
    {
        lock (_gate)
        {
            if (_inFlight <= 0)
            {
                return Task.CompletedTask;
            }

            return _drained.Task;
        }
    }

    private static TaskCompletionSource NewDrainSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
