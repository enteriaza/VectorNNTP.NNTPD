using Microsoft.Extensions.DependencyInjection;
using VectorNNTP.StorageServer.Storage.SegmentPools;

namespace VectorNNTP.StorageServer.Tests.TestDoubles;

/// <summary>Replaces the production pool inspector after hosting registration.</summary>
internal static class SegmentPoolTestSupport
{
    /// <summary>
    /// Installs <see cref="PermissiveSegmentPoolInspector"/> so host tests can start without a dedicated disk.
    /// </summary>
    /// <param name="services">Service collection that already contains StorageServer hosting.</param>
    internal static void UsePermissiveInspector(IServiceCollection services) =>
        ReplaceInspector(services, typeof(PermissiveSegmentPoolInspector));

    /// <summary>
    /// Installs <see cref="FailingSegmentPoolInspector"/> so host startup fails inside the real application lifecycle.
    /// </summary>
    /// <param name="services">Service collection that already contains StorageServer hosting.</param>
    internal static void UseFailingInspector(IServiceCollection services) =>
        ReplaceInspector(services, typeof(FailingSegmentPoolInspector));

    private static void ReplaceInspector(IServiceCollection services, Type implementation)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(implementation);
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(ISegmentPoolInspector))
            {
                services.RemoveAt(i);
            }
        }

        services.AddSingleton(typeof(ISegmentPoolInspector), implementation);
    }
}
