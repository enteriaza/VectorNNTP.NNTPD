using Microsoft.Extensions.Logging;

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

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Application startup initiated for {ApplicationName}")]
    public static partial void StartupInitiated(ILogger logger, string ApplicationName);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Application entered Running state for {ApplicationName} in {ElapsedMs} ms")]
    public static partial void EnteredRunning(ILogger logger, string ApplicationName, long ElapsedMs);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Information,
        Message = "Application shutdown initiated for {ApplicationName}")]
    public static partial void ShutdownInitiated(ILogger logger, string ApplicationName);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Information,
        Message = "Application shutdown completed for {ApplicationName} in {ElapsedMs} ms")]
    public static partial void ShutdownCompleted(ILogger logger, string ApplicationName, long ElapsedMs);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Error,
        Message = "Application shutdown timed out for {ApplicationName} after {Timeout}")]
    public static partial void ShutdownTimedOut(ILogger logger, string ApplicationName, TimeSpan Timeout);
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
