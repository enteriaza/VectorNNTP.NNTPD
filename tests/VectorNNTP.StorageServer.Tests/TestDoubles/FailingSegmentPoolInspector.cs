using VectorNNTP.StorageServer.Storage.SegmentPools;

namespace VectorNNTP.StorageServer.Tests.TestDoubles;

/// <summary>
/// Inspector that rejects every directory. Used to prove host startup stops before the listener and advertisement.
/// </summary>
internal sealed class FailingSegmentPoolInspector : ISegmentPoolInspector
{
    /// <inheritdoc />
    public SegmentPoolInspection Inspect(
        string configuredPath,
        string? applicationBaseDirectory,
        CancellationToken cancellationToken) =>
        new(configuredPath, configuredPath, null, [], "does not exist.");
}