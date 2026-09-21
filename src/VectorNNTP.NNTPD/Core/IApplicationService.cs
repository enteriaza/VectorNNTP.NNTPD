namespace VectorNNTP.NNTPD.Core;

/// <summary>
/// Minimal abstraction for an application-managed service whose lifetime is
/// coordinated by <see cref="ApplicationServiceManager"/>.
/// </summary>
/// <remarks>
/// Implementations must be independently testable and must not assume they run
/// on the data-plane hot path. Long-running work after <see cref="StartAsync"/>
/// may be exposed via <see cref="Execution"/> so unexpected termination can be observed.
/// </remarks>
public interface IApplicationService
{
    /// <summary>Gets a stable, human-readable name used in logs and diagnostics.</summary>
    string Name { get; }

    /// <summary>
    /// Gets an optional long-running execution task that remains active while the service is running.
    /// </summary>
    /// <remarks>
    /// When non-null, a faulted or unexpectedly completed task while the application is
    /// <see cref="ApplicationState.Running"/> is treated as unexpected service termination.
    /// Return <see langword="null"/> for services that have no background execution after start.
    /// </remarks>
    Task? Execution { get; }

    /// <summary>
    /// Starts the service asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel startup.</param>
    /// <returns>A task that completes when the service has started.</returns>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stops the service asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel or bound shutdown.</param>
    /// <returns>A task that completes when the service has stopped.</returns>
    Task StopAsync(CancellationToken cancellationToken);
}
