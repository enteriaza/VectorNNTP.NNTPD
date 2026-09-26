namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>
/// One AMQP connection owned by <see cref="RabbitMqService"/>.
/// </summary>
/// <remarks>
/// This surface is connection lifecycle plus caller-owned channel factories.
/// <see cref="RabbitMqService"/> remains the sole TCP connection owner.
/// </remarks>
public interface IRabbitMqConnection : IAsyncDisposable
{
    /// <summary>Gets a value indicating whether the broker connection is currently open.</summary>
    bool IsOpen { get; }

    /// <summary>Gets the broker host selected for this connection.</summary>
    string Host { get; }

    /// <summary>Gets the broker port selected for this connection.</summary>
    int Port { get; }

    /// <summary>Gets the virtual host used to establish this connection.</summary>
    string VirtualHost { get; }

    /// <summary>Gets the client-provided name visible in broker diagnostics.</summary>
    string ClientProvidedName { get; }

    /// <summary>
    /// Raised when the broker or peer closes the connection.
    /// </summary>
    /// <remarks>
    /// <see cref="RabbitMqService"/> is the only production subscriber. A lost connection
    /// does not dispose itself; the service decides when the instance is retired.
    /// </remarks>
    event EventHandler<RabbitMqConnectionLostEventArgs>? ConnectionLost;

    /// <summary>
    /// Opens a short-lived channel that can declare exchanges, queues, and bindings.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel channel creation.</param>
    /// <returns>A declare-only channel owned by the caller.</returns>
    Task<IRabbitMqTopologyChannel> CreateTopologyChannelAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Opens a caller-owned channel for article-work RPC publish or consume.
    /// </summary>
    /// <param name="generation">Connection generation the channel belongs to.</param>
    /// <param name="cancellationToken">Token used to cancel channel creation.</param>
    /// <returns>An RPC channel owned by the caller. The caller must not dispose the connection.</returns>
    Task<IRabbitMqRpcChannel> CreateRpcChannelAsync(long generation, CancellationToken cancellationToken);
}
