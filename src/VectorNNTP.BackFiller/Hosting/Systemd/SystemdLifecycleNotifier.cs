using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Logging;
using Microsoft.Extensions.Options;

namespace VectorNNTP.BackFiller.Hosting.Systemd;

/// <summary>
/// Publishes systemd readiness and status notifications from Generic Host lifetime events.
/// </summary>
/// <remarks>
/// <para>
/// BackFiller does not use NNTPD's <c>ApplicationLifecycle</c> state machine. Ready is reported
/// at most once after <see cref="IHostApplicationLifetime.ApplicationStarted"/> (all hosted
/// services started successfully). Startup failure never reports ready. Once shutdown begins,
/// further readiness notifications are suppressed.
/// </para>
/// </remarks>
public sealed class SystemdLifecycleNotifier : IHostedService, IDisposable
{
    private readonly IHostApplicationLifetime _hostLifetime;
    private readonly BackFillerApplicationHealth _health;
    private readonly ISystemdNotifyBridge _notify;
    private readonly ISystemdRuntime _runtime;
    private readonly IOptions<BackFillerOptions> _options;
    private readonly ILogger<SystemdLifecycleNotifier> _logger;
    private readonly object _sync = new();
    private int _readySent;
    private int _stoppingSent;
    private string? _lastStatus;
    private bool _subscribed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemdLifecycleNotifier"/> class.
    /// </summary>
    public SystemdLifecycleNotifier(
        IHostApplicationLifetime hostLifetime,
        BackFillerApplicationHealth health,
        ISystemdNotifyBridge notify,
        ISystemdRuntime runtime,
        IOptions<BackFillerOptions> options,
        ILogger<SystemdLifecycleNotifier> logger)
    {
        ArgumentNullException.ThrowIfNull(hostLifetime);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(notify);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _hostLifetime = hostLifetime;
        _health = health;
        _notify = notify;
        _runtime = runtime;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_runtime.IsLinux && (_runtime.IsSystemdService || _notify.IsEnabled))
        {
            SystemdLogMessages.LifecycleNotificationsActivated(
                _logger,
                _notify.IsEnabled,
                _runtime.IsSystemdService);
        }

        _hostLifetime.ApplicationStarted.Register(OnApplicationStarted);
        _hostLifetime.ApplicationStopping.Register(OnApplicationStopping);
        _subscribed = true;

        TryNotifyStatus("Starting: initializing BackFiller hosted services");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // STOPPING is emitted from ApplicationStopping. Ensure it was observed even if
        // registration raced with an already-stopping host.
        OnApplicationStopping();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _subscribed = false;
    }

    private void OnApplicationStarted()
    {
        if (!_subscribed)
        {
            return;
        }

        _health.MarkStarted();
        TryNotifyStatus("Running: BackFiller hosted services started");
        TryNotifyReady();
    }

    private void OnApplicationStopping()
    {
        _health.MarkStopping();
        TryNotifyStopping();
        TryNotifyStatus("Stopping: graceful shutdown in progress");
    }

    private void TryNotifyReady()
    {
        if (!_options.Value.Systemd.NotifyReadyOnApplicationRunning)
        {
            return;
        }

        if (!_notify.IsEnabled)
        {
            return;
        }

        if (!_health.IsHealthyForWatchdog)
        {
            return;
        }

        if (Interlocked.Exchange(ref _readySent, 1) != 0)
        {
            return;
        }

        try
        {
            _notify.NotifyReady();
            SystemdLogMessages.ReadinessReported(_logger);
        }
        catch (Exception ex)
        {
            SystemdLogMessages.NotificationFailed(_logger, ex, "READY=1");
        }
    }

    private void TryNotifyStopping()
    {
        if (!_options.Value.Systemd.NotifyStoppingOnApplicationShutdown)
        {
            return;
        }

        if (!_notify.IsEnabled)
        {
            return;
        }

        if (Interlocked.Exchange(ref _stoppingSent, 1) != 0)
        {
            return;
        }

        // Once stopping, readiness must not be reported later.
        Interlocked.Exchange(ref _readySent, 1);

        try
        {
            _notify.NotifyStopping();
            SystemdLogMessages.StoppingReported(_logger);
        }
        catch (Exception ex)
        {
            SystemdLogMessages.NotificationFailed(_logger, ex, "STOPPING=1");
        }
    }

    private void TryNotifyStatus(string status)
    {
        if (!_options.Value.Systemd.ReportLifecycleStatus || !_notify.IsEnabled)
        {
            return;
        }

        lock (_sync)
        {
            if (string.Equals(_lastStatus, status, StringComparison.Ordinal))
            {
                return;
            }

            _lastStatus = status;
        }

        try
        {
            _notify.NotifyStatus(status);
            SystemdLogMessages.StatusReported(_logger, status);
        }
        catch (Exception ex)
        {
            SystemdLogMessages.NotificationFailed(_logger, ex, "STATUS");
        }
    }
}
