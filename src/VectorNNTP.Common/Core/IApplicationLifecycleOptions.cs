namespace VectorNNTP.NNTPD.Core;

/// <summary>
/// Lifecycle and graceful-shutdown knobs shared by hosts that use
/// <see cref="ApplicationLifecycle"/> / <see cref="ApplicationServiceManager"/>.
/// </summary>
public interface IApplicationLifecycleOptions
{
    /// <summary>Gets the application display name used in lifecycle logs.</summary>
    string ApplicationName { get; }

    /// <summary>Gets the overall graceful shutdown wall-clock budget.</summary>
    TimeSpan GracefulShutdownTimeout { get; }

    /// <summary>
    /// Gets the optional startup timeout. When <see langword="null"/>, startup is bounded
    /// only by the caller's cancellation token.
    /// </summary>
    TimeSpan? StartupTimeout { get; }

    /// <summary>
    /// Gets a value indicating whether the process should exit if an application service
    /// terminates unexpectedly while running.
    /// </summary>
    bool StopHostOnUnexpectedServiceTermination { get; }
}
