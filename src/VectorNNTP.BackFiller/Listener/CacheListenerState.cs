namespace VectorNNTP.BackFiller.Listener;

/// <summary>Local lifecycle of the cache Listener service.</summary>
/// <remarks>
/// Transitions are assigned by <see cref="CacheListenerService"/> under its state lock.
/// <see cref="Stopped"/> is also entered when start fails after bound sockets are released.
/// </remarks>
internal enum CacheListenerState
{
    /// <summary>Constructed and not yet started.</summary>
    Created = 0,

    /// <summary>
    /// Start is in progress: TLS certificate readiness and availability are checked, then listen sockets are bound.
    /// </summary>
    Starting = 1,

    /// <summary>Listen sockets are bound and the accept loops are running or about to start.</summary>
    Running = 2,

    /// <summary>
    /// Shutdown has begun. The accept token is cancelled, listen sockets are closed, and admitted connections drain.
    /// </summary>
    Retiring = 3,

    /// <summary>Listen sockets are closed and the service is not running.</summary>
    Stopped = 4,
}
