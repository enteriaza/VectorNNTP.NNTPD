namespace VectorNNTP.BackFiller.RabbitMq;

/// <summary>
/// One AMQP connection owned by <see cref="BackFillerRabbitMqService"/>.
/// </summary>
/// <remarks>
/// This surface is connection lifecycle plus caller-owned channel factories.
/// The service remains the sole TCP connection owner. Callers must not dispose
/// an instance obtained through a handle.
/// </remarks>
internal interface IBackFillerRabbitMqConnection : IAsyncDisposable
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
    /// The service is the only production subscriber. A lost connection does not
    /// dispose itself; the service decides when the instance is retired.
    /// </remarks>
    event EventHandler<BackFillerRabbitMqConnectionLostEventArgs>? ConnectionLost;

    /// <summary>
    /// Opens a caller-owned channel on this connection.
    /// </summary>
    /// <param name="generation">Connection generation the channel belongs to.</param>
    /// <param name="cancellationToken">Token used to cancel channel creation.</param>
    /// <returns>A channel owned by the caller. The caller must not dispose the connection.</returns>
    Task<IBackFillerRabbitMqChannel> CreateChannelAsync(long generation, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a caller-owned confirm-enabled publish channel on this connection.
    /// </summary>
    /// <param name="generation">Connection generation the channel belongs to.</param>
    /// <param name="cancellationToken">Token used to cancel channel creation.</param>
    /// <returns>A publish channel owned by the caller. The caller must not dispose the connection.</returns>
    Task<IBackFillerRabbitMqPublishChannel> CreatePublishChannelAsync(long generation, CancellationToken cancellationToken);
}
