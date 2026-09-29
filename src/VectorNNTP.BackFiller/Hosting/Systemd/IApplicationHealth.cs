namespace VectorNNTP.BackFiller.Hosting.Systemd;

/// <summary>
/// Application health used for systemd watchdog keep-alive decisions.
/// </summary>
public interface IApplicationHealth
{
    /// <summary>
    /// Gets a value indicating whether watchdog keep-alives may be sent.
    /// </summary>
    /// <remarks>
    /// True only after successful host start (all hosted services started), while shutdown
    /// has not begun, and no unexpected supervised-service termination has been observed.
    /// </remarks>
    bool IsHealthyForWatchdog { get; }
}
