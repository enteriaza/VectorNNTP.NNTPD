namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>
/// Process-wide RabbitMQ connection surface. Does not own topology, publishers, or consumers.
/// </summary>
public interface IRabbitMqService
{
    /// <summary>Gets a value indicating whether the current broker connection is open and usable.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Gets the monotonic generation of the current connection, or zero before the first successful connect.
    /// </summary>
    long ConnectionGeneration { get; }

    /// <summary>
    /// Raised after a connection generation becomes current.
    /// </summary>
    /// <remarks>
    /// The first successful connect has <see cref="RabbitMqConnectionReplacedEventArgs.IsReplacement"/>
    /// set to <see langword="false"/>. Later generations set it to <see langword="true"/> so callers
    /// can drop work tied to the previous connection.
    /// </remarks>
    event EventHandler<RabbitMqConnectionReplacedEventArgs>? ConnectionReplaced;
}
