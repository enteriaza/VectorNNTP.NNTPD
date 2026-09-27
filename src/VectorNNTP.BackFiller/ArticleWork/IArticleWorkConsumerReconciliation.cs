namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Reconciles Article Work consume sessions from the provider snapshot and usable NNTP capacity.
/// </summary>
public interface IArticleWorkConsumerReconciliation
{
    /// <summary>Gets whether the consumer host has started and is not shutting down.</summary>
    bool IsRunning { get; }

    /// <summary>Creates, keeps, or retires consume sessions to match desired membership.</summary>
    Task ReconcileAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Retires consume sessions for <paramref name="backbone"/> whose connection number
    /// is greater than <paramref name="retainConnectionCount"/>.
    /// </summary>
    Task RetireCapacityAsync(string backbone, int retainConnectionCount, CancellationToken cancellationToken);
}
