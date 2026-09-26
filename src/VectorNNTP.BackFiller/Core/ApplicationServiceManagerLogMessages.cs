namespace VectorNNTP.BackFiller.Core;

/// <summary>Source-generated log messages for <see cref="ApplicationServiceManager"/>.</summary>
internal static partial class ApplicationServiceManagerLogMessages
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Starting application service {ServiceName} ({Index}/{Count})")]
    public static partial void StartingService(ILogger logger, string serviceName, int index, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Started application service {ServiceName}")]
    public static partial void ServiceStarted(ILogger logger, string serviceName);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Application service {ServiceName} failed to start")]
    public static partial void ServiceStartupFailed(ILogger logger, Exception exception, string serviceName);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Application service {ServiceName} startup was canceled")]
    public static partial void ServiceStartupCanceled(ILogger logger, string serviceName);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Stopping application service {ServiceName}")]
    public static partial void StoppingService(ILogger logger, string serviceName);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Application service {ServiceName} failed to stop")]
    public static partial void ServiceStopFailed(ILogger logger, Exception exception, string serviceName);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Application service {ServiceName} stop was canceled")]
    public static partial void ServiceStopCanceled(ILogger logger, string serviceName);

    [LoggerMessage(EventId = 8, Level = LogLevel.Error, Message = "Application service {ServiceName} rollback failed")]
    public static partial void ServiceRollbackFailed(ILogger logger, Exception exception, string serviceName);

    [LoggerMessage(EventId = 9, Level = LogLevel.Error, Message = "Application service {ServiceName} execution faulted")]
    public static partial void ServiceExecutionFaulted(ILogger logger, Exception exception, string serviceName);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "Application service {ServiceName} execution completed unexpectedly")]
    public static partial void ServiceExecutionEnded(ILogger logger, string serviceName);
}
