namespace VectorNNTP.BackFiller.Hosting.Systemd;

/// <summary>
/// Snapshot of systemd environment detection for this process.
/// </summary>
public interface ISystemdRuntime
{
    /// <summary>Gets a value indicating whether the process is running on Linux.</summary>
    bool IsLinux { get; }

    /// <summary>Gets a value indicating whether the process was started as a systemd service.</summary>
    bool IsSystemdService { get; }

    /// <summary>
    /// Gets a value indicating whether the official systemd notifier is enabled
    /// (typically <c>NOTIFY_SOCKET</c> was present when the host notifier was constructed).
    /// </summary>
    bool IsNotifyEnabled { get; }

    /// <summary>Gets the systemd watchdog deadline when configured; otherwise <see langword="null"/>.</summary>
    TimeSpan? WatchdogTimeout { get; }

    /// <summary>Gets a value indicating whether a usable watchdog deadline is configured.</summary>
    bool IsWatchdogConfigured { get; }
}
