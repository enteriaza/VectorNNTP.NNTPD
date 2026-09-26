namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>
/// Process-wide RabbitMQ infrastructure surface. Does not own topology, publishers, or consumers.
/// </summary>
public interface IRabbitMqService
{
    /// <summary>Gets a value indicating whether a live broker connection is currently usable.</summary>
    bool IsReady { get; }

    /// <summary>Gets the monotonic generation assigned to the current live connection, or zero before the first connect.</summary>
    long ConnectionGeneration { get; }

    /// <summary>Gets the current RabbitMQ lifecycle state.</summary>
    RabbitMqInfrastructureState State { get; }
}
