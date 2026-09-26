namespace VectorNNTP.BackFiller.RabbitMq;

/// <summary>
/// Caller-owned AMQP channel created from a current connection generation.
/// </summary>
/// <remarks>
/// The channel owner disposes the channel. Disposing a channel must not dispose
/// the process RabbitMQ connection. This phase does not expose consume, publish,
/// or ACK/NACK operations.
/// </remarks>
public interface IBackFillerRabbitMqChannel : IAsyncDisposable
{
    /// <summary>Gets the connection generation this channel was opened against.</summary>
    long Generation { get; }

    /// <summary>Gets a value indicating whether the channel currently reports itself open.</summary>
    bool IsOpen { get; }
}
