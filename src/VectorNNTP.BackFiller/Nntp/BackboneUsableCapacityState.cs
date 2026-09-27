using System.Collections.Immutable;

namespace VectorNNTP.BackFiller.Nntp;

/// <summary>
/// Read-only view of per-backbone usable NNTP capacity used to gate Article Work consume.
/// </summary>
public interface IBackboneUsableCapacityProvider
{
    /// <summary>Raised after a snapshot is published.</summary>
    event EventHandler? SnapshotPublished;

    /// <summary>
    /// Returns whether <paramref name="backbone"/> currently has at least one ACTIVE NNTP session.
    /// </summary>
    bool HasUsableCapacityForBackbone(string backbone);

    /// <summary>Returns the published ACTIVE session count for <paramref name="backbone"/>.</summary>
    int GetUsableCapacityForBackbone(string backbone);
}

/// <summary>Publishes authoritative backbone usable-capacity snapshots.</summary>
public interface IBackboneUsableCapacityStateWriter
{
    /// <summary>
    /// Replaces the current snapshot. Non-positive counts and blank names are excluded,
    /// matching the old control-plane publisher.
    /// </summary>
    void PublishSnapshot(IReadOnlyDictionary<string, int> capacityByBackbone);
}

/// <summary>
/// Holds the latest backbone-to-ACTIVE-session-count snapshot.
/// Usable capacity is a positive ACTIVE count for that backbone.
/// </summary>
public sealed class BackboneUsableCapacityState : IBackboneUsableCapacityProvider, IBackboneUsableCapacityStateWriter
{
    private ImmutableDictionary<string, int> _capacityByBackbone =
        ImmutableDictionary<string, int>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public event EventHandler? SnapshotPublished;

    /// <inheritdoc />
    public bool HasUsableCapacityForBackbone(string backbone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backbone);
        return _capacityByBackbone.TryGetValue(backbone, out var usableCount) && usableCount > 0;
    }

    /// <inheritdoc />
    public int GetUsableCapacityForBackbone(string backbone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backbone);
        return _capacityByBackbone.TryGetValue(backbone, out var usableCount) ? usableCount : 0;
    }

    /// <inheritdoc />
    public void PublishSnapshot(IReadOnlyDictionary<string, int> capacityByBackbone)
    {
        ArgumentNullException.ThrowIfNull(capacityByBackbone);

        var builder = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (backbone, usableCount) in capacityByBackbone)
        {
            if (string.IsNullOrWhiteSpace(backbone) || usableCount <= 0)
            {
                continue;
            }

            builder[backbone] = usableCount;
        }

        _capacityByBackbone = builder.ToImmutable();
        SnapshotPublished?.Invoke(this, EventArgs.Empty);
    }
}
