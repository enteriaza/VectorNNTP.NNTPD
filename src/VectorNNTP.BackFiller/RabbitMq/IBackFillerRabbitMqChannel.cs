namespace VectorNNTP.BackFiller.RabbitMq;

/// <summary>
/// Caller-owned AMQP channel created from a current connection generation.
/// </summary>
/// <remarks>
/// The channel owner disposes the channel. Disposing a channel must not dispose
/// the process RabbitMQ connection. Settlement for a delivery must use this same
/// instance; a replacement channel must not ACK or NACK another channel's tags.
/// </remarks>
public interface IBackFillerRabbitMqChannel : IAsyncDisposable
{
    /// <summary>Gets the connection generation this channel was opened against.</summary>
    long Generation { get; }

    /// <summary>Gets a value indicating whether the channel currently reports itself open.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Starts a manual-ack consumer on <paramref name="queue"/> with the given prefetch.
    /// </summary>
    /// <param name="queue">Existing <c>backfiller.*</c> queue. Must not be declared here.</param>
    /// <param name="prefetchCount">Per-channel prefetch. Phase 3 uses 1 unless configured.</param>
    /// <param name="onDelivery">Invoked for each delivery. The callback must settle or the session must retire.</param>
    /// <param name="cancellationToken">Token used to cancel consumer start.</param>
    /// <returns>The broker consumer tag.</returns>
    Task<string> BasicConsumeAsync(
        string queue,
        ushort prefetchCount,
        Func<BackFillerRabbitMqConsumedDelivery, Task> onDelivery,
        CancellationToken cancellationToken);

    /// <summary>Cancels the consumer tag on this channel.</summary>
    /// <param name="consumerTag">Tag returned by <see cref="BasicConsumeAsync"/>.</param>
    /// <param name="cancellationToken">Token used to cancel the cancel RPC.</param>
    Task BasicCancelAsync(string consumerTag, CancellationToken cancellationToken);

    /// <summary>Acknowledges <paramref name="deliveryTag"/> on this channel only.</summary>
    /// <param name="deliveryTag">Channel-scoped delivery tag.</param>
    /// <param name="cancellationToken">Token used to cancel the ACK.</param>
    Task BasicAckAsync(ulong deliveryTag, CancellationToken cancellationToken);

    /// <summary>Negatively acknowledges <paramref name="deliveryTag"/> on this channel only.</summary>
    /// <param name="deliveryTag">Channel-scoped delivery tag.</param>
    /// <param name="requeue">Whether the broker should requeue the message.</param>
    /// <param name="cancellationToken">Token used to cancel the NACK.</param>
    Task BasicNackAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken);
}
