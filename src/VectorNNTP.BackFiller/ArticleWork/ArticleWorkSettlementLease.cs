using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>
/// Exactly-once settlement token bound to one channel generation and delivery tag.
/// </summary>
internal sealed class ArticleWorkSettlementLease
{
    private readonly IRabbitMqManualAckChannel _channel;
    private readonly ulong _deliveryTag;
    private readonly long _generation;
    private int _settled;

    /// <summary>
    /// Initializes a new settlement lease.
    /// </summary>
    /// <param name="channel">Original consumer channel. Must not be a replacement channel.</param>
    /// <param name="deliveryTag">Channel-scoped delivery tag.</param>
    /// <param name="generation">Connection generation captured at admission.</param>
    internal ArticleWorkSettlementLease(IRabbitMqManualAckChannel channel, ulong deliveryTag, long generation)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        _deliveryTag = deliveryTag;
        _generation = generation;
    }

    /// <summary>Gets the delivery tag this lease may settle.</summary>
    internal ulong DeliveryTag => _deliveryTag;

    /// <summary>Gets the connection generation captured at admission.</summary>
    internal long Generation => _generation;

    /// <summary>Gets a value indicating whether this lease has already settled.</summary>
    internal bool IsSettled => Volatile.Read(ref _settled) == 1;

    /// <summary>
    /// Attempts ACK or NACK on the original channel only.
    /// </summary>
    /// <param name="disposition">Settlement plan.</param>
    /// <param name="channelStillCurrent">
    /// <see langword="true"/> when the lease generation is still the process current generation
    /// and the channel is still the session channel.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the broker RPC.</param>
    /// <returns>
    /// <see langword="true"/> when this call performed settlement on the original channel.
    /// <see langword="false"/> when the lease was already settled, the channel is stale,
    /// or the broker RPC failed.
    /// </returns>
    internal async Task<bool> TrySettleAsync(
        ArticleWorkDisposition disposition,
        bool channelStillCurrent,
        CancellationToken cancellationToken)
    {
        if (!channelStillCurrent
            || _channel.Generation != _generation
            || !_channel.IsOpen)
        {
            return false;
        }

        if (Interlocked.Exchange(ref _settled, 1) == 1)
        {
            return false;
        }

        try
        {
            if (disposition.Acknowledge)
            {
                await _channel.BasicAckAsync(_deliveryTag, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _channel.BasicNackAsync(_deliveryTag, disposition.Requeue, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns whether <paramref name="channel"/> is the original lease channel at the captured generation.
    /// </summary>
    /// <param name="channel">Candidate channel.</param>
    /// <returns><see langword="true"/> when settlement may use this channel.</returns>
    internal bool IsOriginalChannel(IRabbitMqManualAckChannel? channel) =>
        channel is not null
        && ReferenceEquals(channel, _channel)
        && channel.Generation == _generation;
}
