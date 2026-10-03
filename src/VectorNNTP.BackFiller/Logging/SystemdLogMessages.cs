namespace VectorNNTP.BackFiller.Logging;

/// <summary>
/// Source-generated structured log messages for systemd notify and watchdog events.
/// </summary>
internal static partial class SystemdLogMessages
{
    [LoggerMessage(
        EventId = 5100,
        Level = LogLevel.Information,
        Message = "systemd lifecycle notifications activated (notifyEnabled={NotifyEnabled}, isSystemdService={IsSystemdService}")]
    internal static partial void LifecycleNotificationsActivated(
        ILogger logger,
        bool NotifyEnabled,
        bool IsSystemdService);

    [LoggerMessage(
        EventId = 5101,
        Level = LogLevel.Information,
        Message = "Reported systemd readiness after successful BackFiller host start")]
    internal static partial void ReadinessReported(ILogger logger);

    [LoggerMessage(
        EventId = 5102,
        Level = LogLevel.Information,
        Message = "Reported systemd STOPPING notification for graceful shutdown")]
    internal static partial void StoppingReported(ILogger logger);

    [LoggerMessage(
        EventId = 5103,
        Level = LogLevel.Information,
        Message = "Reported systemd status: {Status}")]
    internal static partial void StatusReported(ILogger logger, string Status);

    [LoggerMessage(
        EventId = 5104,
        Level = LogLevel.Debug,
        Message = "Sent systemd notification {Notification}")]
    internal static partial void NotificationSent(ILogger logger, string Notification);

    [LoggerMessage(
        EventId = 5105,
        Level = LogLevel.Error,
        Message = "Failed to send systemd notification {Notification}")]
    internal static partial void NotificationFailed(ILogger logger, Exception exception, string Notification);

    [LoggerMessage(
        EventId = 5106,
        Level = LogLevel.Information,
        Message = "systemd runtime detection: isSystemdService={IsSystemdService}, notifyEnabled={NotifyEnabled}, watchdogConfigured={WatchdogConfigured}, watchdogTimeout={WatchdogTimeout}, detail={Detail}")]
    internal static partial void RuntimeDetection(
        ILogger logger,
        bool IsSystemdService,
        bool NotifyEnabled,
        bool WatchdogConfigured,
        TimeSpan? WatchdogTimeout,
        string Detail);

    [LoggerMessage(
        EventId = 5107,
        Level = LogLevel.Warning,
        Message = "Ignoring malformed systemd watchdog configuration (WATCHDOG_USEC is not a positive integer)")]
    internal static partial void MalformedWatchdogUsec(ILogger logger);

    [LoggerMessage(
        EventId = 5108,
        Level = LogLevel.Warning,
        Message = "Ignoring systemd watchdog configuration because WATCHDOG_PID is malformed")]
    internal static partial void MalformedWatchdogPid(ILogger logger);

    [LoggerMessage(
        EventId = 5109,
        Level = LogLevel.Information,
        Message = "systemd watchdog is configured for a different PID; watchdog keep-alives will remain disabled")]
    internal static partial void WatchdogConfiguredForDifferentPid(ILogger logger);

    [LoggerMessage(
        EventId = 5110,
        Level = LogLevel.Warning,
        Message = "Ignoring systemd watchdog configuration because WATCHDOG_USEC is out of range")]
    internal static partial void WatchdogUsecOutOfRange(ILogger logger);

    [LoggerMessage(
        EventId = 5111,
        Level = LogLevel.Information,
        Message = "systemd watchdog remain disabled ({Reason})")]
    internal static partial void WatchdogRemainDisabled(ILogger logger, string Reason);

    [LoggerMessage(
        EventId = 5112,
        Level = LogLevel.Information,
        Message = "systemd watchdog activated. Deadline={WatchdogTimeout}, heartbeatInterval={HeartbeatInterval}")]
    internal static partial void WatchdogActivated(
        ILogger logger,
        TimeSpan WatchdogTimeout,
        TimeSpan HeartbeatInterval);

    [LoggerMessage(
        EventId = 5113,
        Level = LogLevel.Debug,
        Message = "Skipping systemd watchdog keep-alive because the application is unhealthy")]
    internal static partial void WatchdogKeepAliveSkippedUnhealthy(ILogger logger);

    [LoggerMessage(
        EventId = 5114,
        Level = LogLevel.Trace,
        Message = "Sent systemd watchdog keep-alive")]
    internal static partial void WatchdogKeepAliveSent(ILogger logger);

    [LoggerMessage(
        EventId = 5115,
        Level = LogLevel.Error,
        Message = "systemd watchdog notification failed")]
    internal static partial void WatchdogNotificationFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 5116,
        Level = LogLevel.Information,
        Message = "systemd watchdog deactivated due to shutdown")]
    internal static partial void WatchdogDeactivated(ILogger logger);

    [LoggerMessage(
        EventId = 5117,
        Level = LogLevel.Critical,
        Message = "systemd watchdog loop failed unexpectedly; requesting host stop")]
    internal static partial void WatchdogLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 5118,
        Level = LogLevel.Information,
        Message = "systemd watchdog stopping")]
    internal static partial void WatchdogStopping(ILogger logger);
}
