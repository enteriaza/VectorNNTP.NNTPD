namespace VectorNNTP.BackFiller.Hosting.Systemd;

/// <summary>
/// Host-lifetime health used for systemd watchdog decisions.
/// </summary>
/// <remarks>
/// BackFiller does not use NNTPD's <c>ApplicationLifecycle</c> state machine. Readiness for
/// watchdog purposes means all Generic Host hosted services started successfully, shutdown
/// has not begun, and no unexpected supervised-service termination has been recorded.
/// Each flag is set to one with <see cref="Interlocked.Exchange(ref int, int)"/> and is never cleared.
/// </remarks>
internal sealed class BackFillerApplicationHealth : IApplicationHealth
{
    /// <summary>Non-zero after <see cref="MarkStarted"/>.</summary>
    private int _started;

    /// <summary>Non-zero after <see cref="MarkStopping"/>.</summary>
    private int _stopping;

    /// <summary>Non-zero after <see cref="MarkUnexpectedTermination"/>.</summary>
    private int _unexpectedTermination;

    /// <inheritdoc />
    public bool IsHealthyForWatchdog =>
        Volatile.Read(ref _started) == 1
        && Volatile.Read(ref _stopping) == 0
        && Volatile.Read(ref _unexpectedTermination) == 0;

    /// <summary>Marks successful host start (all hosted services started).</summary>
    /// <remarks>Leaves <see cref="_stopping"/> and <see cref="_unexpectedTermination"/> unchanged.</remarks>
    internal void MarkStarted() => Interlocked.Exchange(ref _started, 1);

    /// <summary>Marks that graceful shutdown has begun.</summary>
    /// <remarks>Sticky. A later <see cref="MarkStarted"/> does not make <see cref="IsHealthyForWatchdog"/> true.</remarks>
    internal void MarkStopping() => Interlocked.Exchange(ref _stopping, 1);

    /// <summary>Marks an unexpected supervised-service termination.</summary>
    /// <remarks>
    /// Sticky. Callers do not distinguish a faulted execution from one that completed or was canceled.
    /// </remarks>
    internal void MarkUnexpectedTermination() => Interlocked.Exchange(ref _unexpectedTermination, 1);
}
