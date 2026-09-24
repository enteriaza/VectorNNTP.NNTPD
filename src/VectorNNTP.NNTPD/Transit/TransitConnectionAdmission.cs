using VectorNNTP.NNTPD.Networking.Transport;
using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Session.Commands;

namespace VectorNNTP.NNTPD.Transit;

/// <summary>
/// Admits an inbound connection against the identified Transit peer's MaxIncomingConnections.
/// </summary>
internal static class TransitConnectionAdmission
{
    /// <summary>
    /// Consumes a slot when the session is associated with a Transit peer.
    /// Non-transit clients are admitted without a slot.
    /// </summary>
    public static bool TryAdmit(
        ITransitInboundConnectionLimiter limiter,
        NntpSession session,
        out TransitInboundConnectionLease lease)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentNullException.ThrowIfNull(session);
        lease = TransitInboundConnectionLease.None;
        var peerName = session.Authorization.TransitPeerName;
        if (peerName is null)
        {
            return true;
        }

        return limiter.TryAcquire(peerName, out lease);
    }

    /// <summary>
    /// Writes RFC 3977/4644 <c>400 Service temporarily unavailable</c> and completes.
    /// </summary>
    public static async ValueTask WriteUnavailableAsync(
        INntpConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var writer = new NntpResponseWriter(connection.Output);
        await writer
            .WriteLineAsync(NntpResponses.ServiceTemporarilyUnavailable, cancellationToken)
            .ConfigureAwait(false);
    }
}
