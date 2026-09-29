using RabbitMQ.Client.Events;

namespace VectorNNTP.Common.Messaging.RabbitMq;

/// <summary>
/// Confirm-enabled publish channel that does <b>not</b> await broker confirmation inside
/// <see cref="PublishAsync"/>. Callers correlate confirms via sequence numbers and
/// <see cref="BasicAcksAsync"/> / <see cref="BasicNacksAsync"/> / <see cref="BasicReturnAsync"/>.
/// </summary>
/// <remarks>
/// Created with publisher confirmations enabled and publisher-confirmation tracking
/// disabled so <c>BasicPublishAsync</c> returns after the write. Channel operations must
/// remain single-threaded from the caller; callbacks are invoked by the client I/O path
/// and must stay lightweight.
/// </remarks>
public interface IRabbitMqAsyncConfirmPublishChannel : IAsyncDisposable
{
    /// <summary>Gets the connection generation this channel was opened against.</summary>
    long Generation { get; }

    /// <summary>Gets whether the channel currently reports itself open.</summary>
    bool IsOpen { get; }

    /// <summary>Signalled when a Basic.Ack arrives from the broker.</summary>
    event AsyncEventHandler<BasicAckEventArgs> BasicAcksAsync;

    /// <summary>Signalled when a Basic.Nack arrives from the broker.</summary>
    event AsyncEventHandler<BasicNackEventArgs> BasicNacksAsync;

    /// <summary>Signalled when a Basic.Return arrives for a mandatory publication.</summary>
    event AsyncEventHandler<BasicReturnEventArgs> BasicReturnAsync;

    /// <summary>
    /// Returns the publish sequence number that will be assigned to the next publish.
    /// </summary>
    ValueTask<ulong> GetNextPublishSequenceNumberAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Publishes one persistent mandatory message without waiting for broker confirmation.
    /// </summary>
    Task PublishAsync(
        string exchange,
        string routingKey,
        string messageId,
        string appId,
        string expiration,
        ulong publishSequenceNumber,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken);
}
