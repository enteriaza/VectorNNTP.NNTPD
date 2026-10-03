namespace VectorNNTP.Common.Messaging.RabbitMq
{
    /// <summary>
    /// One confirm-enabled AMQP publication with caller-selected delivery and property options.
    /// </summary>
    /// <param name="Exchange">Destination exchange. Empty string is the default exchange.</param>
    /// <param name="RoutingKey">Routing key used for the publication.</param>
    /// <param name="MessageId">Fresh AMQP MessageId for this publish attempt.</param>
    /// <param name="AppId">Optional AMQP AppId.</param>
    /// <param name="CorrelationId">Optional AMQP CorrelationId.</param>
    /// <param name="ContentType">Optional AMQP content type.</param>
    /// <param name="RequestIdHeader">Optional AMQP <c>RequestId</c> header value.</param>
    /// <param name="ExpirationMilliseconds">
    /// AMQP per-message expiration in milliseconds, as a string. Whitespace omits expiration.
    /// </param>
    /// <param name="Persistent">Whether the message uses persistent delivery mode.</param>
    /// <param name="Mandatory">Whether the broker must return unroutable messages.</param>
    /// <param name="Body">Application payload bytes.</param>
    /// <param name="ContentEncoding">Optional AMQP content encoding, such as <c>utf-8</c>.</param>
    /// <param name="Timestamp">Optional AMQP timestamp. Omitted from the frame when null.</param>
    public sealed record RabbitMqConfirmedPublication(
        string Exchange,
        string RoutingKey,
        string MessageId,
        string? AppId,
        string? CorrelationId,
        string? ContentType,
        string? RequestIdHeader,
        string ExpirationMilliseconds,
        bool Persistent,
        bool Mandatory,
        ReadOnlyMemory<byte> Body,
        string? ContentEncoding = null,
        DateTimeOffset? Timestamp = null);
}
