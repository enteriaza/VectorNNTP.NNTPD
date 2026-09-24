using VectorNNTP.NNTPD.Core;

namespace VectorNNTP.NNTPD.Logging;

/// <summary>
/// Reserved logger category names for VectorNNTP.NNTPD lifecycle diagnostics.
/// </summary>
public static class NntpdLogCategories
{
    /// <summary>Category for application lifecycle transitions.</summary>
    public const string Lifecycle = "VectorNNTP.NNTPD.Lifecycle";

    /// <summary>Category for application service manager events.</summary>
    public const string Services = "VectorNNTP.NNTPD.Services";

    /// <summary>Category for host integration events.</summary>
    public const string Hosting = "VectorNNTP.NNTPD.Hosting";
}

/// <summary>
/// High-performance structured log messages for lifecycle events.
/// </summary>
internal static partial class LifecycleLogMessages
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Application lifecycle state transition: {FromState} -> {ToState}")]
    public static partial void LifecycleTransition(
        ILogger logger,
        ApplicationStateLog FromState,
        ApplicationStateLog ToState);

    /// <summary>
    /// Reserved unused template. Operational startup uses
    /// <see cref="StartupInitiatedWithState"/> so existing message text is preserved.
    /// </summary>
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Application startup initiated for {ApplicationName}")]
    public static partial void StartupInitiated(ILogger logger, string ApplicationName);

    /// <summary>
    /// Reserved unused template. Operational running uses
    /// <see cref="InitializationCompleted"/> so existing message text is preserved.
    /// </summary>
    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Application entered Running state for {ApplicationName} in {ElapsedMs} ms")]
    public static partial void EnteredRunning(ILogger logger, string ApplicationName, long ElapsedMs);

    /// <summary>
    /// Reserved unused template. Operational shutdown uses
    /// <see cref="ShutdownInitiatedWithDetails"/> so existing message text is preserved.
    /// </summary>
    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Information,
        Message = "Application shutdown initiated for {ApplicationName}")]
    public static partial void ShutdownInitiated(ILogger logger, string ApplicationName);

    /// <summary>
    /// Reserved unused template. Operational shutdown completion uses
    /// <see cref="ShutdownCompletedWithState"/> so existing message text is preserved.
    /// </summary>
    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Information,
        Message = "Application shutdown completed for {ApplicationName} in {ElapsedMs} ms")]
    public static partial void ShutdownCompleted(ILogger logger, string ApplicationName, long ElapsedMs);

    /// <summary>
    /// Reserved unused template. Operational timeout uses
    /// <see cref="ShutdownTimedOutElapsed"/> so existing message text is preserved.
    /// </summary>
    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Error,
        Message = "Application shutdown timed out for {ApplicationName} after {Timeout}")]
    public static partial void ShutdownTimedOut(ILogger logger, string ApplicationName, TimeSpan Timeout);

    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Information,
        Message = "Application startup initiated for {ApplicationName}. Current state: {State}")]
    public static partial void StartupInitiatedWithState(
        ILogger logger,
        string ApplicationName,
        ApplicationState State);

    [LoggerMessage(
        EventId = 1011,
        Level = LogLevel.Warning,
        Message = "Application startup canceled after {ElapsedMs} ms. Transitioning to shutdown")]
    public static partial void StartupCanceled(ILogger logger, long ElapsedMs);

    [LoggerMessage(
        EventId = 1012,
        Level = LogLevel.Error,
        Message = "Application startup timed out after {Timeout} ({ElapsedMs} ms)")]
    public static partial void StartupTimedOut(ILogger logger, TimeSpan? Timeout, long ElapsedMs);

    [LoggerMessage(
        EventId = 1013,
        Level = LogLevel.Error,
        Message = "Application startup failed after {ElapsedMs} ms. Rolling back and transitioning to Stopped")]
    public static partial void StartupFailed(ILogger logger, Exception exception, long ElapsedMs);

    [LoggerMessage(
        EventId = 1014,
        Level = LogLevel.Information,
        Message = "Application initialization completed for {ApplicationName} in {ElapsedMs} ms. State: {State}")]
    public static partial void InitializationCompleted(
        ILogger logger,
        string ApplicationName,
        long ElapsedMs,
        ApplicationState State);

    [LoggerMessage(
        EventId = 1015,
        Level = LogLevel.Information,
        Message = "Application stop requested before startup. State transition: {From} -> {To}")]
    public static partial void StopRequestedBeforeStartup(
        ILogger logger,
        ApplicationState From,
        ApplicationState To);

    [LoggerMessage(
        EventId = 1016,
        Level = LogLevel.Error,
        Message = "ApplicationLifecycle disposal encountered a shutdown failure")]
    public static partial void DisposalShutdownFailure(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1017,
        Level = LogLevel.Information,
        Message = "Application shutdown initiated for {ApplicationName}. State: {State}. Timeout: {Timeout}")]
    public static partial void ShutdownInitiatedWithDetails(
        ILogger logger,
        string ApplicationName,
        ApplicationState State,
        TimeSpan Timeout);

    [LoggerMessage(
        EventId = 1018,
        Level = LogLevel.Error,
        Message = "Application shutdown timed out after {ElapsedMs} ms")]
    public static partial void ShutdownTimedOutElapsed(ILogger logger, Exception exception, long ElapsedMs);

    [LoggerMessage(
        EventId = 1019,
        Level = LogLevel.Error,
        Message = "Application shutdown failed after {ElapsedMs} ms. Forcing Stopped state")]
    public static partial void ShutdownFailed(ILogger logger, Exception exception, long ElapsedMs);

    [LoggerMessage(
        EventId = 1020,
        Level = LogLevel.Information,
        Message = "Application shutdown completed for {ApplicationName} in {ElapsedMs} ms. State: {State}")]
    public static partial void ShutdownCompletedWithState(
        ILogger logger,
        string ApplicationName,
        long ElapsedMs,
        ApplicationState State);

    [LoggerMessage(
        EventId = 1021,
        Level = LogLevel.Error,
        Message = "Additional cleanup after startup failure encountered an error")]
    public static partial void StartupFailureCleanupError(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1022,
        Level = LogLevel.Error,
        Message = "An ApplicationLifecycle.StateChanged handler failed for transition {FromState} -> {ToState}")]
    public static partial void StateChangedHandlerFailed(
        ILogger logger,
        Exception exception,
        ApplicationState FromState,
        ApplicationState ToState);

    [LoggerMessage(
        EventId = 1023,
        Level = LogLevel.Critical,
        Message = "Unexpected termination of application service {ServiceName} while Running (completedNormally={CompletedNormally})")]
    public static partial void UnexpectedServiceTerminationWhileRunning(
        ILogger logger,
        Exception? exception,
        string ServiceName,
        bool CompletedNormally);
}

/// <summary>
/// Log-friendly mirror of <see cref="Core.ApplicationState"/> to keep logging helpers decoupled.
/// </summary>
internal enum ApplicationStateLog
{
    Created = 0,
    Starting = 1,
    Running = 2,
    Stopping = 3,
    Stopped = 4,
}
