using VectorNNTP.BackFiller.RabbitMq;

namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// One backbone-scoped consumer session that owns a single consume channel.
/// </summary>
public sealed class ArticleWorkConsumerSession : IAsyncDisposable
{
    private readonly string _backbone;
    private readonly string _queue;
    private readonly ushort _prefetch;
    private readonly ArticleWorkDeliveryPipeline _pipeline;
    private readonly IBackFillerRabbitMqService _connections;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _dispatch = new(1, 1);

    private IBackFillerRabbitMqChannel? _channel;
    private string? _consumerTag;
    private int _inFlight;
    private TaskCompletionSource _drained = NewDrainSource();
    private ArticleWorkConsumerState _state = ArticleWorkConsumerState.Created;
    private int _disposed;

    /// <summary>
    /// Initializes a new session.
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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backbone);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(logger);
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
    }

    /// <summary>Gets the backbone context for this session.</summary>
    public string Backbone => _backbone;

    /// <summary>Gets the consume queue name.</summary>
    public string Queue => _queue;

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
    /// Cancels the consumer, waits for admitted work, and disposes the channel.
    /// </summary>
    public async Task RetireAsync()
    {
        IBackFillerRabbitMqChannel? channel;
        string? tag;
        lock (_gate)
        {
            if (_state is ArticleWorkConsumerState.Retiring or ArticleWorkConsumerState.Stopped)
            {
                channel = _channel;
                tag = _consumerTag;
            }
            else
            {
                _state = ArticleWorkConsumerState.Retiring;
                channel = _channel;
                tag = _consumerTag;
            }
        }

        ArticleWorkLogMessages.ConsumerRetiring(_logger, _backbone, Generation);

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

        await WaitForDrainAsync().ConfigureAwait(false);

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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await RetireAsync().ConfigureAwait(false);
        _dispatch.Dispose();
    }

    private async Task OnDeliveryAsync(BackFillerRabbitMqConsumedDelivery delivery)
    {
        if (!TryAdmit())
        {
            return;
        }

        try
        {
            await _dispatch.WaitAsync().ConfigureAwait(false);
            try
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
                    .ProcessAsync(delivery, _backbone, channel, ChannelStillCurrent, CancellationToken.None)
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
            finally
            {
                _ = _dispatch.Release();
            }
        }
        finally
        {
            ReleaseAdmission();
        }
    }

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
