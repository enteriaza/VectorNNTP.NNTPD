namespace VectorNNTP.BackFiller.ArticleWork
{
    /// <summary>
    /// Reconciles Article Work consume sessions from the provider snapshot and usable NNTP capacity.
    /// </summary>
    internal interface IArticleWorkConsumerReconciliation
    {
        /// <summary>Gets whether the consumer host has started and is not shutting down.</summary>
        bool IsRunning { get; }

        /// <summary>Creates, keeps, or retires consume sessions to match desired membership.</summary>
        /// <param name="cancellationToken">Cancellation for the reconciled attempt.</param>
        /// <returns>A task that completes when the reconciled attempt finishes.</returns>
        Task ReconcileAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Retires consume sessions for <paramref name="backbone"/> whose connection number
        /// is greater than <paramref name="retainConnectionCount"/>.
        /// </summary>
        /// <param name="backbone">Provider backbone whose excess consumption slots are retired.</param>
        /// <param name="retainConnectionCount">
        /// Highest connection number to keep. Sessions with a greater connection number are retired.
        /// </param>
        /// <param name="cancellationToken">Cancellation passed through to each retired session.</param>
        /// <returns>A task that completes when each selected session has been retired and disposed of.</returns>
        Task RetireCapacityAsync(string backbone, int retainConnectionCount, CancellationToken cancellationToken);
    }
}
