using System.Collections.Concurrent;

namespace VectorNNTP.NNTPD.Transit;

/// <summary>
/// Per-peer inbound connection slot counter that reads the current snapshot on each acquire.
/// </summary>
/// <remarks>
/// Lowering <c>MaxIncomingConnections</c> on reload does not disconnect existing holders.
/// Removing a peer rejects new acquires for that name; existing leases still release.
/// </remarks>
public sealed class TransitInboundConnectionLimiter : ITransitInboundConnectionLimiter
{
    private readonly TransitConfigurationStore _store;
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>Shared no-op limiter for tests that do not exercise inbound limits.</summary>
    public static ITransitInboundConnectionLimiter Disabled { get; } = new DisabledLimiter();

    /// <summary>Initializes a limiter bound to the active Transit snapshot.</summary>
    public TransitInboundConnectionLimiter(TransitConfigurationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>Gets the current counted connections for <paramref name="peerName"/> (tests).</summary>
    public int GetCount(string peerName) =>
        _counts.TryGetValue(peerName, out var count) ? count : 0;

    /// <inheritdoc />
    public bool TryAcquire(string peerName, out TransitInboundConnectionLease lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerName);
        lease = TransitInboundConnectionLease.None;
        if (!_store.Current.Peers.TryGetValue(peerName, out var peer))
        {
            return false;
        }

        var max = peer.MaxIncomingConnections;
        while (true)
        {
            var current = _counts.GetOrAdd(peerName, 0);
            if (current >= max)
            {
                return false;
            }

            if (_counts.TryUpdate(peerName, current + 1, current))
            {
                lease = new TransitInboundConnectionLease(Release, peerName);
                return true;
            }
        }
    }

    private void Release(string peerName)
    {
        while (true)
        {
            if (!_counts.TryGetValue(peerName, out var current) || current <= 0)
            {
                return;
            }

            if (_counts.TryUpdate(peerName, current - 1, current))
            {
                return;
            }
        }
    }

    private sealed class DisabledLimiter : ITransitInboundConnectionLimiter
    {
        public bool TryAcquire(string peerName, out TransitInboundConnectionLease lease)
        {
            lease = TransitInboundConnectionLease.None;
            return true;
        }
    }
}
