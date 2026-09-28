using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>
/// Confirm-enabled RabbitMQ.Client publish channel for one-way handoffs.
/// </summary>
/// <remarks>
/// Created with <c>publisherConfirmationsEnabled</c> and
/// <c>publisherConfirmationTrackingEnabled</c>. Publications use
/// <c>mandatory=true</c> so an unroutable message is returned as
/// <see cref="PublishException"/> (<c>IsReturn</c>) rather than confirmed
/// and dropped. Completing <see cref="PublishConfirmedAsync"/> means the
/// broker confirmed a routed publication. A completed
/// <c>BasicPublishAsync</c> without those options is not a confirmation.
/// </remarks>
internal sealed class RabbitMqClientPublishChannel : IRabbitMqPublishChannel
{
    private readonly IChannel _channel;
    private int _disposed;

    /// <summary>Initializes a new wrapper around an opened confirm-enabled channel.</summary>
    internal RabbitMqClientPublishChannel(IChannel channel, long generation)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        Generation = generation;
    }

    /// <inheritdoc />
    public long Generation { get; }

    /// <inheritdoc />
    public bool IsOpen => _channel.IsOpen;

    /// <inheritdoc />
    public async Task PublishConfirmedAsync(
        string exchange,
        string routingKey,
        string messageId,
        string appId,
        string expiration,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentException.ThrowIfNullOrWhiteSpace(routingKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expiration);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (!_channel.IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ publish channel is not open.");
        }

        var properties = CreateHandoffProperties(messageId, appId, expiration);

        try
        {
            await _channel.BasicPublishAsync(
                    exchange,
                    routingKey,
                    mandatory: OverviewDbTopology.Mandatory,
                    basicProperties: properties,
                    body: body,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PublishException ex)
        {
            throw new InvalidOperationException(
                ex.IsReturn
                    ? "RabbitMQ returned the OverviewDB handoff as unroutable."
                    : "RabbitMQ negatively acknowledged the OverviewDB handoff.",
                ex);
        }

        if (!_channel.IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ publish channel closed after publication.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            if (_channel.IsOpen)
            {
                await _channel.CloseAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
        }

        await _channel.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the AMQP properties sent on every OverviewDB publication.
    /// </summary>
    /// <param name="messageId">Fresh UUID string for this publish attempt.</param>
    /// <param name="appId">Generated application FQDN.</param>
    /// <param name="expiration">Per-message TTL in milliseconds, as an AMQP string.</param>
    /// <returns>The properties instance passed to <c>BasicPublishAsync</c>.</returns>
    internal static BasicProperties CreateHandoffProperties(string messageId, string appId, string expiration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expiration);
        return new BasicProperties
        {
            MessageId = messageId,
            AppId = appId,
            Expiration = expiration,
            DeliveryMode = DeliveryModes.Persistent,
            Persistent = true,
        };
    }
}
