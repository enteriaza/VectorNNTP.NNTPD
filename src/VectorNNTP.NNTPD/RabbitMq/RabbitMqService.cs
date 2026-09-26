using System.Diagnostics;
using Microsoft.Extensions.Options;
using RabbitMQ.Client.Events;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;

namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>
/// Dedicated RabbitMQ infrastructure service: owns the process-wide broker connection,
/// connection generation, application-level replacement, and shutdown.
/// </summary>
/// <remarks>
/// <para>
/// RabbitMQ is a critical NNTPD dependency. Startup fails if an initial usable connection
/// cannot be established. Client automatic recovery is disabled; this service replaces the
/// connection and increments the generation on loss.
/// </para>
/// <para>
/// This service does not declare topology, create channels, publish, consume, or process messages.
/// </para>
/// </remarks>
public sealed class RabbitMqService : IRabbitMqService, IApplicationService, IAsyncDisposable
{
    private readonly IRabbitMqBrokerConnector _connector;
    private readonly IOptions<RabbitMqOptions> _options;
    private readonly IOptions<NntpdOptions> _nntpdOptions;
    private readonly ILogger<RabbitMqService> _logger;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _recoverySignal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _shutdownCts = new();

    private RabbitMqRuntimeOptions? _runtimeOptions;
    private string _connectionName = string.Empty;
    private IRabbitMqBrokerConnection? _connection;
    private Task? _recoveryTask;
    private volatile RabbitMqInfrastructureState _state = RabbitMqInfrastructureState.NotInitialized;
    private volatile bool _disposeRequested;
    private int _recoveryQueued;
    private int _recoveryAttempt;
    private int _consecutiveClientRecoveryErrors;
    private long _connectionGeneration;
    private int _started;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="RabbitMqService"/> class.</summary>
    internal RabbitMqService(
        IRabbitMqBrokerConnector connector,
        IOptions<RabbitMqOptions> options,
        IOptions<NntpdOptions> nntpdOptions,
        ILogger<RabbitMqService> logger)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(nntpdOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _connector = connector;
        _options = options;
        _nntpdOptions = nntpdOptions;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "RabbitMQ";

    /// <inheritdoc />
    public Task? Execution => _recoveryTask;

    /// <inheritdoc />
    public bool IsReady => _state is RabbitMqInfrastructureState.Connected;

    /// <inheritdoc />
    public long ConnectionGeneration => Interlocked.Read(ref _connectionGeneration);

    /// <inheritdoc />
    public RabbitMqInfrastructureState State => _state;

    /// <summary>
    /// Raised after a new broker connection generation becomes active.
    /// </summary>
    /// <remarks>
    /// The first successful connect also raises this event with <c>IsReplacement</c> set to <see langword="false"/>.
    /// Later generations set <c>IsReplacement</c> to <see langword="true"/>.
    /// </remarks>
    public event EventHandler<RabbitMqConnectionReplacedEventArgs>? ConnectionReplaced;

    /// <summary>Gets the number of times a usable connection was installed (tests).</summary>
    internal int ConnectionCount { get; private set; }

    /// <summary>
    /// Returns the current open connection for future topology or channel work.
    /// </summary>
    /// <returns>The current broker connection.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no open connection is available.</exception>
    internal IRabbitMqBrokerConnection GetRequiredConnection()
    {
        var connection = _connection
            ?? throw new InvalidOperationException("RabbitMQ connection has not been established.");

        return !connection.IsOpen
            ? throw new InvalidOperationException("RabbitMQ connection is not open.")
            : connection;
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
            _runtimeOptions = _options.Value.ToRuntimeOptions();
            _connectionName = RabbitMqRuntimeOptions.GetDefaultConnectionName(_nntpdOptions.Value.Fqdn);
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RabbitMqLogMessages.StartupFailed(_logger, ex);
            await DisposeConnectionAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _started, 0);
            throw;
        }
        catch (OperationCanceledException)
        {
            await DisposeConnectionAsync().ConfigureAwait(false);
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

        _disposeRequested = true;
        _state = RabbitMqInfrastructureState.Stopping;
        _shutdownCts.Cancel();
        _ = _recoverySignal.Release();

        try
        {
            if (_recoveryTask is not null)
            {
                await _recoveryTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }

        await DisposeConnectionAsync().ConfigureAwait(false);

        _state = RabbitMqInfrastructureState.Stopped;
        RabbitMqLogMessages.ShutdownCompleted(_logger);

        _stateGate.Dispose();
        _recoverySignal.Dispose();
        _shutdownCts.Dispose();
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ThrowIfStopping();

        if (State is RabbitMqInfrastructureState.Connected)
        {
            return;
        }

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopping();
            if (State is RabbitMqInfrastructureState.Connected)
            {
                return;
            }

            await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
            _recoveryTask ??= Task.Run(RecoveryLoopAsync, CancellationToken.None);
        }
        finally
        {
            _ = _stateGate.Release();
        }
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        var runtimeOptions = _runtimeOptions
            ?? throw new InvalidOperationException("RabbitMQ runtime options have not been projected.");
        var stopwatch = Stopwatch.StartNew();

        _state = RabbitMqInfrastructureState.Connecting;
        var snapshot = RabbitMqConnectionFactoryBuilder.BuildSanitizedSnapshot(runtimeOptions, _connectionName);
        var hosts = string.Join(',', snapshot.Hosts);
        RabbitMqLogMessages.ConnectionAttempt(
            _logger,
            hosts,
            snapshot.Port,
            snapshot.VirtualHost,
            snapshot.ClientProvidedConnectionName,
            snapshot.EnableSsl);

        try
        {
            var connection = await _connector
                .ConnectAsync(runtimeOptions, _connectionName, cancellationToken)
                .ConfigureAwait(false);

            if (!connection.IsOpen)
            {
                RabbitMqLogMessages.ConnectionNotUsable(_logger);
                await connection.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException("RabbitMQ connection opened but is not usable.");
            }

            AttachConnectionEvents(connection);
            _connection = connection;
            _recoveryAttempt = 0;
            _ = Interlocked.Exchange(ref _consecutiveClientRecoveryErrors, 0);
            _state = RabbitMqInfrastructureState.Connected;

            var generation = Interlocked.Increment(ref _connectionGeneration);
            var isReplacement = generation > 1;
            ConnectionCount++;

            RabbitMqLogMessages.ConnectionSucceeded(
                _logger,
                connection.EndpointHostName,
                connection.EndpointPort,
                connection.VirtualHost,
                connection.ClientProvidedName,
                generation,
                stopwatch.Elapsed.TotalMilliseconds);

            ConnectionReplaced?.Invoke(this, new RabbitMqConnectionReplacedEventArgs(generation, isReplacement));
        }
        catch (Exception ex)
        {
            _state = RabbitMqInfrastructureState.Failed;
            RabbitMqLogMessages.ConnectionFailed(
                _logger,
                ex,
                hosts,
                snapshot.Port,
                snapshot.VirtualHost,
                snapshot.ClientProvidedConnectionName,
                stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    private async Task RecoveryLoopAsync()
    {
        while (!_shutdownCts.IsCancellationRequested)
        {
            try
            {
                await _recoverySignal.WaitAsync(_shutdownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_shutdownCts.IsCancellationRequested || _disposeRequested)
            {
                break;
            }

            _ = Interlocked.Exchange(ref _recoveryQueued, 0);

            var acquired = false;
            try
            {
                await _stateGate.WaitAsync(_shutdownCts.Token).ConfigureAwait(false);
                acquired = true;

                if (_shutdownCts.IsCancellationRequested || _disposeRequested)
                {
                    break;
                }

                _state = RabbitMqInfrastructureState.Reconnecting;
                var failureCount = 0;
                var runtimeOptions = _runtimeOptions
                    ?? throw new InvalidOperationException("RabbitMQ runtime options have not been projected.");

                while (!_shutdownCts.IsCancellationRequested && !_disposeRequested)
                {
                    _recoveryAttempt++;
                    var delay = ComputeRecoveryBackoff(runtimeOptions, _recoveryAttempt);
                    RabbitMqLogMessages.RecoveryStarting(_logger, _recoveryAttempt, delay.TotalMilliseconds);

                    try
                    {
                        await Task.Delay(delay, _shutdownCts.Token).ConfigureAwait(false);
                        await DisposeConnectionAsync().ConfigureAwait(false);
                        await ConnectCoreAsync(_shutdownCts.Token).ConfigureAwait(false);

                        RabbitMqLogMessages.RecoverySucceeded(_logger, _recoveryAttempt, ConnectionGeneration);
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        failureCount++;
                        RabbitMqLogMessages.RecoveryFailed(_logger, _recoveryAttempt, failureCount, ex.Message);

                        if (failureCount >= runtimeOptions.MaxConsecutiveRecoveryFailures)
                        {
                            _state = RabbitMqInfrastructureState.Failed;
                            RabbitMqLogMessages.RecoveryFailureThresholdReached(_logger, failureCount);
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            finally
            {
                if (acquired)
                {
                    _ = _stateGate.Release();
                }
            }
        }
    }

    private void QueueRecovery(string reason)
    {
        if (_disposeRequested || _shutdownCts.IsCancellationRequested)
        {
            return;
        }

        if (Interlocked.Exchange(ref _recoveryQueued, 1) == 0)
        {
            RabbitMqLogMessages.RecoveryQueued(_logger, reason);
            _ = _recoverySignal.Release();
        }
    }

    private async Task DisposeConnectionAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is null)
        {
            return;
        }

        DetachConnectionEvents(connection);

        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RabbitMqLogMessages.ConnectionDisposeFailed(_logger, ex);
        }
    }

    private static TimeSpan ComputeRecoveryBackoff(RabbitMqRuntimeOptions options, int attempt)
    {
        var boundedAttempt = Math.Clamp(attempt, 1, 30);
        var exponential = Math.Pow(2, boundedAttempt - 1);
        var delayMs = options.PoolReconnectBaseDelayMs * exponential;
        delayMs = Math.Min(delayMs, options.PoolReconnectMaxDelayMs);
        return TimeSpan.FromMilliseconds(delayMs);
    }

    private void AttachConnectionEvents(IRabbitMqBrokerConnection connection)
    {
        connection.ConnectionShutdown += OnConnectionShutdown;
        connection.CallbackException += OnCallbackException;
        connection.ConnectionBlocked += OnConnectionBlocked;
        connection.ConnectionUnblocked += OnConnectionUnblocked;
        connection.ConnectionRecoveryError += OnConnectionRecoveryError;
        connection.RecoverySucceeded += OnRecoverySucceeded;
    }

    private void DetachConnectionEvents(IRabbitMqBrokerConnection connection)
    {
        connection.ConnectionShutdown -= OnConnectionShutdown;
        connection.CallbackException -= OnCallbackException;
        connection.ConnectionBlocked -= OnConnectionBlocked;
        connection.ConnectionUnblocked -= OnConnectionUnblocked;
        connection.ConnectionRecoveryError -= OnConnectionRecoveryError;
        connection.RecoverySucceeded -= OnRecoverySucceeded;
    }

    private void OnConnectionShutdown(object? sender, ShutdownEventArgs eventArgs)
    {
        RabbitMqLogMessages.ConnectionShutdown(
            _logger,
            eventArgs.ReplyCode,
            eventArgs.ReplyText,
            eventArgs.Initiator.ToString());

        if (_disposeRequested || _shutdownCts.IsCancellationRequested)
        {
            return;
        }

        _state = RabbitMqInfrastructureState.Reconnecting;
        QueueRecovery($"connection-shutdown:{eventArgs.ReplyCode}");
    }

    private void OnCallbackException(object? sender, CallbackExceptionEventArgs eventArgs)
    {
        RabbitMqLogMessages.CallbackException(_logger, eventArgs.Exception.Message);
    }

    private void OnConnectionBlocked(object? sender, ConnectionBlockedEventArgs eventArgs)
    {
        RabbitMqLogMessages.ConnectionBlocked(_logger, eventArgs.Reason);
    }

    private void OnConnectionUnblocked(object? sender, AsyncEventArgs eventArgs)
    {
        RabbitMqLogMessages.ConnectionUnblocked(_logger);
    }

    private void OnConnectionRecoveryError(object? sender, ConnectionRecoveryErrorEventArgs eventArgs)
    {
        var consecutiveErrors = Interlocked.Increment(ref _consecutiveClientRecoveryErrors);
        RabbitMqLogMessages.ClientAutomaticRecoveryError(_logger, consecutiveErrors, eventArgs.Exception.Message);

        var threshold = _runtimeOptions?.MaxConsecutiveRecoveryFailures ?? 5;
        if (consecutiveErrors >= threshold)
        {
            RabbitMqLogMessages.ClientAutomaticRecoveryThresholdReached(_logger, consecutiveErrors);
            QueueRecovery("automatic-recovery-error-threshold");
        }
    }

    private void OnRecoverySucceeded(object? sender, AsyncEventArgs eventArgs)
    {
        _ = Interlocked.Exchange(ref _consecutiveClientRecoveryErrors, 0);
        if (_state is not RabbitMqInfrastructureState.Stopping and not RabbitMqInfrastructureState.Stopped)
        {
            _state = RabbitMqInfrastructureState.Connected;
        }

        RabbitMqLogMessages.ClientAutomaticRecoverySucceeded(_logger);
    }

    private void ThrowIfStopping()
    {
        if (_disposeRequested || _shutdownCts.IsCancellationRequested)
        {
            throw new InvalidOperationException("RabbitMQ connection service is stopping.");
        }
    }
}
