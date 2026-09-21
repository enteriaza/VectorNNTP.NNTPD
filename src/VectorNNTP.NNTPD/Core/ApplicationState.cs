namespace VectorNNTP.NNTPD.Core;

/// <summary>
/// Explicit application lifecycle states for VectorNNTP.NNTPD.
/// </summary>
public enum ApplicationState
{
    /// <summary>The application has been constructed but has not begun startup.</summary>
    Created = 0,

    /// <summary>Startup is in progress; application services are being initialized.</summary>
    Starting = 1,

    /// <summary>Startup completed successfully; the application is serving.</summary>
    Running = 2,

    /// <summary>Shutdown is in progress; application services are being stopped.</summary>
    Stopping = 3,

    /// <summary>Shutdown completed; the application is idle and will not restart in-process.</summary>
    Stopped = 4,
}
