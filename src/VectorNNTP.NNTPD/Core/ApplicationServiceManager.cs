using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.Core;

/// <summary>
/// Coordinates deterministic start/stop of registered <see cref="IApplicationService"/> instances.
/// </summary>
/// <remarks>
/// Services start in registration order and stop in reverse order. On startup failure, already-started
/// services are stopped (rollback). Concurrent lifecycle execution is rejected.
/// </remarks>
public sealed class ApplicationServiceManager
{
    private readonly IReadOnlyList<IApplicationService> _services;
    private readonly IOptions<NntpdOptions> _options;
    private readonly ILogger<ApplicationServiceManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<IApplicationService> _started = new();
    private readonly object _startedSync = new();
    private int _lifecycleBusy;
    private CancellationTokenSource? _executionCts;
    private Task? _executionMonitor;

    /// <summary>
    /// Occurs when a started service's <see cref="IApplicationService.Execution"/> faulted or completed unexpectedly.
    /// </summary>
    public event EventHandler<UnexpectedServiceTerminationEventArgs>? UnexpectedServiceTermination;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApplicationServiceManager"/> class.
    /// </summary>
    /// <param name="services">Registered application services in startup order.</param>
    /// <param name="options">Hosting and lifecycle options.</param>
    /// <param name="logger">Logger.</param>
    public ApplicationServiceManager(
        IEnumerable<IApplicationService> services,
        IOptions<NntpdOptions> options,
        ILogger<ApplicationServiceManager> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _services = services as IReadOnlyList<IApplicationService> ?? services.ToList();
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Gets the services that completed <see cref="StartAsync"/> successfully and have not yet finished a
    /// stop attempt (success, cooperative cancel/timeout, or exception).
    /// </summary>
    /// <remarks>
    /// Removal from this list means the manager has finished awaiting that service's
    /// <see cref="IApplicationService.StopAsync"/> call for the current shutdown. It does not imply
    /// that a non-cooperative service released all resources if the process is later killed by the host.
    /// </remarks>
    public IReadOnlyList<IApplicationService> StartedServices
    {
        get
        {
            lock (_startedSync)
            {
                return _started.ToArray();
            }
        }
    }

    /// <summary>
    /// Starts all registered services in deterministic registration order.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel startup.</param>
    /// <returns>A task that completes when all services have started.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a lifecycle operation is already in progress.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _lifecycleBusy, 1, 0) != 0)
        {
            throw new InvalidOperationException("An application service lifecycle operation is already in progress.");
        }

        var acquired = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;

            lock (_startedSync)
            {
                if (_started.Count > 0)
                {
                    throw new InvalidOperationException("Application services have already been started.");
                }
            }

            _logger.LogInformation(
                "Starting {ServiceCount} application service(s) in registration order.",
                _services.Count);

            for (var i = 0; i < _services.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var service = _services[i];
                var sw = Stopwatch.StartNew();

                _logger.LogInformation(
                    "Starting application service {ServiceName} ({Index}/{Total}).",
                    service.Name,
                    i + 1,
                    _services.Count);

                try
                {
                    await service.StartAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "Startup of application service {ServiceName} was canceled after {ElapsedMs} ms. Rolling back {StartedCount} started service(s).",
                        service.Name,
                        sw.ElapsedMilliseconds,
                        StartedServices.Count);

                    await RollbackStartedAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Application service {ServiceName} failed during startup after {ElapsedMs} ms. Rolling back {StartedCount} started service(s).",
                        service.Name,
                        sw.ElapsedMilliseconds,
                        StartedServices.Count);

                    await RollbackStartedAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }

                lock (_startedSync)
                {
                    _started.Add(service);
                }

                _logger.LogInformation(
                    "Application service {ServiceName} started in {ElapsedMs} ms.",
                    service.Name,
                    sw.ElapsedMilliseconds);
            }

            BeginExecutionMonitoring();

            _logger.LogInformation("All application services started successfully.");
        }
        finally
        {
            if (acquired)
            {
                _gate.Release();
            }

            Interlocked.Exchange(ref _lifecycleBusy, 0);
        }
    }

    /// <summary>
    /// Stops successfully started services in reverse startup order, applying a bounded shutdown timeout.
    /// </summary>
    /// <param name="cancellationToken">External cancellation that cooperates with the configured shutdown timeout.</param>
    /// <returns>A task that completes when shutdown finishes (or times out).</returns>
    /// <exception cref="InvalidOperationException">Thrown when a lifecycle operation is already in progress.</exception>
    /// <exception cref="TimeoutException">Thrown when shutdown exceeds the configured graceful timeout.</exception>
    /// <exception cref="AggregateException">Thrown when one or more services fail during shutdown.</exception>
    /// <remarks>
    /// <para>
    /// <see cref="NntpdOptions.GracefulShutdownTimeout"/> is a single overall wall-clock budget for the
    /// entire stop sequence (not a fresh full timeout per service). The manager always awaits each
    /// <see cref="IApplicationService.StopAsync"/> — it does not abandon in-flight stops. Cooperative
    /// services observe the linked timeout/cancel token; after the budget is exhausted or the caller
    /// cancels, remaining services are offered an already-canceled token and are removed from
    /// <see cref="StartedServices"/> when their awaited stop attempt finishes.
    /// </para>
    /// <para>
    /// A service that ignores cancellation keeps the manager awaiting until that call returns. The
    /// Generic Host <c>ShutdownTimeout</c> (aligned with <see cref="NntpdOptions.GracefulShutdownTimeout"/>)
    /// and process supervisors (for example systemd <c>TimeoutStopSec</c>) remain the backstop that can
    /// terminate the process. Concurrent <see cref="StartAsync"/> / <see cref="StopAsync"/> calls on this
    /// manager are rejected; <see cref="ApplicationLifecycle"/> provides single-flight stop for host paths.
    /// </para>
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _lifecycleBusy, 1, 0) != 0)
        {
            throw new InvalidOperationException("An application service lifecycle operation is already in progress.");
        }

        var acquired = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;

            await StopExecutionMonitoringAsync().ConfigureAwait(false);

            IApplicationService[] toStop;
            lock (_startedSync)
            {
                toStop = _started.ToArray();
            }

            if (toStop.Length == 0)
            {
                _logger.LogInformation("No started application services to stop.");
                return;
            }

            var timeout = _options.Value.GracefulShutdownTimeout;
            _logger.LogInformation(
                "Stopping {ServiceCount} application service(s) in reverse startup order with overall timeout {Timeout}.",
                toStop.Length,
                timeout);

            var budgetSw = Stopwatch.StartNew();
            var failures = new List<Exception>();
            var budgetExhausted = false;
            var externallyCanceled = false;

            for (var i = toStop.Length - 1; i >= 0; i--)
            {
                var service = toStop[i];
                var sw = Stopwatch.StartNew();

                _logger.LogInformation(
                    "Stopping application service {ServiceName} ({Remaining} remaining including current).",
                    service.Name,
                    i + 1);

                CancellationTokenSource? timeoutCts = null;
                CancellationTokenSource? linkedCts = null;
                try
                {
                    CancellationToken stopToken;
                    if (externallyCanceled || budgetExhausted || budgetSw.Elapsed >= timeout)
                    {
                        // Overall budget spent or caller canceled: cooperative abort only — do not extend the wall clock.
                        if (budgetSw.Elapsed >= timeout)
                        {
                            budgetExhausted = true;
                        }

                        timeoutCts = new CancellationTokenSource();
                        timeoutCts.Cancel();
                        stopToken = timeoutCts.Token;
                    }
                    else
                    {
                        var remaining = timeout - budgetSw.Elapsed;
                        timeoutCts = new CancellationTokenSource(remaining);
                        linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken,
                            timeoutCts.Token);
                        stopToken = linkedCts.Token;
                    }

                    try
                    {
                        await service.StopAsync(stopToken).ConfigureAwait(false);

                        // Tracking drops only after the awaited stop attempt returns — never while still executing.
                        lock (_startedSync)
                        {
                            _started.Remove(service);
                        }

                        // Non-cooperative stops may return after the budget without throwing OCE.
                        if (budgetSw.Elapsed >= timeout)
                        {
                            if (!budgetExhausted)
                            {
                                budgetExhausted = true;
                                _logger.LogError(
                                    "Graceful shutdown timed out after {Timeout} while stopping application service {ServiceName} (elapsed {ElapsedMs} ms; stop returned after budget).",
                                    timeout,
                                    service.Name,
                                    sw.ElapsedMilliseconds);

                                failures.Add(new TimeoutException(
                                    $"Graceful shutdown timed out after {timeout} while stopping '{service.Name}'."));
                            }
                            else
                            {
                                _logger.LogWarning(
                                    "Application service {ServiceName} stop completed after graceful shutdown budget was already exhausted ({ElapsedMs} ms).",
                                    service.Name,
                                    sw.ElapsedMilliseconds);
                            }
                        }
                        else
                        {
                            _logger.LogInformation(
                                "Application service {ServiceName} stopped in {ElapsedMs} ms.",
                                service.Name,
                                sw.ElapsedMilliseconds);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        externallyCanceled = true;
                        _logger.LogWarning(
                            "Shutdown of application service {ServiceName} was canceled after {ElapsedMs} ms. Continuing best-effort abort of remaining services.",
                            service.Name,
                            sw.ElapsedMilliseconds);

                        // Drop tracking and continue so ApplicationLifecycle Stopped/DisposeAsync cannot
                        // strand services that were never offered a stop after external cancellation.
                        lock (_startedSync)
                        {
                            _started.Remove(service);
                        }
                    }
                    catch (OperationCanceledException) when (budgetExhausted
                                                             || timeoutCts.IsCancellationRequested)
                    {
                        if (!budgetExhausted)
                        {
                            budgetExhausted = true;
                            _logger.LogError(
                                "Graceful shutdown timed out after {Timeout} while stopping application service {ServiceName} (elapsed {ElapsedMs} ms).",
                                timeout,
                                service.Name,
                                sw.ElapsedMilliseconds);

                            failures.Add(new TimeoutException(
                                $"Graceful shutdown timed out after {timeout} while stopping '{service.Name}'."));
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Application service {ServiceName} stop aborted after graceful shutdown budget was already exhausted.",
                                service.Name);
                        }

                        // Drop tracking even when StopAsync did not complete cleanly so lifecycle
                        // Stopped does not strand services in StartedServices.
                        lock (_startedSync)
                        {
                            _started.Remove(service);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "Application service {ServiceName} failed during shutdown after {ElapsedMs} ms.",
                            service.Name,
                            sw.ElapsedMilliseconds);
                        failures.Add(ex);

                        lock (_startedSync)
                        {
                            _started.Remove(service);
                        }
                    }
                }
                finally
                {
                    linkedCts?.Dispose();
                    timeoutCts?.Dispose();
                }
            }

            if (externallyCanceled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (failures.Count == 1 && failures[0] is TimeoutException timeoutEx)
            {
                throw timeoutEx;
            }

            if (failures.Count > 0)
            {
                throw new AggregateException("One or more application services failed during shutdown.", failures);
            }

            _logger.LogInformation("All application services stopped successfully.");
        }
        finally
        {
            if (acquired)
            {
                _gate.Release();
            }

            Interlocked.Exchange(ref _lifecycleBusy, 0);
        }
    }

    private async Task RollbackStartedAsync(CancellationToken cancellationToken)
    {
        await StopExecutionMonitoringAsync().ConfigureAwait(false);

        IApplicationService[] toStop;
        lock (_startedSync)
        {
            toStop = _started.ToArray();
        }

        if (toStop.Length == 0)
        {
            return;
        }

        _logger.LogWarning(
            "Rolling back {ServiceCount} partially started application service(s) in reverse order.",
            toStop.Length);

        var timeout = _options.Value.GracefulShutdownTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        for (var i = toStop.Length - 1; i >= 0; i--)
        {
            var service = toStop[i];
            try
            {
                await service.StopAsync(timeoutCts.Token).ConfigureAwait(false);
                _logger.LogInformation("Rolled back application service {ServiceName}.", service.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to roll back application service {ServiceName} after startup failure.",
                    service.Name);
            }
            finally
            {
                lock (_startedSync)
                {
                    _started.Remove(service);
                }
            }
        }
    }

    private void BeginExecutionMonitoring()
    {
        var monitored = _services
            .Where(static s => s.Execution is not null)
            .ToArray();

        if (monitored.Length == 0)
        {
            return;
        }

        _executionCts = new CancellationTokenSource();
        var token = _executionCts.Token;
        _executionMonitor = MonitorExecutionsAsync(monitored, token);
    }

    private async Task MonitorExecutionsAsync(IReadOnlyList<IApplicationService> monitored, CancellationToken cancellationToken)
    {
        var tasks = monitored
            .Select(s => WatchServiceAsync(s, cancellationToken))
            .ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during orderly shutdown.
        }
    }

    private async Task WatchServiceAsync(IApplicationService service, CancellationToken cancellationToken)
    {
        var execution = service.Execution;
        if (execution is null)
        {
            return;
        }

        try
        {
            var completed = await Task.WhenAny(execution, Task.Delay(Timeout.Infinite, cancellationToken))
                .ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (completed != execution)
            {
                return;
            }

            // Observe the execution outcome.
            if (execution.IsFaulted)
            {
                var ex = execution.Exception?.GetBaseException() ?? new InvalidOperationException("Service execution faulted.");
                _logger.LogError(
                    ex,
                    "Application service {ServiceName} terminated unexpectedly with a fault.",
                    service.Name);

                UnexpectedServiceTermination?.Invoke(
                    this,
                    new UnexpectedServiceTerminationEventArgs(service.Name, ex, completedNormally: false));
                return;
            }

            if (execution.IsCanceled)
            {
                _logger.LogWarning(
                    "Application service {ServiceName} execution was canceled unexpectedly while the application was running.",
                    service.Name);

                UnexpectedServiceTermination?.Invoke(
                    this,
                    new UnexpectedServiceTerminationEventArgs(
                        service.Name,
                        new OperationCanceledException("Service execution was canceled."),
                        completedNormally: false));
                return;
            }

            _logger.LogWarning(
                "Application service {ServiceName} execution completed unexpectedly while the application was running.",
                service.Name);

            UnexpectedServiceTermination?.Invoke(
                this,
                new UnexpectedServiceTerminationEventArgs(service.Name, exception: null, completedNormally: true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Orderly shutdown.
        }
    }

    private async Task StopExecutionMonitoringAsync()
    {
        var cts = Interlocked.Exchange(ref _executionCts, null);
        var monitor = Interlocked.Exchange(ref _executionMonitor, null);

        if (cts is not null)
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Already disposed.
            }
        }

        if (monitor is not null)
        {
            try
            {
                await monitor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Execution monitor completed with an exception during shutdown.");
            }
        }

        cts?.Dispose();
    }
}

/// <summary>
/// Provides data for the <see cref="ApplicationServiceManager.UnexpectedServiceTermination"/> event.
/// </summary>
public sealed class UnexpectedServiceTerminationEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UnexpectedServiceTerminationEventArgs"/> class.
    /// </summary>
    /// <param name="serviceName">Name of the service that terminated.</param>
    /// <param name="exception">Fault exception, if any.</param>
    /// <param name="completedNormally">Whether the execution task completed without fault or cancellation.</param>
    public UnexpectedServiceTerminationEventArgs(string serviceName, Exception? exception, bool completedNormally)
    {
        ServiceName = serviceName;
        Exception = exception;
        CompletedNormally = completedNormally;
    }

    /// <summary>Gets the service name.</summary>
    public string ServiceName { get; }

    /// <summary>Gets the fault exception, if the execution faulted.</summary>
    public Exception? Exception { get; }

    /// <summary>Gets a value indicating whether execution completed without fault or cancellation.</summary>
    public bool CompletedNormally { get; }
}
