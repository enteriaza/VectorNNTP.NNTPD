namespace VectorNNTP.BackFiller.Listener;

/// <summary>Per-connection protocol session lifecycle.</summary>
public enum CacheListenerSessionState
{
    /// <summary>Admitting and processing requests.</summary>
    Running = 0,

    /// <summary>No new requests; in-flight work may finish.</summary>
    GracefulShutdown = 1,

    /// <summary>Cancellation has been signaled.</summary>
    ForcedShutdown = 2,

    /// <summary>Resources released.</summary>
    Completed = 3,
}
