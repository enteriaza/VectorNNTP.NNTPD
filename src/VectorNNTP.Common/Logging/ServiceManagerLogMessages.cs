namespace VectorNNTP.NNTPD.Logging;

/// <summary>
/// Source-generated structured log messages for application service manager events.
/// </summary>
internal static partial class ServiceManagerLogMessages
{
    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Information,
        Message = "Starting {ServiceCount} application service(s) in registration order")]
    public static partial void StartingServices(ILogger logger, int ServiceCount);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Information,
        Message = "Starting application service {ServiceName} ({Index}/{Total})")]
    public static partial void StartingService(ILogger logger, string ServiceName, int Index, int Total);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Warning,
        Message = "Startup of application service {ServiceName} was canceled after {ElapsedMs} ms. Rolling back {StartedCount} started service(s)")]
    public static partial void ServiceStartupCanceled(
        ILogger logger,
        string ServiceName,
        long ElapsedMs,
        int StartedCount);

    [LoggerMessage(
        EventId = 1103,
        Level = LogLevel.Error,
        Message = "Application service {ServiceName} failed during startup after {ElapsedMs} ms. Rolling back {StartedCount} started service(s)")]
    public static partial void ServiceStartupFailed(
        ILogger logger,
        Exception exception,
        string ServiceName,
        long ElapsedMs,
        int StartedCount);

    [LoggerMessage(
        EventId = 1104,
        Level = LogLevel.Information,
        Message = "Application service {ServiceName} started in {ElapsedMs} ms")]
    public static partial void ServiceStarted(ILogger logger, string ServiceName, long ElapsedMs);

    [LoggerMessage(
        EventId = 1105,
        Level = LogLevel.Information,
        Message = "All application services started successfully")]
    public static partial void AllServicesStarted(ILogger logger);

    [LoggerMessage(
        EventId = 1106,
        Level = LogLevel.Information,
        Message = "No started application services to stop")]
    public static partial void NoServicesToStop(ILogger logger);

    [LoggerMessage(
        EventId = 1107,
        Level = LogLevel.Information,
        Message = "Stopping {ServiceCount} application service(s) in reverse startup order with overall timeout {Timeout}")]
    public static partial void StoppingServices(ILogger logger, int ServiceCount, TimeSpan Timeout);

    [LoggerMessage(
        EventId = 1108,
        Level = LogLevel.Information,
        Message = "Stopping application service {ServiceName} ({Remaining} remaining including current)")]
    public static partial void StoppingService(ILogger logger, string ServiceName, int Remaining);

    [LoggerMessage(
        EventId = 1109,
        Level = LogLevel.Error,
        Message = "Graceful shutdown timed out after {Timeout} while stopping application service {ServiceName} (elapsed {ElapsedMs} ms; stop returned after budget)")]
    public static partial void GracefulShutdownTimedOutAfterBudget(
        ILogger logger,
        TimeSpan Timeout,
        string ServiceName,
        long ElapsedMs);

    [LoggerMessage(
        EventId = 1110,
        Level = LogLevel.Warning,
        Message = "Application service {ServiceName} stop completed after graceful shutdown budget was already exhausted ({ElapsedMs} ms)")]
    public static partial void ServiceStopCompletedAfterBudget(
        ILogger logger,
        string ServiceName,
        long ElapsedMs);

    [LoggerMessage(
        EventId = 1111,
        Level = LogLevel.Information,
        Message = "Application service {ServiceName} stopped in {ElapsedMs} ms")]
    public static partial void ServiceStopped(ILogger logger, string ServiceName, long ElapsedMs);

    [LoggerMessage(
        EventId = 1112,
        Level = LogLevel.Warning,
        Message = "Shutdown of application service {ServiceName} was canceled after {ElapsedMs} ms. Continuing best-effort abort of remaining services")]
    public static partial void ServiceShutdownCanceled(ILogger logger, string ServiceName, long ElapsedMs);

    [LoggerMessage(
        EventId = 1113,
        Level = LogLevel.Error,
        Message = "Graceful shutdown timed out after {Timeout} while stopping application service {ServiceName} (elapsed {ElapsedMs} ms)")]
    public static partial void GracefulShutdownTimedOut(
        ILogger logger,
        TimeSpan Timeout,
        string ServiceName,
        long ElapsedMs);

    [LoggerMessage(
        EventId = 1114,
        Level = LogLevel.Warning,
        Message = "Application service {ServiceName} stop aborted after graceful shutdown budget was already exhausted")]
    public static partial void ServiceStopAbortedAfterBudget(ILogger logger, string ServiceName);

    [LoggerMessage(
        EventId = 1115,
        Level = LogLevel.Error,
        Message = "Application service {ServiceName} failed during shutdown after {ElapsedMs} ms")]
    public static partial void ServiceShutdownFailed(
        ILogger logger,
        Exception exception,
        string ServiceName,
        long ElapsedMs);

    [LoggerMessage(
        EventId = 1116,
        Level = LogLevel.Information,
        Message = "All application services stopped successfully")]
    public static partial void AllServicesStopped(ILogger logger);

    [LoggerMessage(
        EventId = 1117,
        Level = LogLevel.Warning,
        Message = "Rolling back {ServiceCount} partially started application service(s) in reverse order")]
    public static partial void RollingBackServices(ILogger logger, int ServiceCount);

    [LoggerMessage(
        EventId = 1118,
        Level = LogLevel.Information,
        Message = "Rolled back application service {ServiceName}")]
    public static partial void ServiceRolledBack(ILogger logger, string ServiceName);

    [LoggerMessage(
        EventId = 1119,
        Level = LogLevel.Error,
        Message = "Failed to roll back application service {ServiceName} after startup failure")]
    public static partial void ServiceRollbackFailed(ILogger logger, Exception exception, string ServiceName);

    [LoggerMessage(
        EventId = 1120,
        Level = LogLevel.Error,
        Message = "Application service {ServiceName} terminated unexpectedly with a fault")]
    public static partial void ServiceTerminatedWithFault(ILogger logger, Exception exception, string ServiceName);

    [LoggerMessage(
        EventId = 1121,
        Level = LogLevel.Warning,
        Message = "Application service {ServiceName} execution was canceled unexpectedly while the application was running")]
    public static partial void ServiceExecutionCanceledUnexpectedly(ILogger logger, string ServiceName);

    [LoggerMessage(
        EventId = 1122,
        Level = LogLevel.Warning,
        Message = "Application service {ServiceName} execution completed unexpectedly while the application was running")]
    public static partial void ServiceExecutionCompletedUnexpectedly(ILogger logger, string ServiceName);

    [LoggerMessage(
        EventId = 1123,
        Level = LogLevel.Debug,
        Message = "Execution monitor completed with an exception during shutdown")]
    public static partial void ExecutionMonitorExceptionDuringShutdown(ILogger logger, Exception exception);
}
