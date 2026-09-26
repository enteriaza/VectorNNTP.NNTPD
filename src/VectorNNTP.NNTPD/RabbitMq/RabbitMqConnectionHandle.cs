namespace VectorNNTP.NNTPD.RabbitMq;

/// <summary>
/// Non-owning generation snapshot of a RabbitMQ connection. Not a use-lease.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RabbitMqService"/> remains the sole lifecycle owner. Capturing a handle
/// does not transfer ownership, does not increment a reference count, and does not
/// delay retirement or disposal of generation <see cref="Generation"/>.
/// </para>
/// <para>
/// <see cref="IsCurrent"/> is a point-in-time observation taken under the service gate.
/// <see langword="true"/> means that generation was the open authoritative connection
/// at the instant of the check. It is not a promise that the connection will remain
/// current, open, or undisposed across the next statement, await, or RPC.
/// </para>
/// <para>
/// After loss, <see cref="IsCurrent"/> becomes <see langword="false"/> as soon as the
/// captured connection reports closed or the service unpublished it. Disposal of that
/// connection can then run concurrently with any caller that still uses
/// <see cref="Connection"/>. Checking <see cref="IsCurrent"/> immediately before an
/// operation does not pin the connection for that operation.
/// </para>
/// <para>
/// Future RPC must treat this type as a staleness detector
/// (<see cref="Generation"/>, <see cref="IsCurrent"/>). In-flight work that cannot
/// tolerate concurrent dispose needs a separate channel lease or equivalent pin,
/// not this handle alone.
/// </para>
/// </remarks>
public readonly struct RabbitMqConnectionHandle
{
    private readonly RabbitMqService? _owner;
    private readonly IRabbitMqConnection? _connection;

    internal RabbitMqConnectionHandle(RabbitMqService owner, IRabbitMqConnection connection, long generation)
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
    /// <remarks>
    /// This reads the connection flag only. It is not synchronized with service retirement
    /// and is not a lifetime pin.
    /// </remarks>
    public bool IsOpen => _connection is { IsOpen: true };

    /// <summary>
    /// Gets a value indicating whether this generation was the service's current open connection
    /// at the instant of the check.
    /// </summary>
    /// <value>
    /// <see langword="true"/> only when the service is not stopping, this generation is still
    /// published, the handle refers to that same instance, and that instance reports open.
    /// </value>
    public bool IsCurrent => _owner is not null && _owner.IsHandleCurrent(this);

    /// <summary>
    /// Gets the captured connection instance. Must not be disposed by the caller.
    /// </summary>
    /// <remarks>
    /// The instance remains reachable after it is unpublished and after the service
    /// disposes it. Same-assembly RPC must not treat this accessor as a lease.
    /// </remarks>
    internal IRabbitMqConnection Connection =>
        _connection ?? throw new InvalidOperationException("RabbitMQ connection handle is empty.");

    internal bool RefersTo(IRabbitMqConnection connection) => ReferenceEquals(_connection, connection);
}
