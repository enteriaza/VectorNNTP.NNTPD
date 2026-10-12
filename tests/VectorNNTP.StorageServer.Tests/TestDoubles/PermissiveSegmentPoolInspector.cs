using VectorNNTP.Common.Configuration;
using VectorNNTP.StorageServer.Storage.SegmentPools;

namespace VectorNNTP.StorageServer.Tests.TestDoubles;

/// <summary>
/// Host-test inspector that does not touch disks. Each path gets its own device id.
/// Production startup uses <see cref="PlatformSegmentPoolInspector"/>.
/// </summary>
internal sealed class PermissiveSegmentPoolInspector : ISegmentPoolInspector
{
    /// <inheritdoc />
    public SegmentPoolInspection Inspect(
        string configuredPath,
        string? applicationBaseDirectory,
        CancellationToken cancellationToken)
    {
        var canonical = ApplicationLocalPath.ResolveApplicationLocalPath(configuredPath, applicationBaseDirectory);
        return new SegmentPoolInspection(configuredPath, canonical, canonical, ["test:" + canonical], null);
    }
}
