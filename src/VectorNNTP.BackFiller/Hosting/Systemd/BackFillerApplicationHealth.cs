namespace VectorNNTP.BackFiller.Hosting.Systemd;

/// <summary>
/// Host-lifetime health used for systemd watchdog decisions.
/// </summary>
/// <remarks>
/// BackFiller does not use NNTPD's <c>ApplicationLifecycle</c> state machine. Readiness for
/// watchdog purposes means all Generic Host hosted services started successfully and shutdown
/// has not begun.
/// </remarks>
internal sealed class BackFillerApplicationHealth : IApplicationHealth
{
    private int _started;
    private int _stopping;
    private int _unexpectedTermination;

    /// <inheritdoc />
    public bool IsHealthyForWatchdog =>
        Volatile.Read(ref _started) == 1
        && Volatile.Read(ref _stopping) == 0
        && Volatile.Read(ref _unexpectedTermination) == 0;

    /// <summary>Marks successful host start (all hosted services started).</summary>
    internal void MarkStarted() => Interlocked.Exchange(ref _started, 1);

    /// <summary>Marks that graceful shutdown has begun.</summary>
    internal void MarkStopping() => Interlocked.Exchange(ref _stopping, 1);

    /// <summary>Marks an unexpected supervised-service termination.</summary>
    internal void MarkUnexpectedTermination() => Interlocked.Exchange(ref _unexpectedTermination, 1);
}
