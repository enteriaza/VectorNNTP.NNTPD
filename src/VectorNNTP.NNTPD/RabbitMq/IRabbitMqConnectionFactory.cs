using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>Creates one RabbitMQ broker connection from validated configuration.</summary>
public interface IRabbitMqConnectionFactory
{
    /// <summary>
    /// Opens a broker connection. The caller owns the returned instance.
    /// </summary>
    /// <param name="options">Validated RabbitMQ options.</param>
    /// <param name="connectionName">Client-provided name advertised to the broker.</param>
    /// <param name="cancellationToken">Token used to cancel the connect attempt.</param>
    /// <returns>An open connection wrapper. Dispose closes the broker connection.</returns>
    Task<IRabbitMqConnection> ConnectAsync(
        RabbitMqOptions options,
        string connectionName,
        CancellationToken cancellationToken);
}
