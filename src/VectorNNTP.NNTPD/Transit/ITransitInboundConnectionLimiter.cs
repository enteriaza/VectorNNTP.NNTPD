namespace VectorNNTP.NNTPD.Transit;

/// <summary>
/// Counts active inbound connections associated with a named Transit peer.
/// </summary>
public interface ITransitInboundConnectionLimiter
{
    /// <summary>
    /// Attempts to consume one inbound slot for <paramref name="peerName"/> using the
    /// current snapshot's <c>MaxIncomingConnections</c>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a slot was taken (or no slot is required).
    /// <see langword="false"/> when the peer is unknown or already at its current limit.
    /// </returns>
    bool TryAcquire(string peerName, out TransitInboundConnectionLease lease);
}

/// <summary>Releases one inbound Transit peer slot when disposed.</summary>
public readonly struct TransitInboundConnectionLease : IDisposable
{
    private readonly Action<string>? _release;
    private readonly string? _peerName;

    internal TransitInboundConnectionLease(Action<string> release, string peerName)
    {
        _release = release;
        _peerName = peerName;
    }

    /// <summary>Empty lease (no slot was taken).</summary>
    public static TransitInboundConnectionLease None => default;

    /// <summary>Gets whether this lease holds a slot.</summary>
    public bool IsHeld => _release is not null && _peerName is not null;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_release is not null && _peerName is not null)
        {
            _release(_peerName);
        }
    }
}
