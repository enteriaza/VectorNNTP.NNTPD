using VectorNNTP.BackFiller.Configuration;

namespace VectorNNTP.BackFiller.RabbitMq;

/// <summary>
/// Process-wide RabbitMQ connection owner: startup connect, generation-tagged replacement, and shutdown.
/// </summary>
/// <remarks>
/// <para>
/// RabbitMQ is a startup invariant. <see cref="StartAsync"/> fails if an initial usable
/// connection cannot be established, and it disposes any partial connect before returning.
/// After start, a lost connection is not terminal: one watch task reconnects with backoff until
/// a new generation is installed or shutdown cancels the loop. RabbitMQ.Client automatic recovery
/// is disabled; this service is the only lifecycle owner.
/// </para>
/// <para>
/// Callers obtain a generation snapshot with <see cref="TryGetCurrent"/>. A handle does not
/// own, pin, or dispose the connection. Only the current generation may publish readiness,
/// request recovery, or be retired as current.
/// </para>
/// <para>
/// This service does not declare topology, publish, consume, or process Article Work messages.
/// </para>
/// </remarks>
public sealed class BackFillerRabbitMqService : IBackFillerRabbitMqService, IHostedService, IAsyncDisposable
{
    private readonly IBackFillerRabbitMqConnectionFactory _connectionFactory;
    private readonly BackFillerRuntimeOptions _runtimeOptions;
    private readonly ILogger<BackFillerRabbitMqService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _runCts = new();
    private readonly object _gate = new();
    private readonly string _connectionName;

    private TaskCompletionSource _recoveryRequested = NewRecoverySource();
    private LiveConnection? _current;
    private Task? _execution;
    private long _generation;
    private int _started;
    private int _disposed;
    private bool _stopping;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackFillerRabbitMqService"/> class.
    /// </summary>
    /// <param name="connectionFactory">Factory that opens broker connections.</param>
    /// <param name="runtimeOptions">Validated immutable runtime snapshot.</param>
    /// <param name="logger">Lifecycle logger. Must never receive credentials.</param>
    internal BackFillerRabbitMqService(
        IBackFillerRabbitMqConnectionFactory connectionFactory,
        BackFillerRuntimeOptions runtimeOptions,
        ILogger<BackFillerRabbitMqService> logger)
        : this(connectionFactory, runtimeOptions, logger, TimeProvider.System)
    {
    }

    /// <summary>Initializes a new instance with an explicit clock (tests).</summary>
    internal BackFillerRabbitMqService(
        IBackFillerRabbitMqConnectionFactory connectionFactory,
        BackFillerRuntimeOptions runtimeOptions,
        ILogger<BackFillerRabbitMqService> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(runtimeOptions);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _connectionFactory = connectionFactory;
        _runtimeOptions = runtimeOptions;
        _logger = logger;
        _timeProvider = timeProvider;
        _connectionName = $"VectorNNTP.BackFiller:{runtimeOptions.Fqdn}";
    }

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
    public event EventHandler<BackFillerRabbitMqConnectionReplacedEventArgs>? ConnectionReplaced;

    /// <summary>Gets the number of times a usable connection was installed (tests).</summary>
    internal int ConnectionCount { get; private set; }

    /// <summary>Gets the observed recovery watch task (tests).</summary>
    internal Task? Execution => _execution;

    /// <inheritdoc />
    public bool TryGetCurrent(out BackFillerRabbitMqConnectionHandle handle)
    {
        lock (_gate)
        {
            if (_stopping
                || Volatile.Read(ref _disposed) == 1
                || _current is not { } current
                || !current.Connection.IsOpen)
            {
                handle = default;
                return false;
            }

            handle = new BackFillerRabbitMqConnectionHandle(this, current.Connection, current.Generation);
            return true;
        }
    }

    /// <summary>
    /// Point-in-time check: <paramref name="handle"/> still names the published open generation.
    /// </summary>
    internal bool IsHandleCurrent(in BackFillerRabbitMqConnectionHandle handle)
    {
        lock (_gate)
        {
            return !_stopping
                && Volatile.Read(ref _disposed) == 0
                && _current is { } current
                && current.Generation == handle.Generation
                && handle.RefersTo(current.Connection)
                && current.Connection.IsOpen;
        }
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
            await ConnectAndInstallAsync(cancellationToken, startup: true, logConnect: true).ConfigureAwait(false);
            _execution = WatchConnectionAsync(_runCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            BackFillerRabbitMqLogMessages.StartupFailed(_logger, Sanitize(ex.Message));
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
        BackFillerRabbitMqLogMessages.Stopped(_logger);
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
        var rabbit = _runtimeOptions.RabbitMq;
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            attempt++;
            var delay = ComputeReconnectBackoff(rabbit, attempt);
            var announce = ShouldAnnounceReconnect(rabbit, attempt, delay);
            if (announce)
            {
                BackFillerRabbitMqLogMessages.ReconnectStarting(
                    _logger,
                    attempt,
                    delay.TotalMilliseconds,
                    ConnectionGeneration);
            }

            try
            {
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                await RetireCurrentAsync().ConfigureAwait(false);
                var generation = await ConnectAndInstallAsync(cancellationToken, startup: false, logConnect: announce)
                    .ConfigureAwait(false);
                BackFillerRabbitMqLogMessages.ReconnectSucceeded(_logger, attempt, generation);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var reason = Sanitize(ex.Message);
                if (attempt == 1)
                {
                    BackFillerRabbitMqLogMessages.ReconnectFailed(_logger, attempt, reason);
                }
                else if (announce)
                {
                    BackFillerRabbitMqLogMessages.ReconnectStillFailing(_logger, attempt, reason);
                }
            }
        }
    }

    private async Task<long> ConnectAndInstallAsync(
        CancellationToken cancellationToken,
        bool startup,
        bool logConnect)
    {
        ThrowIfStopping();

        var rabbit = _runtimeOptions.RabbitMq;
        var hosts = string.Join(',', rabbit.Hosts);
        var started = _timeProvider.GetTimestamp();

        if (logConnect)
        {
            BackFillerRabbitMqLogMessages.Connecting(
                _logger,
                hosts,
                rabbit.Port,
                rabbit.VirtualHost,
                _connectionName,
                rabbit.EnableSsl);
        }

        IBackFillerRabbitMqConnection? connection = null;
        var installed = false;
        try
        {
            connection = await _connectionFactory
                .ConnectAsync(rabbit, _connectionName, cancellationToken)
                .ConfigureAwait(false);

            if (!connection.IsOpen)
            {
                BackFillerRabbitMqLogMessages.ConnectionNotUsable(_logger);
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
            BackFillerRabbitMqLogMessages.Connected(
                _logger,
                live.Current.Connection.Host,
                live.Current.Connection.Port,
                live.Current.Connection.VirtualHost,
                live.Current.Connection.ClientProvidedName,
                live.Current.Generation,
                elapsedMs);

            ConnectionReplaced?.Invoke(
                this,
                new BackFillerRabbitMqConnectionReplacedEventArgs(
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
                BackFillerRabbitMqLogMessages.ConnectionFailed(
                    _logger,
                    hosts,
                    rabbit.Port,
                    rabbit.VirtualHost,
                    _connectionName,
                    _timeProvider.GetElapsedTime(started).TotalMilliseconds,
                    Sanitize(ex.Message));
            }

            throw;
        }
    }

    private PublishedConnection Publish(IBackFillerRabbitMqConnection connection)
    {
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

    private void OnConnectionLost(object? sender, BackFillerRabbitMqConnectionLostEventArgs eventArgs)
    {
        long generation;
        lock (_gate)
        {
            if (_stopping
                || _current is not { } current
                || !ReferenceEquals(current.Connection, sender))
            {
                if (!_stopping && sender is IBackFillerRabbitMqConnection)
                {
                    BackFillerRabbitMqLogMessages.StaleGenerationIgnored(_logger, _generation);
                }

                return;
            }

            generation = current.Generation;
        }

        BackFillerRabbitMqLogMessages.ConnectionLost(
            _logger,
            generation,
            eventArgs.ReplyCode,
            Sanitize(eventArgs.ReplyText),
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

    private async Task DisposeConnectionQuietlyAsync(IBackFillerRabbitMqConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BackFillerRabbitMqLogMessages.ConnectionDisposeFailed(_logger, Sanitize(ex.Message));
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

    private string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text;
        var password = _runtimeOptions.RabbitMq.Password;
        if (!string.IsNullOrEmpty(password))
        {
            result = result.Replace(password, "***", StringComparison.Ordinal);
        }

        return result;
    }

    private static bool ShouldAnnounceReconnect(
        BackFillerRabbitMqRuntimeOptions options,
        int attempt,
        TimeSpan delay) =>
        attempt == 1 || delay.TotalMilliseconds >= options.PoolReconnectMaxDelayMs;

    private static TimeSpan ComputeReconnectBackoff(BackFillerRabbitMqRuntimeOptions options, int attempt)
    {
        var boundedAttempt = Math.Clamp(attempt, 1, 30);
        var exponential = Math.Pow(2, boundedAttempt - 1);
        var delayMs = options.PoolReconnectBaseDelayMs * exponential;
        delayMs = Math.Min(delayMs, options.PoolReconnectMaxDelayMs);
        return TimeSpan.FromMilliseconds(delayMs);
    }

    private static TaskCompletionSource NewRecoverySource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class LiveConnection(IBackFillerRabbitMqConnection connection, long generation)
    {
        public IBackFillerRabbitMqConnection Connection { get; } = connection;

        public long Generation { get; } = generation;
    }

    private readonly record struct PublishedConnection(LiveConnection Current, LiveConnection? Replaced);
}
