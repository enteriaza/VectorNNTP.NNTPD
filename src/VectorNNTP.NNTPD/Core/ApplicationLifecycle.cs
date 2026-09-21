using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.Core;

/// <summary>
/// Explicit, observable application lifecycle coordinator with validated state transitions.
/// </summary>
/// <remarks>
/// <para>
/// Happy path: <see cref="ApplicationState.Created"/> → <see cref="ApplicationState.Starting"/> →
/// <see cref="ApplicationState.Running"/> → <see cref="ApplicationState.Stopping"/> →
/// <see cref="ApplicationState.Stopped"/>.
/// </para>
/// <para>
/// Startup failure or cancellation: <see cref="ApplicationState.Starting"/> →
/// <see cref="ApplicationState.Stopping"/> → <see cref="ApplicationState.Stopped"/>, then the exception is rethrown.
/// Partially started services are rolled back by <see cref="ApplicationServiceManager"/>.
/// </para>
/// <para>
/// Repeated <see cref="StopAsync"/> calls are safe: subsequent callers await the in-flight stop.
/// Concurrent <see cref="StartAsync"/> / <see cref="StopAsync"/> operations are serialized.
/// </para>
/// </remarks>
public sealed class ApplicationLifecycle : IAsyncDisposable
{
    private static readonly HashSet<(ApplicationState From, ApplicationState To)> ValidTransitions =
    [
        (ApplicationState.Created, ApplicationState.Starting),
        (ApplicationState.Created, ApplicationState.Stopped),
        (ApplicationState.Starting, ApplicationState.Running),
        (ApplicationState.Starting, ApplicationState.Stopping),
        (ApplicationState.Running, ApplicationState.Stopping),
        (ApplicationState.Stopping, ApplicationState.Stopped),
    ];

    private readonly ApplicationServiceManager _serviceManager;
    private readonly IOptions<NntpdOptions> _options;
    private readonly ILogger<ApplicationLifecycle> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateSync = new();
    private readonly TaskCompletionSource _stoppedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _unexpectedTerminationTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ApplicationState _state = ApplicationState.Created;
    private Task? _stopTask;
    private int _disposed;
    private int _disposeStarted;
    private int _shutdownRequested;
    private readonly TaskCompletionSource _disposeCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Initializes a new instance of the <see cref="ApplicationLifecycle"/> class.
    /// </summary>
    /// <param name="serviceManager">Service manager used for ordered start/stop.</param>
    /// <param name="options">Lifecycle options.</param>
    /// <param name="logger">Logger.</param>
    public ApplicationLifecycle(
        ApplicationServiceManager serviceManager,
        IOptions<NntpdOptions> options,
        ILogger<ApplicationLifecycle> logger)
    {
        ArgumentNullException.ThrowIfNull(serviceManager);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _serviceManager = serviceManager;
        _options = options;
        _logger = logger;

        _serviceManager.UnexpectedServiceTermination += OnUnexpectedServiceTermination;
    }

    /// <summary>
    /// Occurs after a validated lifecycle state transition has been committed.
    /// </summary>
    public event EventHandler<ApplicationStateChangedEventArgs>? StateChanged;

    /// <summary>Gets the current lifecycle state (thread-safe).</summary>
    public ApplicationState State
    {
        get
        {
            lock (_stateSync)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether shutdown has been requested or the lifecycle has left
    /// <see cref="ApplicationState.Running"/> toward stop.
    /// </summary>
    public bool ShutdownRequested => Volatile.Read(ref _shutdownRequested) != 0;

    /// <summary>
    /// Gets a task that completes when an unexpected application-service termination is observed while running.
    /// </summary>
    public Task UnexpectedTermination => _unexpectedTerminationTcs.Task;

    /// <summary>
    /// Starts the application asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel startup.</param>
    /// <returns>A task that completes when the application enters <see cref="ApplicationState.Running"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown for invalid transitions or concurrent unsafe use.</exception>
    /// <exception cref="OperationCanceledException">Thrown when startup is canceled.</exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Transition(ApplicationState.Starting);

            _logger.LogInformation(
                "Application startup initiated for {ApplicationName}. Current state: {State}.",
                _options.Value.ApplicationName,
                State);

            var sw = Stopwatch.StartNew();

            using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_options.Value.StartupTimeout is { } startupTimeout)
            {
                startupCts.CancelAfter(startupTimeout);
            }

            try
            {
                await _serviceManager.StartAsync(startupCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Application startup canceled after {ElapsedMs} ms. Transitioning to shutdown.",
                    sw.ElapsedMilliseconds);
                await FailStartupCleanupAsync().ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (startupCts.IsCancellationRequested)
            {
                _logger.LogError(
                    "Application startup timed out after {Timeout} ({ElapsedMs} ms).",
                    _options.Value.StartupTimeout,
                    sw.ElapsedMilliseconds);
                await FailStartupCleanupAsync().ConfigureAwait(false);
                throw new TimeoutException(
                    $"Application startup timed out after {_options.Value.StartupTimeout}.");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Application startup failed after {ElapsedMs} ms. Rolling back and transitioning to Stopped.",
                    sw.ElapsedMilliseconds);
                await FailStartupCleanupAsync().ConfigureAwait(false);
                throw;
            }

            Transition(ApplicationState.Running);

            _logger.LogInformation(
                "Application initialization completed for {ApplicationName} in {ElapsedMs} ms. State: {State}.",
                _options.Value.ApplicationName,
                sw.ElapsedMilliseconds,
                State);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops the application asynchronously. Repeated calls are safe and await the same shutdown.
    /// </summary>
    /// <param name="cancellationToken">Token that cooperates with the configured graceful shutdown timeout.</param>
    /// <returns>A task that completes when the application enters <see cref="ApplicationState.Stopped"/>.</returns>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_stateSync)
        {
            // Idempotent when already stopped, including during/after disposal of a completed lifecycle.
            if (_state == ApplicationState.Stopped)
            {
                return Task.CompletedTask;
            }

            ObjectDisposedException.ThrowIf(_disposed != 0, this);

            if (_state == ApplicationState.Created)
            {
                // Never started: move directly to Stopped.
                Interlocked.Exchange(ref _shutdownRequested, 1);
                var from = _state;
                _state = ApplicationState.Stopped;
                _logger.LogInformation(
                    "Application stop requested before startup. State transition: {From} -> {To}.",
                    ApplicationState.Created,
                    ApplicationState.Stopped);
                _stoppedTcs.TrySetResult();
                RaiseStateChanged(from, ApplicationState.Stopped);
                return Task.CompletedTask;
            }

            if (_stopTask is not null)
            {
                return _stopTask;
            }

            Interlocked.Exchange(ref _shutdownRequested, 1);
            _stopTask = StopCoreAsync(cancellationToken);
            return _stopTask;
        }
    }

    /// <summary>
    /// Waits until shutdown completes or an unexpected termination is observed.
    /// </summary>
    /// <param name="cancellationToken">Token canceled when the host requests shutdown.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when an application service terminates unexpectedly while <see cref="ApplicationState.Running"/>.
    /// </exception>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        var shutdown = Task.Delay(Timeout.Infinite, cancellationToken);
        var completed = await Task.WhenAny(UnexpectedTermination, _stoppedTcs.Task, shutdown)
            .ConfigureAwait(false);

        if (completed == UnexpectedTermination)
        {
            await UnexpectedTermination.ConfigureAwait(false);
            throw new InvalidOperationException(
                "An application service terminated unexpectedly while the application was Running.");
        }

        if (completed == _stoppedTcs.Task)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Shutdown is completed before the instance is marked disposed so that a normal host-driven
    /// stop followed by DI disposal does not observe <see cref="ObjectDisposedException"/>.
    /// Repeated disposal is safe and awaits any in-flight stop.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        // Serialize dispose entry: the first caller performs shutdown + resource release.
        if (Interlocked.CompareExchange(ref _disposeStarted, 1, 0) != 0)
        {
            await AwaitExistingStopAsync().ConfigureAwait(false);
            return;
        }

        _serviceManager.UnexpectedServiceTermination -= OnUnexpectedServiceTermination;

        Exception? shutdownFailure = null;
        try
        {
            // Complete graceful stop before marking disposed.
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            shutdownFailure = ex;
            _logger.LogError(ex, "ApplicationLifecycle disposal encountered a shutdown failure.");
        }
        finally
        {
            Interlocked.Exchange(ref _disposed, 1);

            try
            {
                _gate.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Gate may already be disposed by a concurrent path; ignore.
            }

            if (shutdownFailure is null)
            {
                _disposeCompleted.TrySetResult();
            }
            else
            {
                _disposeCompleted.TrySetException(shutdownFailure);
            }
        }

        // Surface genuine shutdown failures to DisposeAsync callers / DI host disposal.
        if (shutdownFailure is not null)
        {
            throw shutdownFailure;
        }
    }

    private async Task AwaitExistingStopAsync()
    {
        Task? stop;
        lock (_stateSync)
        {
            stop = _stopTask;
        }

        if (stop is not null)
        {
            try
            {
                await stop.ConfigureAwait(false);
            }
            catch
            {
                // The original stop/dispose caller observes the failure; subsequent dispose awaits are idempotent.
            }
        }

        try
        {
            await _disposeCompleted.Task.ConfigureAwait(false);
        }
        catch
        {
            // Idempotent subsequent dispose: do not rethrow the original failure.
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var current = State;
            if (current == ApplicationState.Stopped)
            {
                return;
            }

            if (current is not ApplicationState.Stopping)
            {
                Transition(ApplicationState.Stopping);
            }

            _logger.LogInformation(
                "Application shutdown initiated for {ApplicationName}. State: {State}. Timeout: {Timeout}.",
                _options.Value.ApplicationName,
                State,
                _options.Value.GracefulShutdownTimeout);

            var sw = Stopwatch.StartNew();

            try
            {
                await _serviceManager.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                _logger.LogError(
                    ex,
                    "Application shutdown timed out after {ElapsedMs} ms.",
                    sw.ElapsedMilliseconds);
                Transition(ApplicationState.Stopped);
                _stoppedTcs.TrySetResult();
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Application shutdown failed after {ElapsedMs} ms. Forcing Stopped state.",
                    sw.ElapsedMilliseconds);
                Transition(ApplicationState.Stopped);
                _stoppedTcs.TrySetResult();
                throw;
            }

            Transition(ApplicationState.Stopped);

            _logger.LogInformation(
                "Application shutdown completed for {ApplicationName} in {ElapsedMs} ms. State: {State}.",
                _options.Value.ApplicationName,
                sw.ElapsedMilliseconds,
                State);

            _stoppedTcs.TrySetResult();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task FailStartupCleanupAsync()
    {
        // Services should already be rolled back by the manager; ensure lifecycle ends in Stopped.
        if (State == ApplicationState.Starting)
        {
            Transition(ApplicationState.Stopping);
        }

        if (State == ApplicationState.Stopping)
        {
            Transition(ApplicationState.Stopped);
        }

        _stoppedTcs.TrySetResult();

        // Ensure any residual started services are cleared (best effort; manager already rolled back).
        try
        {
            if (_serviceManager.StartedServices.Count > 0)
            {
                await _serviceManager.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Additional cleanup after startup failure encountered an error.");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void Transition(ApplicationState to)
    {
        ApplicationState from;
        lock (_stateSync)
        {
            from = _state;
            if (from == to)
            {
                return;
            }

            if (!ValidTransitions.Contains((from, to)))
            {
                throw new InvalidOperationException(
                    $"Invalid application lifecycle transition from {from} to {to}.");
            }

            if (to is ApplicationState.Stopping or ApplicationState.Stopped)
            {
                Interlocked.Exchange(ref _shutdownRequested, 1);
            }

            _state = to;
            _logger.LogInformation(
                "Application lifecycle state transition: {FromState} -> {ToState}.",
                from,
                to);
        }

        RaiseStateChanged(from, to);
    }

    private void RaiseStateChanged(ApplicationState from, ApplicationState to)
    {
        try
        {
            StateChanged?.Invoke(this, new ApplicationStateChangedEventArgs(from, to));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "An ApplicationLifecycle.StateChanged handler failed for transition {FromState} -> {ToState}.",
                from,
                to);
        }
    }

    private void OnUnexpectedServiceTermination(object? sender, UnexpectedServiceTerminationEventArgs e)
    {
        if (State != ApplicationState.Running)
        {
            return;
        }

        _logger.LogCritical(
            e.Exception,
            "Unexpected termination of application service {ServiceName} while Running (completedNormally={CompletedNormally}).",
            e.ServiceName,
            e.CompletedNormally);

        _unexpectedTerminationTcs.TrySetResult();
    }
}

/// <summary>
/// Provides data for <see cref="ApplicationLifecycle.StateChanged"/>.
/// </summary>
public sealed class ApplicationStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ApplicationStateChangedEventArgs"/> class.
    /// </summary>
    public ApplicationStateChangedEventArgs(ApplicationState fromState, ApplicationState toState)
    {
        FromState = fromState;
        ToState = toState;
    }

    /// <summary>Gets the previous lifecycle state.</summary>
    public ApplicationState FromState { get; }

    /// <summary>Gets the new lifecycle state.</summary>
    public ApplicationState ToState { get; }
}
