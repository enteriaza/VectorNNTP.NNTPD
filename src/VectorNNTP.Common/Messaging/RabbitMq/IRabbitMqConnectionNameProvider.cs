namespace VectorNNTP.Common.Messaging.RabbitMq;

/// <summary>
/// Supplies the AMQP client-provided connection name for <see cref="RabbitMqService"/>.
/// </summary>
/// <remarks>
/// Applications own the naming convention (product prefix + FQDN). Common does not
/// read application options to invent a name.
/// </remarks>
public interface IRabbitMqConnectionNameProvider
{
    /// <summary>Returns the client-provided name advertised to the broker.</summary>
    /// <returns>A non-empty connection name such as <c>VectorNNTP.NNTPD:{fqdn}</c>.</returns>
    string GetConnectionName();
}
