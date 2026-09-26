namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>
/// Lifecycle states used to report RabbitMQ infrastructure readiness and shutdown progress.
/// </summary>
public enum RabbitMqInfrastructureState
{
    /// <summary>No connection attempt has been started.</summary>
    NotInitialized,

    /// <summary>An initial broker connection attempt is in progress.</summary>
    Connecting,

    /// <summary>A broker connection is open and usable.</summary>
    Connected,

    /// <summary>Application-managed recovery is attempting to replace a failed connection.</summary>
    Reconnecting,

    /// <summary>Connection establishment failed, or recovery failed beyond the configured tolerance.</summary>
    Failed,

    /// <summary>Shutdown or disposal has started and new operations should be rejected.</summary>
    Stopping,

    /// <summary>All owned lifecycle resources have been released.</summary>
    Stopped,
}
