using VectorNNTP.BackFiller.Configuration;

namespace VectorNNTP.BackFiller.RabbitMq;

/// <summary>
/// Opens one broker connection from the validated runtime snapshot.
/// </summary>
/// <remarks>
/// <see cref="BackFillerRabbitMqService"/> is the only production caller.
/// The returned connection is owned by the service, not by consumers.
/// </remarks>
internal interface IBackFillerRabbitMqConnectionFactory
{
    /// <summary>
    /// Opens a broker connection. The caller owns the returned instance.
    /// </summary>
    /// <param name="options">Validated RabbitMQ snapshot values.</param>
    /// <param name="connectionName">Client-provided name advertised to the broker.</param>
    /// <param name="cancellationToken">Token used to cancel the connect attempt.</param>
    /// <returns>An open connection wrapper. Dispose closes the broker connection.</returns>
    Task<IBackFillerRabbitMqConnection> ConnectAsync(
        BackFillerRabbitMqRuntimeOptions options,
        string connectionName,
        CancellationToken cancellationToken);
}
