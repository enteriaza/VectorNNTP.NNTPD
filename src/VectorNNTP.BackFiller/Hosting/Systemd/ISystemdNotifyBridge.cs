namespace VectorNNTP.BackFiller.Hosting.Systemd;

/// <summary>
/// Testable wrapper around the official systemd notifier.
/// </summary>
internal interface ISystemdNotifyBridge
{
    /// <summary>Gets a value indicating whether notifications are delivered to systemd.</summary>
    bool IsEnabled { get; }

    /// <summary>Sends <c>READY=1</c>.</summary>
    void NotifyReady();

    /// <summary>Sends <c>STOPPING=1</c>.</summary>
    void NotifyStopping();

    /// <summary>Sends a <c>STATUS=</c> message.</summary>
    /// <param name="status">Human-readable status without newlines or NUL.</param>
    void NotifyStatus(string status);

    /// <summary>Sends <c>WATCHDOG=1</c>.</summary>
    void NotifyWatchdog();
}
