namespace VectorNNTP.BackFiller.RabbitMq;

/// <summary>
/// One confirm-enabled Article Work response publication. Distinct from the consumer settlement channel.
/// </summary>
/// <param name="ReplyTo">Request ReplyTo used as the default-exchange routing key.</param>
/// <param name="CorrelationId">Request CorrelationId echoed on the response.</param>
/// <param name="ContentType">
/// AMQP content type supplied by the caller. Article Work responses use <c>application/json</c>.
/// This record does not validate the value.
/// </param>
/// <param name="MessageId">
/// Caller-supplied AMQP message id. The Article Work publisher passes a new UUID string on each attempt.
/// </param>
/// <param name="RequestIdHeader">Logical request UUID when known. Not the CorrelationId.</param>
/// <param name="ExpirationMilliseconds">
/// Caller-supplied AMQP expiration, in milliseconds, as text. Article Work responses use <c>1000</c>.
/// This record does not validate the value.
/// </param>
/// <param name="Body">UTF-8 JSON response. Never article bytes.</param>
internal sealed record BackFillerRabbitMqPublication(
    string ReplyTo,
    string CorrelationId,
    string ContentType,
    string MessageId,
    string? RequestIdHeader,
    string ExpirationMilliseconds,
    ReadOnlyMemory<byte> Body);
