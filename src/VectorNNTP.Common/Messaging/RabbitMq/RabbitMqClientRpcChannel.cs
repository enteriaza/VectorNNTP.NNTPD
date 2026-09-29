using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace VectorNNTP.Common.Messaging.RabbitMq;

/// <summary>Adapts a RabbitMQ.Client channel to RPC publish/consume.</summary>
internal sealed class RabbitMqClientRpcChannel : IRabbitMqRpcChannel
{
    /// <summary>AMQP header name carrying the logical request UUID.</summary>
    internal const string RequestIdPropertyName = "RequestId";

    private readonly IChannel _channel;
    private int _disposed;

    /// <summary>Initializes a new wrapper around an opened broker channel.</summary>
    internal RabbitMqClientRpcChannel(IChannel channel, long generation)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        Generation = generation;
    }

    /// <inheritdoc />
    public long Generation { get; }

    /// <inheritdoc />
    public async Task QueueDeclareAsync(
        string queue,
        bool durable,
        bool exclusive,
        bool autoDelete,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        _ = await _channel.QueueDeclareAsync(
            queue,
            durable,
            exclusive,
            autoDelete,
            ToMutable(arguments),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PublishAsync(
        string exchange,
        string routingKey,
        string correlationId,
        string requestId,
        string replyTo,
        string contentType,
        string expiration,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
        ArgumentNullException.ThrowIfNull(routingKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(replyTo);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(expiration);

        var properties = new BasicProperties
        {
            CorrelationId = correlationId,
            ReplyTo = replyTo,
            ContentType = contentType,
            Expiration = expiration,
            Headers = CreateRequestIdHeaders(requestId),
        };

        await _channel.BasicPublishAsync(
            exchange,
            routingKey,
            mandatory: false,
            properties,
            body,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> ConsumeAsync(
        string queue,
        Func<RabbitMqRpcDelivery, Task> onDelivery,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentNullException.ThrowIfNull(onDelivery);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += (_, eventArgs) =>
        {
            var body = eventArgs.Body.ToArray();
            var delivery = new RabbitMqRpcDelivery(
                eventArgs.BasicProperties.CorrelationId,
                ReadRequestId(eventArgs.BasicProperties.Headers),
                eventArgs.BasicProperties.ContentType,
                eventArgs.BasicProperties.Expiration,
                body,
                Generation);
            return onDelivery(delivery);
        };

        return await _channel.BasicConsumeAsync(
            queue,
            autoAck: true,
            consumer,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _channel.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Creates the AMQP header table that carries <see cref="RequestIdPropertyName"/>.</summary>
    internal static Dictionary<string, object?> CreateRequestIdHeaders(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [RequestIdPropertyName] = requestId,
        };
    }

    /// <summary>Reads the logical request UUID from AMQP headers, if present.</summary>
    internal static string? ReadRequestId(IDictionary<string, object?>? headers)
    {
        if (headers is null
            || !headers.TryGetValue(RequestIdPropertyName, out var value)
            || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            ReadOnlyMemory<byte> memory => Encoding.UTF8.GetString(memory.Span),
            _ => null,
        };
    }

    private static IDictionary<string, object?>? ToMutable(IReadOnlyDictionary<string, object?>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        return new Dictionary<string, object?>(arguments, StringComparer.Ordinal);
    }
}
