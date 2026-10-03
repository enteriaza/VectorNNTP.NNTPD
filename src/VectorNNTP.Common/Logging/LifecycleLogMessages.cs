using VectorNNTP.Common.Core;

namespace VectorNNTP.Common.Logging
{
    /// <summary>
    /// Reserved logger category names for VectorNNTP.NNTPD lifecycle diagnostics.
    /// </summary>
    internal static class NntpdLogCategories
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
        /// <summary>
        /// Emitted by <see cref="ApplicationLifecycle"/> after a validated state change is committed.
        /// </summary>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="FromState">State before the transition.</param>
        /// <param name="ToState">State after the transition.</param>
        [LoggerMessage(
            EventId = 1000,
            Level = LogLevel.Information,
            Message = "Application lifecycle state transition: {FromState} -> {ToState}")]
        internal static partial void LifecycleTransition(
            ILogger logger,
            ApplicationStateLog FromState,
            ApplicationStateLog ToState);

        /// <summary>
        /// Reserved unused template. Operational startup uses
        /// <see cref="StartupInitiatedWithState"/> so existing message text is preserved.
        /// </summary>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="ApplicationName">Application display name that would have been logged.</param>
        [LoggerMessage(
            EventId = 1001,
            Level = LogLevel.Information,
            Message = "Application startup initiated for {ApplicationName}")]
        private static partial void StartupInitiated(ILogger logger, string ApplicationName);

        /// <summary>
        /// Reserved unused template. Operational running uses
        /// <see cref="InitializationCompleted"/> so existing message text is preserved.
        /// </summary>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="ApplicationName">Application display name that would have been logged.</param>
        /// <param name="ElapsedMs">Startup duration in milliseconds that would have been logged.</param>
        [LoggerMessage(
            EventId = 1002,
            Level = LogLevel.Information,
            Message = "Application entered Running state for {ApplicationName} in {ElapsedMs} ms")]
        private static partial void EnteredRunning(ILogger logger, string ApplicationName, long ElapsedMs);

        /// <summary>
        /// Reserved unused template. Operational shutdown uses
        /// <see cref="ShutdownInitiatedWithDetails"/> so existing message text is preserved.
        /// </summary>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="ApplicationName">Application display name that would have been logged.</param>
        [LoggerMessage(
            EventId = 1003,
            Level = LogLevel.Information,
            Message = "Application shutdown initiated for {ApplicationName}")]
        private static partial void ShutdownInitiated(ILogger logger, string ApplicationName);

        /// <summary>
        /// Reserved unused template. Operational shutdown completion uses
        /// <see cref="ShutdownCompletedWithState"/> so existing message text is preserved.
        /// </summary>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="ApplicationName">Application display name that would have been logged.</param>
        /// <param name="ElapsedMs">Shutdown duration in milliseconds that would have been logged.</param>
        [LoggerMessage(
            EventId = 1004,
            Level = LogLevel.Information,
            Message = "Application shutdown completed for {ApplicationName} in {ElapsedMs} ms")]
        private static partial void ShutdownCompleted(ILogger logger, string ApplicationName, long ElapsedMs);

        /// <summary>
        /// Reserved unused template. Operational timeout uses
        /// <see cref="ShutdownTimedOutElapsed"/> so existing message text is preserved.
        /// </summary>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="ApplicationName">Application display name that would have been logged.</param>
        /// <param name="Timeout">Configured shutdown budget that would have been logged.</param>
        [LoggerMessage(
            EventId = 1005,
            Level = LogLevel.Error,
            Message = "Application shutdown timed out for {ApplicationName} after {Timeout}")]
        private static partial void ShutdownTimedOut(ILogger logger, string ApplicationName, TimeSpan Timeout);

        /// <summary>
        /// Emitted at the start of startup, after the transition to <see cref="ApplicationState.Starting"/>.
        /// </summary>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="ApplicationName">Configured application display name.</param>
        /// <param name="State">Lifecycle state at emission, which is <see cref="ApplicationState.Starting"/>.</param>
        [LoggerMessage(
            EventId = 1010,
            Level = LogLevel.Information,
            Message = "Application startup initiated for {ApplicationName}. Current state: {State}")]
        internal static partial void StartupInitiatedWithState(
            ILogger logger,
            string ApplicationName,
            ApplicationState State);

        /// <summary>
        /// Emitted when the caller cancels service startup, before startup-failure cleanup moves the lifecycle to stopped.
        /// </summary>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="ElapsedMs">Milliseconds from the start of service startup until cancellation.</param>
        [LoggerMessage(
            EventId = 1011,
            Level = LogLevel.Warning,
            Message = "Application startup canceled after {ElapsedMs} ms. Transitioning to shutdown")]
        internal static partial void StartupCanceled(ILogger logger, long ElapsedMs);

        /// <summary>
        /// Emitted when the linked startup token cancels because <see cref="IApplicationLifecycleOptions.StartupTimeout"/>
        /// elapsed, and the caller's token is not canceled.
        /// </summary>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="Timeout">Configured startup timeout passed to the log. Null only if options have no timeout.</param>
        /// <param name="ElapsedMs">Milliseconds from the start of service startup until the timeout.</param>
        [LoggerMessage(
            EventId = 1012,
            Level = LogLevel.Error,
            Message = "Application startup timed out after {Timeout} ({ElapsedMs} ms)")]
        internal static partial void StartupTimedOut(ILogger logger, TimeSpan? Timeout, long ElapsedMs);

        /// <summary>
        /// Emitted when service startup throws an exception other than cancellation, before startup-failure cleanup.
        /// </summary>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="exception">The startup failure.</param>
        /// <param name="ElapsedMs">Milliseconds from the start of service startup until the failure.</param>
        [LoggerMessage(
            EventId = 1013,
            Level = LogLevel.Error,
            Message = "Application startup failed after {ElapsedMs} ms. Rolling back and transitioning to Stopped")]
        internal static partial void StartupFailed(ILogger logger, Exception exception, long ElapsedMs);

        /// <summary>
        /// Emitted after startup succeeds and the lifecycle has entered <see cref="ApplicationState.Running"/>.
        /// </summary>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="ApplicationName">Configured application display name.</param>
        /// <param name="ElapsedMs">Milliseconds from the start of service startup until running.</param>
        /// <param name="State">Lifecycle state at emission, which is <see cref="ApplicationState.Running"/>.</param>
        [LoggerMessage(
            EventId = 1014,
            Level = LogLevel.Information,
            Message = "Application initialization completed for {ApplicationName} in {ElapsedMs} ms. State: {State}")]
        internal static partial void InitializationCompleted(
            ILogger logger,
            string ApplicationName,
            long ElapsedMs,
            ApplicationState State);

        /// <summary>
        /// Emitted when stop is requested while the lifecycle is still <see cref="ApplicationState.Created"/>,
        /// which moves directly to <see cref="ApplicationState.Stopped"/> without starting services.
        /// </summary>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="From">State before the direct stop, <see cref="ApplicationState.Created"/>.</param>
        /// <param name="To">State after the direct stop, <see cref="ApplicationState.Stopped"/>.</param>
        [LoggerMessage(
            EventId = 1015,
            Level = LogLevel.Information,
            Message = "Application stop requested before startup. State transition: {From} -> {To}")]
        internal static partial void StopRequestedBeforeStartup(
            ILogger logger,
            ApplicationState From,
            ApplicationState To);

        /// <summary>
        /// Emitted when <see cref="ApplicationLifecycle.DisposeAsync"/>'s stop throws.
        /// The same exception is rethrown after the instance is marked disposed.
        /// </summary>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="exception">The shutdown failure from stop.</param>
        [LoggerMessage(
            EventId = 1016,
            Level = LogLevel.Error,
            Message = "ApplicationLifecycle disposal encountered a shutdown failure")]
        internal static partial void DisposalShutdownFailure(ILogger logger, Exception exception);

        /// <summary>
        /// Emitted from the stop core after the lifecycle is <see cref="ApplicationState.Stopping"/>
        /// and before services are stopped.
        /// </summary>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="ApplicationName">Configured application display name.</param>
        /// <param name="State">Lifecycle state at emission.</param>
        /// <param name="Timeout">Configured graceful-shutdown budget.</param>
        [LoggerMessage(
            EventId = 1017,
            Level = LogLevel.Information,
            Message = "Application shutdown initiated for {ApplicationName}. State: {State}. Timeout: {Timeout}")]
        internal static partial void ShutdownInitiatedWithDetails(
            ILogger logger,
            string ApplicationName,
            ApplicationState State,
            TimeSpan Timeout);

        /// <summary>
        /// Emitted when service stop throws <see cref="TimeoutException"/>.
        /// The lifecycle is then forced to <see cref="ApplicationState.Stopped"/> and the exception is rethrown.
        /// </summary>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="exception">The timeout from the service manager.</param>
        /// <param name="ElapsedMs">Milliseconds from the start of the stop core until the timeout.</param>
        [LoggerMessage(
            EventId = 1018,
            Level = LogLevel.Error,
            Message = "Application shutdown timed out after {ElapsedMs} ms")]
        internal static partial void ShutdownTimedOutElapsed(ILogger logger, Exception exception, long ElapsedMs);

        /// <summary>
        /// Emitted when service stop throws an exception other than <see cref="TimeoutException"/>.
        /// The lifecycle is then forced to <see cref="ApplicationState.Stopped"/> and the exception is rethrown.
        /// </summary>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="exception">The stop failure.</param>
        /// <param name="ElapsedMs">Milliseconds from the start of the stop core until the failure.</param>
        [LoggerMessage(
            EventId = 1019,
            Level = LogLevel.Error,
            Message = "Application shutdown failed after {ElapsedMs} ms. Forcing Stopped state")]
        internal static partial void ShutdownFailed(ILogger logger, Exception exception, long ElapsedMs);

        /// <summary>
        /// Emitted after services stop successfully and the lifecycle has entered <see cref="ApplicationState.Stopped"/>.
        /// </summary>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="ApplicationName">Configured application display name.</param>
        /// <param name="ElapsedMs">Milliseconds from the start of the stop core until stopped.</param>
        /// <param name="State">Lifecycle state at emission, which is <see cref="ApplicationState.Stopped"/>.</param>
        [LoggerMessage(
            EventId = 1020,
            Level = LogLevel.Information,
            Message = "Application shutdown completed for {ApplicationName} in {ElapsedMs} ms. State: {State}")]
        internal static partial void ShutdownCompletedWithState(
            ILogger logger,
            string ApplicationName,
            long ElapsedMs,
            ApplicationState State);

        /// <summary>
        /// Emitted when the residual service stop during startup-failure cleanup throws.
        /// The original startup exception is still propagated.
        /// </summary>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="exception">The residual cleanup failure.</param>
        [LoggerMessage(
            EventId = 1021,
            Level = LogLevel.Error,
            Message = "Additional cleanup after startup failure encountered an error")]
        internal static partial void StartupFailureCleanupError(ILogger logger, Exception exception);

        /// <summary>
        /// Emitted when a <c>StateChanged</c> subscriber throws. The transition stays committed.
        /// </summary>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="exception">The handler failure.</param>
        /// <param name="FromState">State before the transition.</param>
        /// <param name="ToState">State after the transition.</param>
        [LoggerMessage(
            EventId = 1022,
            Level = LogLevel.Error,
            Message = "An ApplicationLifecycle.StateChanged handler failed for transition {FromState} -> {ToState}")]
        internal static partial void StateChangedHandlerFailed(
            ILogger logger,
            Exception exception,
            ApplicationState FromState,
            ApplicationState ToState);

        /// <summary>
        /// Emitted when a service execution ends while the lifecycle is <see cref="ApplicationState.Running"/>.
        /// Completes the unexpected-termination wait observed by <c>WaitAsync</c>.
        /// </summary>
        /// <param name="logger">Lifecycle logger.</param>
        /// <param name="exception">Fault or cancellation exception, or null when the execution completed without either.</param>
        /// <param name="ServiceName">Name of the service whose execution ended.</param>
        /// <param name="CompletedNormally">
        /// <see langword="true"/> when the execution task ran to completion without fault or cancellation.
        /// </param>
        [LoggerMessage(
            EventId = 1023,
            Level = LogLevel.Critical,
            Message = "Unexpected termination of application service {ServiceName} while Running (completedNormally={CompletedNormally})")]
        internal static partial void UnexpectedServiceTerminationWhileRunning(
            ILogger logger,
            Exception? exception,
            string ServiceName,
            bool CompletedNormally);
    }

    /// <summary>
    /// Log-friendly mirror of <see cref="ApplicationState"/> to keep logging helpers decoupled.
    /// </summary>
    internal enum ApplicationStateLog
    {
        /// <summary>Log value for <see cref="ApplicationState.Created"/>.</summary>
        Created = 0,

        /// <summary>Log value for <see cref="ApplicationState.Starting"/>.</summary>
        Starting = 1,

        /// <summary>Log value for <see cref="ApplicationState.Running"/>.</summary>
        Running = 2,

        /// <summary>Log value for <see cref="ApplicationState.Stopping"/>.</summary>
        Stopping = 3,

        /// <summary>Log value for <see cref="ApplicationState.Stopped"/>.</summary>
        Stopped = 4,
    }
}
