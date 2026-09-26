using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.NNTPD.Core;

namespace VectorNNTP.BackFiller.Core;

/// <summary>
/// Coordinates deterministic start/stop of registered <see cref="IApplicationService"/> instances.
/// </summary>
/// <remarks>
/// Services start in registration order and stop in reverse order. On startup failure,
/// already-started services are stopped (rollback). Concurrent lifecycle execution is rejected.
/// </remarks>
public sealed class ApplicationServiceManager
{
    private readonly IReadOnlyList<IApplicationService> _services;
    private readonly TimeSpan _shutdownBudget;
    private readonly ILogger<ApplicationServiceManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<IApplicationService> _started = [];
    private readonly object _startedSync = new();
    private int _lifecycleBusy;
    private CancellationTokenSource? _executionCts;
    private Task? _executionMonitor;

    /// <summary>
    /// Occurs when a started service's <see cref="IApplicationService.Execution"/> faulted or completed unexpectedly.
    /// </summary>
    public event EventHandler<UnexpectedServiceTerminationEventArgs>? UnexpectedServiceTermination;

    /// <summary>Initializes a new instance of the <see cref="ApplicationServiceManager"/> class.</summary>
    /// <param name="services">Registered application services in startup order.</param>
    /// <param name="runtime">Runtime snapshot that supplies the shutdown budget.</param>
    /// <param name="logger">Logger.</param>
    public ApplicationServiceManager(
        IEnumerable<IApplicationService> services,
        BackFillerRuntimeOptions runtime,
        ILogger<ApplicationServiceManager> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services as IReadOnlyList<IApplicationService> ?? [.. services];
        _shutdownBudget = runtime.Shutdown.GracePeriod;
        _logger = logger;
    }

    /// <summary>Gets the services that completed start and have not finished stop.</summary>
    public IReadOnlyList<IApplicationService> StartedServices
    {
        get
        {
            lock (_startedSync)
            {
                return [.. _started];
            }
        }
    }

    /// <summary>Starts registered services in registration order.</summary>
    /// <param name="cancellationToken">Cancels startup.</param>
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

            for (var i = 0; i < _services.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var service = _services[i];
                ApplicationServiceManagerLogMessages.StartingService(
                    _logger,
                    service.Name,
                    i + 1,
                    _services.Count);
                try
                {
                    await service.StartAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    ApplicationServiceManagerLogMessages.ServiceStartupFailed(_logger, ex, service.Name);
                    await RollbackStartedAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    ApplicationServiceManagerLogMessages.ServiceStartupCanceled(_logger, service.Name);
                    await RollbackStartedAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }

                lock (_startedSync)
                {
                    _started.Add(service);
                }

                ApplicationServiceManagerLogMessages.ServiceStarted(_logger, service.Name);
            }

            BeginExecutionMonitoring();
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

    /// <summary>Stops started services in reverse order.</summary>
    /// <param name="cancellationToken">External cancellation cooperating with the shutdown budget.</param>
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
                toStop = [.. _started];
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_shutdownBudget);
            var failures = new List<Exception>();
            for (var i = toStop.Length - 1; i >= 0; i--)
            {
                var service = toStop[i];
                ApplicationServiceManagerLogMessages.StoppingService(_logger, service.Name);
                try
                {
                    await service.StopAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ApplicationServiceManagerLogMessages.ServiceStopFailed(_logger, ex, service.Name);
                    failures.Add(ex);
                }
                catch (OperationCanceledException)
                {
                    ApplicationServiceManagerLogMessages.ServiceStopCanceled(_logger, service.Name);
                }
                finally
                {
                    lock (_startedSync)
                    {
                        _started.Remove(service);
                    }
                }
            }

            if (failures.Count > 0)
            {
                throw new AggregateException("One or more application services failed during shutdown.", failures);
            }
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
            toStop = [.. _started];
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_shutdownBudget);
        for (var i = toStop.Length - 1; i >= 0; i--)
        {
            var service = toStop[i];
            try
            {
                await service.StopAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ApplicationServiceManagerLogMessages.ServiceRollbackFailed(_logger, ex, service.Name);
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
        var monitored = _services.Where(static item => item.Execution is not null).ToArray();
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
        try
        {
            await Task.WhenAll(monitored.Select(item => WatchServiceAsync(item, cancellationToken)))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Orderly shutdown.
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
            if (cancellationToken.IsCancellationRequested || completed != execution)
            {
                return;
            }

            if (execution.IsFaulted)
            {
                var ex = execution.Exception?.GetBaseException()
                    ?? new InvalidOperationException("Service execution faulted.");
                ApplicationServiceManagerLogMessages.ServiceExecutionFaulted(_logger, ex, service.Name);
                UnexpectedServiceTermination?.Invoke(
                    this,
                    new UnexpectedServiceTerminationEventArgs(service.Name, ex));
                return;
            }

            ApplicationServiceManagerLogMessages.ServiceExecutionEnded(_logger, service.Name);
            UnexpectedServiceTermination?.Invoke(
                this,
                new UnexpectedServiceTerminationEventArgs(service.Name, exception: null));
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

            cts.Dispose();
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
        }
    }
}

/// <summary>Provides data for unexpected application-service termination.</summary>
public sealed class UnexpectedServiceTerminationEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="UnexpectedServiceTerminationEventArgs"/> class.</summary>
    /// <param name="serviceName">Terminated service name.</param>
    /// <param name="exception">Fault, if any.</param>
    public UnexpectedServiceTerminationEventArgs(string serviceName, Exception? exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ServiceName = serviceName;
        Exception = exception;
    }

    /// <summary>Gets the terminated service name.</summary>
    public string ServiceName { get; }

    /// <summary>Gets the fault, if the execution faulted.</summary>
    public Exception? Exception { get; }
}
