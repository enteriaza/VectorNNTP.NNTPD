using VectorNNTP.NNTPD.Diagnostics;

namespace VectorNNTP.NNTPD.Session;

/// <summary>Point-in-time counts of every established NNTP session on this process.</summary>
public readonly struct NntpSessionCensusSnapshot
{
    /// <summary>Initializes a census snapshot.</summary>
    public NntpSessionCensusSnapshot(
        int active,
        int established,
        int idle,
        int receiving,
        int waitingHistory,
        int waitingQueue,
        int waitingWindow,
        int completing)
    {
        Active = active;
        Established = established;
        Idle = idle;
        Receiving = receiving;
        WaitingHistory = waitingHistory;
        WaitingQueue = waitingQueue;
        WaitingWindow = waitingWindow;
        Completing = completing;
    }

    /// <summary>Gets established session count (complete server population).</summary>
    public int Active { get; }

    /// <summary>Gets established session count (same population as <see cref="Active"/>).</summary>
    public int Established { get; }

    /// <summary>Gets sessions in <see cref="FeedSessionState.Idle"/>.</summary>
    public int Idle { get; }

    /// <summary>Gets sessions in <see cref="FeedSessionState.Receiving"/>.</summary>
    public int Receiving { get; }

    /// <summary>Gets sessions in <see cref="FeedSessionState.WaitingHistory"/>.</summary>
    public int WaitingHistory { get; }

    /// <summary>Gets sessions in <see cref="FeedSessionState.WaitingQueue"/>.</summary>
    public int WaitingQueue { get; }

    /// <summary>Gets sessions in <see cref="FeedSessionState.WaitingWindow"/>.</summary>
    public int WaitingWindow { get; }

    /// <summary>Gets sessions in <see cref="FeedSessionState.Completing"/>.</summary>
    public int Completing { get; }
}

/// <summary>
/// Authoritative registry of established NNTP sessions (readers, streamers, and transit).
/// </summary>
public interface INntpSessionCensus
{
    /// <summary>Registers a session that has entered <see cref="NntpSession.RunAsync"/>.</summary>
    void Register(NntpSession session);

    /// <summary>Unregisters a session that is leaving <see cref="NntpSession.RunAsync"/>.</summary>
    void Unregister(NntpSession session);

    /// <summary>Captures the current complete session population.</summary>
    NntpSessionCensusSnapshot Capture();
}
