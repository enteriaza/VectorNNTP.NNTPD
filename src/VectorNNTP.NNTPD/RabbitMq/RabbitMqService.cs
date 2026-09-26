using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;

namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>
/// Process-wide RabbitMQ connection owner: startup connect, generation-tagged replacement, and shutdown.
/// </summary>
/// <remarks>
/// <para>
/// RabbitMQ is a critical NNTPD dependency. <see cref="StartAsync"/> fails if an initial usable
/// connection cannot be established, and it disposes any partial connect before returning.
/// RabbitMQ.Client automatic recovery is disabled; this service is the only lifecycle owner.
/// </para>
/// <para>
/// One watch task observes the current generation. A lost connection requests recovery; a
/// stale generation cannot install state, trigger reconnect, or dispose a newer connection.
/// Shutdown cancels that task, waits for it, and disposes the current connection once.
/// </para>
/// <para>
/// This service does not declare topology, create channels, publish, consume, or process messages.
/// </para>
/// </remarks>
public sealed class RabbitMqService : IRabbitMqService, IApplicationService, IAsyncDisposable
{
    private readonly IRabbitMqConnectionFactory _connectionFactory;
    private readonly IOptions<RabbitMqOptions> _options;
    private readonly IOptions<NntpdOptions> _nntpdOptions;
    private readonly ILogger<RabbitMqService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _runCts = new();
    private readonly object _gate = new();

    private TaskCompletionSource _recoveryRequested = NewRecoverySource();
    private RabbitMqRuntimeOptions? _runtime;
    private string _connectionName = string.Empty;
    private LiveConnection? _current;
    private Task? _execution;
    private long _generation;
    private int _started;
    private int _disposed;
    private bool _stopping;

    /// <summary>Initializes a new instance of the <see cref="RabbitMqService"/> class.</summary>
    public RabbitMqService(
        IRabbitMqConnectionFactory connectionFactory,
        IOptions<RabbitMqOptions> options,
        IOptions<NntpdOptions> nntpdOptions,
        ILogger<RabbitMqService> logger)
        : this(connectionFactory, options, nntpdOptions, logger, TimeProvider.System)
    {
    }

    /// <summary>Initializes a new instance with an explicit clock (tests).</summary>
    internal RabbitMqService(
        IRabbitMqConnectionFactory connectionFactory,
        IOptions<RabbitMqOptions> options,
        IOptions<NntpdOptions> nntpdOptions,
        ILogger<RabbitMqService> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(nntpdOptions);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _connectionFactory = connectionFactory;
        _options = options;
        _nntpdOptions = nntpdOptions;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public string Name => "RabbitMQ";

    /// <inheritdoc />
    public Task? Execution => _execution;

    /// <inheritdoc />
    public bool IsReady
    {
        get
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return false;
            }

            var current = Volatile.Read(ref _current);
            return current is { Connection.IsOpen: true };
        }
    }

    /// <inheritdoc />
    public long ConnectionGeneration => Volatile.Read(ref _generation);

    /// <inheritdoc />
    public event EventHandler<RabbitMqConnectionReplacedEventArgs>? ConnectionReplaced;

    /// <summary>Gets the number of times a usable connection was installed (tests).</summary>
    internal int ConnectionCount { get; private set; }

    /// <summary>
    /// Returns the current open connection for later topology or channel work.
    /// </summary>
    /// <returns>The current broker connection.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no open connection is available.</exception>
    internal IRabbitMqConnection GetRequiredConnection()
    {
        var current = Volatile.Read(ref _current)
            ?? throw new InvalidOperationException("RabbitMQ connection has not been established.");

        return !current.Connection.IsOpen
            ? throw new InvalidOperationException("RabbitMQ connection is not open.")
            : current.Connection;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        try
        {
            _runtime = _options.Value.ToRuntimeOptions();
            _connectionName = RabbitMqRuntimeOptions.GetDefaultConnectionName(_nntpdOptions.Value.Fqdn);
            await ConnectAndInstallAsync(cancellationToken, startup: true).ConfigureAwait(false);
            _execution = WatchConnectionAsync(_runCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RabbitMqLogMessages.StartupFailed(_logger, ex);
            await RetireCurrentAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _started, 0);
            throw;
        }
        catch (OperationCanceledException)
        {
            await RetireCurrentAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _started, 0);
            throw;
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
        }

        await _runCts.CancelAsync().ConfigureAwait(false);

        var execution = _execution;
        if (execution is not null)
        {
            try
            {
                await execution.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await RetireCurrentAsync().ConfigureAwait(false);
        RabbitMqLogMessages.Stopped(_logger);
        _runCts.Dispose();
    }

    private async Task WatchConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await WaitForRecoveryRequestAsync(cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await RecoverAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        var runtime = _runtime
            ?? throw new InvalidOperationException("RabbitMQ runtime options have not been projected.");

        var consecutiveFailures = 0;
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            attempt++;
            var delay = ComputeReconnectBackoff(runtime, attempt);
            RabbitMqLogMessages.ReconnectStarting(
                _logger,
                attempt,
                delay.TotalMilliseconds,
                ConnectionGeneration);

            try
            {
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                await RetireCurrentAsync().ConfigureAwait(false);
                var generation = await ConnectAndInstallAsync(cancellationToken, startup: false)
                    .ConfigureAwait(false);
                RabbitMqLogMessages.ReconnectSucceeded(_logger, attempt, generation);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                RabbitMqLogMessages.ReconnectFailed(_logger, attempt, consecutiveFailures, ex.Message);
                if (consecutiveFailures >= runtime.MaxConsecutiveRecoveryFailures)
                {
                    RabbitMqLogMessages.ReconnectAbandoned(_logger, consecutiveFailures);
                    return;
                }
            }
        }
    }

    private async Task<long> ConnectAndInstallAsync(CancellationToken cancellationToken, bool startup)
    {
        ThrowIfStopping();

        var runtime = _runtime
            ?? throw new InvalidOperationException("RabbitMQ runtime options have not been projected.");
        var hosts = string.Join(',', runtime.Hosts);
        var started = _timeProvider.GetTimestamp();

        RabbitMqLogMessages.Connecting(
            _logger,
            hosts,
            runtime.Port,
            runtime.VirtualHost,
            _connectionName,
            runtime.EnableSsl);

        IRabbitMqConnection? connection = null;
        var installed = false;
        try
        {
            connection = await _connectionFactory
                .ConnectAsync(_options.Value, _connectionName, cancellationToken)
                .ConfigureAwait(false);

            if (!connection.IsOpen)
            {
                RabbitMqLogMessages.ConnectionNotUsable(_logger);
                await DisposeConnectionQuietlyAsync(connection).ConfigureAwait(false);
                connection = null;
                throw new InvalidOperationException("RabbitMQ connection opened but is not usable.");
            }

            var live = Publish(connection);
            installed = true;
            connection = null;

            var previous = live.Replaced;
            if (previous is not null)
            {
                previous.Connection.ConnectionLost -= OnConnectionLost;
                await DisposeConnectionQuietlyAsync(previous.Connection).ConfigureAwait(false);
            }

            var elapsedMs = _timeProvider.GetElapsedTime(started).TotalMilliseconds;
            RabbitMqLogMessages.Connected(
                _logger,
                live.Current.Connection.Host,
                live.Current.Connection.Port,
                live.Current.Connection.VirtualHost,
                live.Current.Connection.ClientProvidedName,
                live.Current.Generation,
                elapsedMs);

            ConnectionReplaced?.Invoke(
                this,
                new RabbitMqConnectionReplacedEventArgs(
                    live.Current.Generation,
                    live.Current.Generation > 1));

            return live.Current.Generation;
        }
        catch (Exception ex)
        {
            if (!installed && connection is not null)
            {
                await DisposeConnectionQuietlyAsync(connection).ConfigureAwait(false);
            }

            if (startup && ex is not OperationCanceledException)
            {
                RabbitMqLogMessages.ConnectionFailed(
                    _logger,
                    ex,
                    hosts,
                    runtime.Port,
                    runtime.VirtualHost,
                    _connectionName,
                    _timeProvider.GetElapsedTime(started).TotalMilliseconds);
            }

            throw;
        }
    }

    private PublishedConnection Publish(IRabbitMqConnection connection)
    {
        // Subscribe before making the instance current so a close that races the
        // publish is observed. OnConnectionLost still ignores a sender that is not current.
        connection.ConnectionLost += OnConnectionLost;

        lock (_gate)
        {
            if (_stopping)
            {
                connection.ConnectionLost -= OnConnectionLost;
                throw new InvalidOperationException("RabbitMQ service is stopping.");
            }

            var previous = _current;
            _generation++;
            var current = new LiveConnection(connection, _generation);
            _current = current;
            ConnectionCount++;
            return new PublishedConnection(current, previous);
        }
    }

    private void OnConnectionLost(object? sender, RabbitMqConnectionLostEventArgs eventArgs)
    {
        long generation;
        lock (_gate)
        {
            // A retired connection must not request recovery or overwrite a newer generation.
            if (_stopping
                || _current is not { } current
                || !ReferenceEquals(current.Connection, sender))
            {
                return;
            }

            generation = current.Generation;
        }

        RabbitMqLogMessages.ConnectionLost(
            _logger,
            generation,
            eventArgs.ReplyCode,
            eventArgs.ReplyText,
            eventArgs.Initiator);
        RequestRecovery();
    }

    private void RequestRecovery()
    {
        TaskCompletionSource source;
        lock (_gate)
        {
            if (_stopping)
            {
                return;
            }

            source = _recoveryRequested;
        }

        source.TrySetResult();
    }

    private async Task WaitForRecoveryRequestAsync(CancellationToken cancellationToken)
    {
        Task wait;
        lock (_gate)
        {
            wait = _recoveryRequested.Task;
        }

        await wait.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Replace the consumed signal before RecoverAsync runs so a loss of the
        // newly installed generation is not dropped as an already-observed event.
        lock (_gate)
        {
            if (_recoveryRequested.Task.IsCompleted)
            {
                _recoveryRequested = NewRecoverySource();
            }
        }
    }

    private async Task RetireCurrentAsync()
    {
        LiveConnection? current;
        lock (_gate)
        {
            current = _current;
            _current = null;
        }

        if (current is null)
        {
            return;
        }

        current.Connection.ConnectionLost -= OnConnectionLost;
        await DisposeConnectionQuietlyAsync(current.Connection).ConfigureAwait(false);
    }

    private async Task DisposeConnectionQuietlyAsync(IRabbitMqConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RabbitMqLogMessages.ConnectionDisposeFailed(_logger, ex);
        }
    }

    private void ThrowIfStopping()
    {
        lock (_gate)
        {
            if (_stopping)
            {
                throw new InvalidOperationException("RabbitMQ service is stopping.");
            }
        }
    }

    private static TimeSpan ComputeReconnectBackoff(RabbitMqRuntimeOptions options, int attempt)
    {
        var boundedAttempt = Math.Clamp(attempt, 1, 30);
        var exponential = Math.Pow(2, boundedAttempt - 1);
        var delayMs = options.PoolReconnectBaseDelayMs * exponential;
        delayMs = Math.Min(delayMs, options.PoolReconnectMaxDelayMs);
        return TimeSpan.FromMilliseconds(delayMs);
    }

    private static TaskCompletionSource NewRecoverySource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class LiveConnection(IRabbitMqConnection connection, long generation)
    {
        public IRabbitMqConnection Connection { get; } = connection;

        public long Generation { get; } = generation;
    }

    private readonly record struct PublishedConnection(LiveConnection Current, LiveConnection? Replaced);
}
