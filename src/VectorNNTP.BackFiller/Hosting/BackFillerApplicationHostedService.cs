using VectorNNTP.BackFiller.Core;

namespace VectorNNTP.BackFiller.Hosting;

/// <summary>
/// Integrates <see cref="ApplicationServiceManager"/> with the .NET Generic Host.
/// </summary>
/// <remarks>
/// Startup runs during <see cref="StartAsync"/> so a failed application-service
/// initialization prevents the host from reporting successful start.
/// </remarks>
public sealed class BackFillerApplicationHostedService : IHostedService
{
    private readonly ApplicationServiceManager _manager;
    private readonly IHostApplicationLifetime _lifetime;

    /// <summary>Initializes a new instance of the <see cref="BackFillerApplicationHostedService"/> class.</summary>
    public BackFillerApplicationHostedService(
        ApplicationServiceManager manager,
        IHostApplicationLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(lifetime);
        _manager = manager;
        _lifetime = lifetime;
        _manager.UnexpectedServiceTermination += OnUnexpectedServiceTermination;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => _manager.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => _manager.StopAsync(cancellationToken);

    private void OnUnexpectedServiceTermination(object? sender, UnexpectedServiceTerminationEventArgs args)
    {
        _lifetime.StopApplication();
    }
}
