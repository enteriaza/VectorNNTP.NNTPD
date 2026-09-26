using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace VectorNNTP.BackFiller.RabbitMq;

/// <summary>
/// Confirm-enabled RabbitMQ.Client publish channel. Does not ACK Article Work deliveries.
/// </summary>
/// <remarks>
/// Created with <c>publisherConfirmationsEnabled</c> and
/// <c>publisherConfirmationTrackingEnabled</c>. Completing
/// <see cref="PublishConfirmedAsync"/> means the broker confirmed. A completed
/// <c>BasicPublishAsync</c> without those options is not a confirmation.
/// </remarks>
internal sealed class BackFillerRabbitMqClientPublishChannel : IBackFillerRabbitMqPublishChannel
{
    private readonly IChannel _channel;
    private int _disposed;

    /// <summary>
    /// Initializes a new wrapper around an opened confirm-enabled channel.
    /// </summary>
    /// <param name="channel">Opened RabbitMQ.Client channel with confirmation tracking.</param>
    /// <param name="generation">Connection generation the channel belongs to.</param>
    internal BackFillerRabbitMqClientPublishChannel(IChannel channel, long generation)
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
    public async Task PublishConfirmedAsync(BackFillerRabbitMqPublication publication, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (!_channel.IsOpen)
        {
            throw new InvalidOperationException("RabbitMQ publish channel is not open.");
        }

        if (string.IsNullOrWhiteSpace(publication.ReplyTo))
        {
            throw new InvalidOperationException("ReplyTo is required for response publication.");
        }

        if (string.IsNullOrWhiteSpace(publication.CorrelationId))
        {
            throw new InvalidOperationException("CorrelationId is required for response publication.");
        }

        var properties = new BasicProperties
        {
            ContentType = publication.ContentType,
            CorrelationId = publication.CorrelationId,
            MessageId = publication.MessageId,
            DeliveryMode = DeliveryModes.Transient,
            Expiration = publication.ExpirationMilliseconds,
        };

        if (!string.IsNullOrWhiteSpace(publication.RequestIdHeader))
        {
            properties.Headers = new Dictionary<string, object?>
            {
                [ArticleWork.ArticleWorkResponseWireProtocol.RequestIdHeaderName] = publication.RequestIdHeader,
            };
        }

        try
        {
            await _channel.BasicPublishAsync(
                    exchange: string.Empty,
                    routingKey: publication.ReplyTo,
                    mandatory: true,
                    basicProperties: properties,
                    body: publication.Body,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PublishException ex)
        {
            throw new InvalidOperationException(
                ex.IsReturn
                    ? "RabbitMQ returned the Article Work response as unroutable."
                    : "RabbitMQ negatively acknowledged the Article Work response.",
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
}
