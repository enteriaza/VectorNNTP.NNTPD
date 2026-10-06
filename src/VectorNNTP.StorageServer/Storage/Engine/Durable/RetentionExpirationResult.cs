namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

/// <summary>Counts from one bounded retention-expiration batch.</summary>
/// <param name="EntriesVisited">Index rows examined, including rows that are not Present.</param>
/// <param name="PresentEvaluated">Present rows whose arrival age was evaluated.</param>
/// <param name="Expired">Present rows transitioned to Evicted by this batch.</param>
/// <param name="NotEligible">Present rows the age predicate rejected.</param>
/// <param name="StateChanged">
/// Candidates whose authoritative row no longer matched the scan snapshot, so no tombstone was written.
/// </param>
/// <param name="BatchSize">Maximum rows this batch was allowed to examine.</param>
/// <param name="DurationMilliseconds">Elapsed time of the batch, including index appends.</param>
/// <param name="Wrapped">True when the scan cursor reached the end of the retained population.</param>
/// <param name="Disabled">True when <c>MaxRetentionAge</c> was zero or negative and no rows were examined.</param>
public readonly record struct RetentionExpirationResult(
    int EntriesVisited,
    int PresentEvaluated,
    int Expired,
    int NotEligible,
    int StateChanged,
    int BatchSize,
    double DurationMilliseconds,
    bool Wrapped,
    bool Disabled);
