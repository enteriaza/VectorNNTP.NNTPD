using System.Collections.Concurrent;
using VectorNNTP.NNTPD.Diagnostics;
using VectorNNTP.NNTPD.Transit;

namespace VectorNNTP.NNTPD.Session;

/// <summary>
/// Process-wide established-session registry. Independent of FeedDiagnostics.
/// </summary>
public sealed class NntpSessionCensus : INntpSessionCensus
{
    private readonly ConcurrentDictionary<NntpSession, byte> _sessions = new();
    private readonly ITransitPeerMetrics? _peerMetrics;

    /// <summary>Initializes a census that optionally updates <paramref name="peerMetrics"/> on register/unregister.</summary>
    public NntpSessionCensus(ITransitPeerMetrics? peerMetrics = null)
    {
        _peerMetrics = peerMetrics;
    }

    /// <inheritdoc />
    public void Register(NntpSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _sessions[session] = 0;
        if (session.Authorization.TransitPeerName is { } peerId)
        {
            _peerMetrics?.RecordSessionRegistered(peerId);
        }
    }

    /// <inheritdoc />
    public void Unregister(NntpSession session)
    {
        if (session is null)
        {
            return;
        }

        if (_sessions.TryRemove(session, out _)
            && session.Authorization.TransitPeerName is { } peerId)
        {
            _peerMetrics?.RecordSessionUnregistered(peerId);
        }
    }

    /// <inheritdoc />
    public NntpSessionCensusSnapshot Capture()
    {
        var idle = 0;
        var receiving = 0;
        var waitingHistory = 0;
        var waitingQueue = 0;
        var waitingWindow = 0;
        var completing = 0;
        var active = 0;
        foreach (var session in _sessions.Keys)
        {
            active++;
            switch (session.ActivityState)
            {
                case FeedSessionState.Receiving:
                    receiving++;
                    break;
                case FeedSessionState.WaitingHistory:
                    waitingHistory++;
                    break;
                case FeedSessionState.WaitingQueue:
                    waitingQueue++;
                    break;
                case FeedSessionState.WaitingWindow:
                    waitingWindow++;
                    break;
                case FeedSessionState.Completing:
                    completing++;
                    break;
                default:
                    idle++;
                    break;
            }
        }

        return new NntpSessionCensusSnapshot(
            active,
            active,
            idle,
            receiving,
            waitingHistory,
            waitingQueue,
            waitingWindow,
            completing);
    }
}
