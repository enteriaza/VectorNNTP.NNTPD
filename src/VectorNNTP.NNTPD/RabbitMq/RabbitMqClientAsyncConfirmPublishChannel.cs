using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>
/// RabbitMQ.Client publish channel with confirms enabled and library tracking disabled.
/// </summary>
internal sealed class RabbitMqClientAsyncConfirmPublishChannel : IRabbitMqAsyncConfirmPublishChannel
{
    private readonly IChannel _channel;
    private int _disposed;

    /// <summary>Initializes a new wrapper around an opened async-confirm channel.</summary>
    internal RabbitMqClientAsyncConfirmPublishChannel(IChannel channel, long generation)
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
    public event AsyncEventHandler<BasicAckEventArgs> BasicAcksAsync
    {
        add => _channel.BasicAcksAsync += value;
        remove => _channel.BasicAcksAsync -= value;
    }

    /// <inheritdoc />
    public event AsyncEventHandler<BasicNackEventArgs> BasicNacksAsync
    {
        add => _channel.BasicNacksAsync += value;
        remove => _channel.BasicNacksAsync -= value;
    }

    /// <inheritdoc />
    public event AsyncEventHandler<BasicReturnEventArgs> BasicReturnAsync
    {
        add => _channel.BasicReturnAsync += value;
        remove => _channel.BasicReturnAsync -= value;
    }

    /// <inheritdoc />
    public ValueTask<ulong> GetNextPublishSequenceNumberAsync(CancellationToken cancellationToken) =>
        _channel.GetNextPublishSequenceNumberAsync(cancellationToken);

    /// <inheritdoc />
    public async Task PublishAsync(
        string exchange,
        string routingKey,
        string messageId,
        string appId,
        string expiration,
        ulong publishSequenceNumber,
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

        var properties = CreateHandoffProperties(messageId, appId, expiration, publishSequenceNumber);
        await _channel.BasicPublishAsync(
                exchange,
                routingKey,
                mandatory: OverviewDbTopology.Mandatory,
                basicProperties: properties,
                body: body,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

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

    internal static BasicProperties CreateHandoffProperties(
        string messageId,
        string appId,
        string expiration,
        ulong publishSequenceNumber)
    {
        return new BasicProperties
        {
            MessageId = messageId,
            AppId = appId,
            Expiration = expiration,
            DeliveryMode = DeliveryModes.Persistent,
            Persistent = true,
            Headers = new Dictionary<string, object?>
            {
                [Constants.PublishSequenceNumberHeader] = publishSequenceNumber,
            },
        };
    }
}
