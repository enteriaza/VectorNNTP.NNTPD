namespace VectorNNTP.BackFiller.RabbitMq;

/// <summary>
/// One consumed AMQP delivery. Does not wrap RabbitMQ.Client types.
/// </summary>
/// <param name="DeliveryTag">Channel-scoped delivery identity used for ACK/NACK.</param>
/// <param name="Body">Application payload bytes.</param>
/// <param name="CorrelationId">AMQP RPC correlation identity, when present.</param>
/// <param name="ReplyTo">AMQP reply destination, when present.</param>
/// <param name="ContentType">AMQP content type, when present.</param>
/// <param name="RequestIdHeader">AMQP <c>RequestId</c> header, when present. Not a JSON field.</param>
/// <param name="Redelivered">Whether the broker marked the delivery as a redelivery.</param>
/// <param name="RoutingKey">Routing key, when present.</param>
/// <param name="Exchange">Exchange, when present.</param>
/// <param name="ConsumerTag">Consumer tag, when present.</param>
/// <param name="Generation">Connection generation of the consuming channel.</param>
public readonly record struct BackFillerRabbitMqConsumedDelivery(
    ulong DeliveryTag,
    ReadOnlyMemory<byte> Body,
    string? CorrelationId,
    string? ReplyTo,
    string? ContentType,
    string? RequestIdHeader,
    bool Redelivered,
    string? RoutingKey,
    string? Exchange,
    string? ConsumerTag,
    long Generation);
