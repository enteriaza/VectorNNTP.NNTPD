namespace VectorNNTP.BackFiller.RabbitMq;

/// <summary>
/// Non-owning generation snapshot of a RabbitMQ connection. Not a use-lease.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BackFillerRabbitMqService"/> remains the sole lifecycle owner. Capturing a handle
/// does not transfer ownership, does not increment a reference count, and does not
/// delay retirement or disposal of generation <see cref="Generation"/>.
/// </para>
/// <para>
/// <see cref="IsCurrent"/> is a point-in-time observation. <see langword="true"/> means that
/// generation was the open authoritative connection at the instant of the check.
/// </para>
/// </remarks>
public readonly struct BackFillerRabbitMqConnectionHandle
{
    private readonly BackFillerRabbitMqService? _owner;
    private readonly IBackFillerRabbitMqConnection? _connection;

    internal BackFillerRabbitMqConnectionHandle(
        BackFillerRabbitMqService owner,
        IBackFillerRabbitMqConnection connection,
        long generation)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(connection);
        _owner = owner;
        _connection = connection;
        Generation = generation;
    }

    /// <summary>Gets the generation that was current when this handle was captured.</summary>
    public long Generation { get; }

    /// <summary>
    /// Gets a value indicating whether the captured connection object currently reports itself open.
    /// </summary>
    public bool IsOpen => _connection is { IsOpen: true };

    /// <summary>
    /// Gets a value indicating whether this generation was the service's current open connection
    /// at the instant of the check.
    /// </summary>
    public bool IsCurrent => _owner is not null && _owner.IsHandleCurrent(this);

    /// <summary>
    /// Opens a caller-owned channel on the captured generation.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel channel creation.</param>
    /// <returns>A channel stamped with <see cref="Generation"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the handle is empty or the generation is no longer current.
    /// </exception>
    public Task<IBackFillerRabbitMqChannel> CreateChannelAsync(CancellationToken cancellationToken)
    {
        if (_owner is null || _connection is null)
        {
            throw new InvalidOperationException("RabbitMQ connection handle is empty.");
        }

        if (!IsCurrent)
        {
            throw new InvalidOperationException("RabbitMQ connection generation is no longer current.");
        }

        return _connection.CreateChannelAsync(Generation, cancellationToken);
    }

    /// <summary>
    /// Opens a caller-owned confirm-enabled publish channel on the captured generation.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel channel creation.</param>
    /// <returns>A publish channel stamped with <see cref="Generation"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the handle is empty or the generation is no longer current.
    /// </exception>
    public Task<IBackFillerRabbitMqPublishChannel> CreatePublishChannelAsync(CancellationToken cancellationToken)
    {
        if (_owner is null || _connection is null)
        {
            throw new InvalidOperationException("RabbitMQ connection handle is empty.");
        }

        if (!IsCurrent)
        {
            throw new InvalidOperationException("RabbitMQ connection generation is no longer current.");
        }

        return _connection.CreatePublishChannelAsync(Generation, cancellationToken);
    }

    internal IBackFillerRabbitMqConnection Connection =>
        _connection ?? throw new InvalidOperationException("RabbitMQ connection handle is empty.");

    internal bool RefersTo(IBackFillerRabbitMqConnection connection) => ReferenceEquals(_connection, connection);
}
