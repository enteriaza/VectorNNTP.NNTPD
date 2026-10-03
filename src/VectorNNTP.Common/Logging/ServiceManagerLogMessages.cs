namespace VectorNNTP.Common.Logging
{
    /// <summary>
    /// Source-generated structured log messages for application service manager events.
    /// </summary>
    internal static partial class ServiceManagerLogMessages
    {
        /// <summary>Emitted once at the start of a startup pass, before any service is started.</summary>
        /// <param name="logger">Service-manager logger.</param>
        /// <param name="ServiceCount">Number of registered services, including those not yet started.</param>
        [LoggerMessage(
            EventId = 1100,
            Level = LogLevel.Information,
            Message = "Starting {ServiceCount} application service(s) in registration order")]
        internal static partial void StartingServices(ILogger logger, int ServiceCount);

        /// <summary>Emitted immediately before each service's <c>StartAsync</c>.</summary>
        /// <param name="logger">Service-manager logger.</param>
        /// <param name="ServiceName">Name of the service about to start.</param>
        /// <param name="Index">One-based position in registration order.</param>
        /// <param name="Total">Number of registered services.</param>
        [LoggerMessage(
            EventId = 1101,
            Level = LogLevel.Information,
            Message = "Starting application service {ServiceName} ({Index}/{Total})")]
        internal static partial void StartingService(ILogger logger, string ServiceName, int Index, int Total);

        /// <summary>
        /// Emitted when a service start is canceled by the caller, before already-started services are rolled back.
        /// </summary>
        /// <param name="logger">Service-manager logger.</param>
        /// <param name="ServiceName">Name of the service whose start was canceled.</param>
        /// <param name="ElapsedMs">Milliseconds spent in that service's <c>StartAsync</c> before cancellation.</param>
        /// <param name="StartedCount">Services that had already started and will be rolled back.</param>
        [LoggerMessage(
            EventId = 1102,
            Level = LogLevel.Warning,
            Message = "Startup of application service {ServiceName} was canceled after {ElapsedMs} ms. Rolling back {StartedCount} started service(s)")]
        internal static partial void ServiceStartupCanceled(
            ILogger logger,
            string ServiceName,
            long ElapsedMs,
            int StartedCount);

        /// <summary>
        /// Emitted when a service start throws an exception other than caller cancellation, before rollback.
        /// </summary>
        /// <param name="logger">Service-manager logger.</param>
        /// <param name="exception">The start failure.</param>
        /// <param name="ServiceName">Name of the service that failed to start.</param>
        /// <param name="ElapsedMs">Milliseconds spent in that service's <c>StartAsync</c> before the failure.</param>
        /// <param name="StartedCount">Services that had already started and will be rolled back.</param>
        [LoggerMessage(
            EventId = 1103,
            Level = LogLevel.Error,
            Message = "Application service {ServiceName} failed during startup after {ElapsedMs} ms. Rolling back {StartedCount} started service(s)")]
        internal static partial void ServiceStartupFailed(
            ILogger logger,
            Exception exception,
            string ServiceName,
            long ElapsedMs,
            int StartedCount);

        /// <summary>Emitted after a service's <c>StartAsync</c> returns and the service is tracked as started.</summary>
        /// <param name="logger">Service-manager logger.</param>
        /// <param name="ServiceName">Name of the service that started.</param>
        /// <param name="ElapsedMs">Milliseconds spent in that service's <c>StartAsync</c>.</param>
        [LoggerMessage(
            EventId = 1104,
            Level = LogLevel.Information,
            Message = "Application service {ServiceName} started in {ElapsedMs} ms")]
        internal static partial void ServiceStarted(ILogger logger, string ServiceName, long ElapsedMs);

        /// <summary>Emitted after every registered service has started and execution monitoring has begun.</summary>
        /// <param name="logger">Service-manager logger.</param>
        [LoggerMessage(
            EventId = 1105,
            Level = LogLevel.Information,
            Message = "All application services started successfully")]
        internal static partial void AllServicesStarted(ILogger logger);

        /// <summary>Emitted when stop finds no services still tracked as started.</summary>
        /// <param name="logger">Service-manager logger.</param>
        [LoggerMessage(
            EventId = 1106,
            Level = LogLevel.Information,
            Message = "No started application services to stop")]
        internal static partial void NoServicesToStop(ILogger logger);

        /// <summary>Emitted once before the reverse-order stop loop.</summary>
        /// <param name="logger">Service-manager logger.</param>
        /// <param name="ServiceCount">Services still tracked as started.</param>
        /// <param name="Timeout">Single overall graceful-shutdown budget for the whole stop sequence.</param>
        [LoggerMessage(
            EventId = 1107,
            Level = LogLevel.Information,
            Message = "Stopping {ServiceCount} application service(s) in reverse startup order with overall timeout {Timeout}")]
        internal static partial void StoppingServices(ILogger logger, int ServiceCount, TimeSpan Timeout);

        /// <summary>Emitted immediately before each service's <c>StopAsync</c>.</summary>
        /// <param name="logger">Service-manager logger.</param>
        /// <param name="ServiceName">Name of the service about to stop.</param>
        /// <param name="Remaining">
        /// Services in this stop pass that have not yet been offered <c>StopAsync</c>, including the current one.
        /// The stop loop walks the started list backwards, so the logged value is the current index plus one.
        /// </param>
        [LoggerMessage(
            EventId = 1108,
            Level = LogLevel.Information,
            Message = "Stopping application service {ServiceName} ({Remaining} remaining including current)")]
        internal static partial void StoppingService(ILogger logger, string ServiceName, int Remaining);

        /// <summary>
        /// Emitted when a service's stop returns successfully but the overall budget was already exhausted by that call,
        /// and this is the first service to cross the budget.
        /// </summary>
        /// <param name="logger">Service-manager logger.</param>
        /// <param name="Timeout">Configured graceful-shutdown budget.</param>
        /// <param name="ServiceName">Name of the service whose stop returned after the budget.</param>
        /// <param name="ElapsedMs">Milliseconds spent in that service's <c>StopAsync</c>.</param>
        [LoggerMessage(
            EventId = 1109,
            Level = LogLevel.Error,
            Message = "Graceful shutdown timed out after {Timeout} while stopping application service {ServiceName} (elapsed {ElapsedMs} ms; stop returned after budget)")]
        internal static partial void GracefulShutdownTimedOutAfterBudget(
            ILogger logger,
            TimeSpan Timeout,
            string ServiceName,
            long ElapsedMs);

        /// <summary>
        /// Emitted when a later service's stop returns after the budget was already recorded as exhausted.
        /// </summary>
        /// <param name="logger">Service-manager logger.</param>
        /// <param name="ServiceName">Name of the service whose stop returned.</param>
        /// <param name="ElapsedMs">Milliseconds spent in that service's <c>StopAsync</c>.</param>
        [LoggerMessage(
            EventId = 1110,
            Level = LogLevel.Warning,
            Message = "Application service {ServiceName} stop completed after graceful shutdown budget was already exhausted ({ElapsedMs} ms)")]
        internal static partial void ServiceStopCompletedAfterBudget(
            ILogger logger,
            string ServiceName,
            long ElapsedMs);

        /// <summary>Emitted when a service stop returns inside the overall budget.</summary>
        /// <param name="logger">Service-manager logger.</param>
        /// <param name="ServiceName">Name of the service that stopped.</param>
        /// <param name="ElapsedMs">Milliseconds spent in that service's <c>StopAsync</c>.</param>
        [LoggerMessage(
            EventId = 1111,
            Level = LogLevel.Information,
            Message = "Application service {ServiceName} stopped in {ElapsedMs} ms")]
        internal static partial void ServiceStopped(ILogger logger, string ServiceName, long ElapsedMs);

        /// <summary>
        /// Emitted when a service stop is canceled by the caller's token.
        /// The service is dropped from the started set and remaining services are still offered a stop.
        /// </summary>
        /// <param name="logger">Service-manager logger.</param>
        /// <param name="ServiceName">Name of the service whose stop was canceled.</param>
        /// <param name="ElapsedMs">Milliseconds spent in that service's <c>StopAsync</c> before cancellation.</param>
        [LoggerMessage(
            EventId = 1112,
            Level = LogLevel.Warning,
            Message = "Shutdown of application service {ServiceName} was canceled after {ElapsedMs} ms. Continuing best-effort abort of remaining services")]
        internal static partial void ServiceShutdownCanceled(ILogger logger, string ServiceName, long ElapsedMs);

        /// <summary>
        /// Emitted when a service stop throws <see cref="OperationCanceledException"/> because the shutdown budget
        /// elapsed, and this is the first service to cross the budget.
        /// </summary>
        /// <param name="logger">Service-manager logger.</param>
        /// <param name="Timeout">Configured graceful-shutdown budget.</param>
        /// <param name="ServiceName">Name of the service whose stop was canceled by the budget.</param>
        /// <param name="ElapsedMs">Milliseconds spent in that service's <c>StopAsync</c>.</param>
        [LoggerMessage(
            EventId = 1113,
            Level = LogLevel.Error,
            Message = "Graceful shutdown timed out after {Timeout} while stopping application service {ServiceName} (elapsed {ElapsedMs} ms)")]
        internal static partial void GracefulShutdownTimedOut(
            ILogger logger,
            TimeSpan Timeout,
            string ServiceName,
            long ElapsedMs);

        /// <summary>
        /// Emitted when a later service is offered an already-canceled stop token after the budget was exhausted,
        /// and that stop throws cancellation.
        /// </summary>
        /// <param name="logger">Service-manager logger.</param>
        /// <param name="ServiceName">Name of the service whose stop was aborted.</param>
        [LoggerMessage(
            EventId = 1114,
            Level = LogLevel.Warning,
            Message = "Application service {ServiceName} stop aborted after graceful shutdown budget was already exhausted")]
        internal static partial void ServiceStopAbortedAfterBudget(ILogger logger, string ServiceName);

        /// <summary>Emitted when a service stop throws an exception other than budget or caller cancellation.</summary>
        /// <param name="logger">Service-manager logger.</param>
        /// <param name="exception">The stop failure. It is also collected and thrown after the remaining services are stopped.</param>
        /// <param name="ServiceName">Name of the service that failed to stop.</param>
        /// <param name="ElapsedMs">Milliseconds spent in that service's <c>StopAsync</c>.</param>
        [LoggerMessage(
            EventId = 1115,
            Level = LogLevel.Error,
            Message = "Application service {ServiceName} failed during shutdown after {ElapsedMs} ms")]
        internal static partial void ServiceShutdownFailed(
            ILogger logger,
            Exception exception,
            string ServiceName,
            long ElapsedMs);

        /// <summary>Emitted when every tracked service has been stopped and no stop failure was collected.</summary>
        /// <param name="logger">Service-manager logger.</param>
        [LoggerMessage(
            EventId = 1116,
            Level = LogLevel.Information,
            Message = "All application services stopped successfully")]
        internal static partial void AllServicesStopped(ILogger logger);

        /// <summary>Emitted before reverse-order rollback of services that started before a startup failure.</summary>
        /// <param name="logger">Service-manager logger.</param>
        /// <param name="ServiceCount">Services that will be stopped.</param>
        [LoggerMessage(
            EventId = 1117,
            Level = LogLevel.Warning,
            Message = "Rolling back {ServiceCount} partially started application service(s) in reverse order")]
        internal static partial void RollingBackServices(ILogger logger, int ServiceCount);

        /// <summary>Emitted after a rollback <c>StopAsync</c> returns.</summary>
        /// <param name="logger">Service-manager logger.</param>
        /// <param name="ServiceName">Name of the service that was rolled back.</param>
        [LoggerMessage(
            EventId = 1118,
            Level = LogLevel.Information,
            Message = "Rolled back application service {ServiceName}")]
        internal static partial void ServiceRolledBack(ILogger logger, string ServiceName);

        /// <summary>
        /// Emitted when a rollback <c>StopAsync</c> throws. Rollback continues with the remaining services.
        /// </summary>
        /// <param name="logger">Service-manager logger.</param>
        /// <param name="exception">The rollback stop failure.</param>
        /// <param name="ServiceName">Name of the service that failed to roll back.</param>
        [LoggerMessage(
            EventId = 1119,
            Level = LogLevel.Error,
            Message = "Failed to roll back application service {ServiceName} after startup failure")]
        internal static partial void ServiceRollbackFailed(ILogger logger, Exception exception, string ServiceName);

        /// <summary>
        /// Emitted when a watched <c>Execution</c> task faults while the shutdown watch is not canceled.
        /// </summary>
        /// <param name="logger">Service-manager logger.</param>
        /// <param name="exception">The execution fault, or a substitute when the task has no base exception.</param>
        /// <param name="ServiceName">Name of the service whose execution faulted.</param>
        [LoggerMessage(
            EventId = 1120,
            Level = LogLevel.Error,
            Message = "Application service {ServiceName} terminated unexpectedly with a fault")]
        internal static partial void ServiceTerminatedWithFault(ILogger logger, Exception exception, string ServiceName);

        /// <summary>
        /// Emitted when a watched <c>Execution</c> task is canceled while the shutdown watch is not canceled.
        /// </summary>
        /// <param name="logger">Service-manager logger.</param>
        /// <param name="ServiceName">Name of the service whose execution was canceled.</param>
        [LoggerMessage(
            EventId = 1121,
            Level = LogLevel.Warning,
            Message = "Application service {ServiceName} execution was canceled unexpectedly while the application was running")]
        internal static partial void ServiceExecutionCanceledUnexpectedly(ILogger logger, string ServiceName);

        /// <summary>
        /// Emitted when a watched <c>Execution</c> task completes without fault or cancellation while the shutdown watch is not canceled.
        /// </summary>
        /// <param name="logger">Service-manager logger.</param>
        /// <param name="ServiceName">Name of the service whose execution completed.</param>
        [LoggerMessage(
            EventId = 1122,
            Level = LogLevel.Warning,
            Message = "Application service {ServiceName} execution completed unexpectedly while the application was running")]
        internal static partial void ServiceExecutionCompletedUnexpectedly(ILogger logger, string ServiceName);

        /// <summary>
        /// Emitted when awaiting the execution monitor during shutdown throws an exception other than cancellation.
        /// </summary>
        /// <param name="logger">Service-manager logger.</param>
        /// <param name="exception">The monitor failure. It does not fail the surrounding stop or rollback.</param>
        [LoggerMessage(
            EventId = 1123,
            Level = LogLevel.Debug,
            Message = "Execution monitor completed with an exception during shutdown")]
        internal static partial void ExecutionMonitorExceptionDuringShutdown(ILogger logger, Exception exception);
    }
}
