using VectorNNTP.StorageServer.Storage.Engine.Maintenance;

namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

/// <summary>Counts from one bounded pressure-expiration window.</summary>
/// <param name="EntriesVisited">Index rows examined, including rows that are not Present.</param>
/// <param name="PresentEvaluated">Present rows inspected in this window.</param>
/// <param name="TooYoung">Present rows younger than the pressure grace.</param>
/// <param name="MissingArrival">Present rows whose durable arrival instant is missing.</param>
/// <param name="FutureArrival">Present rows whose arrival instant is after the evaluation clock.</param>
/// <param name="Selected">Eligible rows this window attempted to expire, after the state limit.</param>
/// <param name="Expired">Rows transitioned to Evicted.</param>
/// <param name="BytesLogicallyExpired">Sum of location lengths for rows this window evicted. Not bytes deleted.</param>
/// <param name="StateChanged">Candidates whose row changed before the tombstone, so no frame was written.</param>
/// <param name="BatchSize">Maximum rows this window was allowed to examine.</param>
/// <param name="DurationMilliseconds">Elapsed time, including index appends.</param>
/// <param name="Wrapped">True when the scan cursor reached the end of the retained population.</param>
/// <param name="State">Bulk class supplied by the caller. Not recomputed here.</param>
public readonly record struct PressureExpirationResult(
    int EntriesVisited,
    int PresentEvaluated,
    int TooYoung,
    int MissingArrival,
    int FutureArrival,
    int Selected,
    int Expired,
    long BytesLogicallyExpired,
    int StateChanged,
    int BatchSize,
    double DurationMilliseconds,
    bool Wrapped,
    BulkStoragePressureState State);
