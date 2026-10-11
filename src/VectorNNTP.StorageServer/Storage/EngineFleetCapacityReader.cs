using Microsoft.Extensions.DependencyInjection;
using VectorNNTP.StorageServer.Storage.Engine.Durable;

namespace VectorNNTP.StorageServer.Storage;

/// <summary>
/// Fleet capacity reader. Resolves the open engine on each read so advertisement startup stays
/// after the engine, and so the published triple uses the same spool accounting as admission.
/// </summary>
internal sealed class EngineFleetCapacityReader : IStorageCapacityReader
{
    private readonly IServiceProvider _services;

    /// <summary>Initializes a reader that asks the hosted engine for the current advertisement.</summary>
    /// <param name="services">Host services. The engine is resolved when <see cref="Read"/> runs.</param>
    public EngineFleetCapacityReader(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>Reads the admission budget across every configured spool.</summary>
    /// <returns>The single advertisement triple.</returns>
    /// <exception cref="InvalidOperationException">The engine is not ready, or no spool filesystem could be measured.</exception>
    public StorageCapacitySnapshot Read() =>
        _services.GetRequiredService<StorageEngineApplicationService>().Engine.ReadFleetAdvertisement();
}
