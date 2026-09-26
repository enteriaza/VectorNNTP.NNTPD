namespace VectorNNTP.BackFiller.RabbitMq;

/// <summary>
/// One confirm-enabled AMQP publication. Distinct from the consumer settlement channel.
/// </summary>
/// <param name="ReplyTo">Request ReplyTo used as the default-exchange routing key.</param>
/// <param name="CorrelationId">Request CorrelationId echoed on the response.</param>
/// <param name="ContentType">Must be <c>application/json</c>.</param>
/// <param name="MessageId">Fresh UUID for this publication attempt.</param>
/// <param name="RequestIdHeader">Logical request UUID when known. Not the CorrelationId.</param>
/// <param name="ExpirationMilliseconds">AMQP expiration, currently <c>1000</c>.</param>
/// <param name="Body">UTF-8 JSON response. Never article bytes.</param>
public sealed record BackFillerRabbitMqPublication(
    string ReplyTo,
    string CorrelationId,
    string ContentType,
    string MessageId,
    string? RequestIdHeader,
    string ExpirationMilliseconds,
    ReadOnlyMemory<byte> Body);

/// <summary>
/// Caller-owned confirm-enabled channel. Does not ACK/NACK Article Work deliveries.
/// </summary>
public interface IBackFillerRabbitMqPublishChannel : IAsyncDisposable
{
    /// <summary>Gets the connection generation this channel was opened against.</summary>
    long Generation { get; }

    /// <summary>Gets whether the channel currently reports itself open.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Publishes and waits for a broker publisher confirmation.
    /// Returning means the broker confirmed. Throwing means it did not.
    /// </summary>
    /// <param name="publication">Response publication.</param>
    /// <param name="cancellationToken">Cancels the publish/confirm wait.</param>
    Task PublishConfirmedAsync(BackFillerRabbitMqPublication publication, CancellationToken cancellationToken);
}
