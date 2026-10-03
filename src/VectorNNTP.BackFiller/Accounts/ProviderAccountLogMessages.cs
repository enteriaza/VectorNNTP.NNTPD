namespace VectorNNTP.BackFiller.Accounts
{
    /// <summary>Source-generated account control-plane logs. Never includes passwords or connection strings.</summary>
    internal static partial class ProviderAccountLogMessages
    {
        /// <summary>
        /// Writes event 5600 after a refresh applies a snapshot that differs from the previous one.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="serverId">Configured BackFiller server id supplied by the caller.</param>
        /// <param name="providerCount">Count of providers in the applied snapshot.</param>
        /// <param name="rejectedCount">Count of rows rejected while mapping that query.</param>
        [LoggerMessage(
            EventId = 5600,
            Level = LogLevel.Information,
            Message = "Provider account snapshot loaded serverId={ServerId} providers={ProviderCount} rejectedRows={RejectedCount}")]
        internal static partial void SnapshotLoaded(ILogger logger, int serverId, int providerCount, int rejectedCount);

        /// <summary>
        /// Writes event 5601 when the new snapshot contains a backbone the previous snapshot did not.
        /// Credentials and keepalive are not included.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="backbone">Canonical backbone that was added.</param>
        /// <param name="host">Provider host on the new definition.</param>
        /// <param name="port">Provider port on the new definition.</param>
        /// <param name="useTls">Whether the new definition uses TLS.</param>
        /// <param name="minSessions"><see cref="Nntp.BackFillerProviderDefinition.MinSessions"/> on the new definition.</param>
        /// <param name="maxSessions"><see cref="Nntp.BackFillerProviderDefinition.MaxSessions"/> on the new definition.</param>
        [LoggerMessage(
            EventId = 5601,
            Level = LogLevel.Information,
            Message = "Provider added backbone={Backbone} host={Host} port={Port} tls={UseTls} minSessions={MinSessions} maxSessions={MaxSessions}")]
        internal static partial void ProviderAdded(
            ILogger logger,
            string backbone,
            string host,
            int port,
            bool useTls,
            int minSessions,
            int maxSessions);

        /// <summary>
        /// Writes event 5602 when the previous snapshot contains a backbone, the new snapshot does not.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="backbone">Backbone that was removed.</param>
        [LoggerMessage(
            EventId = 5602,
            Level = LogLevel.Information,
            Message = "Provider removed backbone={Backbone}")]
        internal static partial void ProviderRemoved(ILogger logger, string backbone);

        /// <summary>
        /// Writes event 5603 when the same backbone is present in both snapshots and the definitions are not equal.
        /// A password-only or keepalive-only change still logs these non-secret fields.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="backbone">Canonical backbone whose definition changed.</param>
        /// <param name="host">Provider host on the new definition.</param>
        /// <param name="port">Provider port on the new definition.</param>
        /// <param name="useTls">Whether the new definition uses TLS.</param>
        /// <param name="minSessions"><see cref="Nntp.BackFillerProviderDefinition.MinSessions"/> on the new definition.</param>
        /// <param name="maxSessions"><see cref="Nntp.BackFillerProviderDefinition.MaxSessions"/> on the new definition.</param>
        [LoggerMessage(
            EventId = 5603,
            Level = LogLevel.Information,
            Message = "Provider configuration changed backbone={Backbone} host={Host} port={Port} tls={UseTls} minSessions={MinSessions} maxSessions={MaxSessions}")]
        internal static partial void ProviderChanged(
            ILogger logger,
            string backbone,
            string host,
            int port,
            bool useTls,
            int minSessions,
            int maxSessions);

        /// <summary>
        /// Writes event 5604 when a refresh fails and the caller retains the last applied snapshot.
        /// Not written when the failure is rethrown or when the caller observes cancellation.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="exception">Failure being logged. This method logs it and does not throw it.</param>
        [LoggerMessage(
            EventId = 5604,
            Level = LogLevel.Warning,
            Message = "Provider account refresh failed; retaining last known-good snapshot")]
        internal static partial void RefreshFailed(ILogger logger, Exception exception);

        /// <summary>
        /// Writes event 5605 when a later refresh succeeds after <see cref="RefreshFailed"/> was written.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="providerCount">Count of providers in the successful snapshot.</param>
        [LoggerMessage(
            EventId = 5605,
            Level = LogLevel.Information,
            Message = "Provider account refresh recovered providers={ProviderCount}")]
        internal static partial void RefreshRecovered(ILogger logger, int providerCount);

        /// <summary>
        /// Writes event 5606 for one row excluded from the snapshot.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="backbone">Backbone associated with the rejected row.</param>
        /// <param name="reason">Rejection reason produced by mapping.</param>
        [LoggerMessage(
            EventId = 5606,
            Level = LogLevel.Warning,
            Message = "Rejected provider account row backbone={Backbone} reason={Reason}")]
        internal static partial void RowRejected(ILogger logger, string backbone, string reason);

        /// <summary>
        /// Writes event 5607 when a refresh maps a snapshot equal to the one already applied.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="providerCount">Count of providers in that unchanged snapshot.</param>
        [LoggerMessage(
            EventId = 5607,
            Level = LogLevel.Debug,
            Message = "Provider account snapshot unchanged providers={ProviderCount}")]
        internal static partial void SnapshotUnchanged(ILogger logger, int providerCount);

        /// <summary>
        /// Writes event 5608 when the control plane accepts its first start, before the required refresh.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        /// <param name="serverId">Configured BackFiller server id supplied by the caller.</param>
        [LoggerMessage(
            EventId = 5608,
            Level = LogLevel.Information,
            Message = "Provider account control plane starting serverId={ServerId}")]
        internal static partial void Starting(ILogger logger, int serverId);

        /// <summary>
        /// Writes event 5609 after the first disposal has cancelled the poll loop and disposed of its token source.
        /// </summary>
        /// <param name="logger">Logger that receives the event.</param>
        [LoggerMessage(
            EventId = 5609,
            Level = LogLevel.Information,
            Message = "Provider account control plane stopped")]
        internal static partial void Stopped(ILogger logger);
    }
}
