namespace VectorNNTP.BackFiller.RabbitMq;

/// <summary>
/// One confirm-enabled Article Work response publication. Distinct from the consumer settlement channel.
/// </summary>
/// <param name="ReplyTo">Request ReplyTo used as the default-exchange routing key.</param>
/// <param name="CorrelationId">Request CorrelationId echoed on the response.</param>
/// <param name="ContentType">Must be <c>application/json</c>.</param>
/// <param name="MessageId">Fresh UUID for this publication attempt.</param>
/// <param name="RequestIdHeader">Logical request UUID when known. Not the CorrelationId.</param>
/// <param name="ExpirationMilliseconds">AMQP expiration, currently <c>1000</c>.</param>
/// <param name="Body">UTF-8 JSON response. Never article bytes.</param>
internal sealed record BackFillerRabbitMqPublication(
    string ReplyTo,
    string CorrelationId,
    string ContentType,
    string MessageId,
    string? RequestIdHeader,
    string ExpirationMilliseconds,
    ReadOnlyMemory<byte> Body);
