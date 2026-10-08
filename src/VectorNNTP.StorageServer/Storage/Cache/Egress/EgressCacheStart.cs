namespace VectorNNTP.StorageServer.Storage.Cache.Egress;

/// <summary>
/// Values the engine needs to open the egress cache. Null means the cache stays off.
/// </summary>
internal sealed class EgressCacheStart
{
    /// <summary>Gets the resolved ControlDir.</summary>
    public required string ControlDir { get; init; }

    /// <summary>Gets the logical occupancy ceiling. <c>0</c> does not open the cache.</summary>
    public long CapacityBytes { get; init; }

    /// <summary>Gets the extra ControlDir free-space floor.</summary>
    public long ReserveBytes { get; init; }

    /// <summary>Gets the journal outstanding-byte hard limit included in the safety floor.</summary>
    public long JournalHardLimitBytes { get; init; }

    /// <summary>Gets the journal checkpoint temp allowance. <c>0</c> adds nothing.</summary>
    public long JournalCheckpointThresholdBytes { get; init; }

    /// <summary>Gets the index checkpoint temp allowance. <c>0</c> adds nothing.</summary>
    public long IndexCheckpointThresholdBytes { get; init; }

    /// <summary>Gets <c>Storage:Capacity:MaximumUtilization</c>.</summary>
    public int MaximumUtilizationPercent { get; init; } = 90;

    /// <summary>Gets <c>Storage:Capacity:MaximumUsageCapacity</c>, applied to <see cref="CapacityBytes"/>.</summary>
    public int MaximumUsageCapacityPercent { get; init; } = 80;

    /// <summary>Gets <c>Storage:Capacity:FreeCapacity</c>, applied to <see cref="CapacityBytes"/>.</summary>
    public int FreeCapacityPercent { get; init; } = 5;

    /// <summary>Gets an injected free-space reader. Null uses <see cref="DriveEgressVolumeSpace"/>.</summary>
    public IEgressVolumeSpace? Space { get; init; }

    /// <summary>Gets the slab size. <c>0</c> uses <see cref="ArticleEgressCache.DefaultSlabBytes"/>.</summary>
    public int SlabBytes { get; init; }

    /// <summary>Gets the fill-queue article cap. <c>0</c> uses <see cref="ArticleEgressCache.DefaultFillQueueDepth"/>.</summary>
    public int FillQueueDepth { get; init; }

    /// <summary>Gets the fill-queue byte cap. <c>0</c> uses <see cref="ArticleEgressCache.DefaultFillQueueMaxBytes"/>.</summary>
    public long FillQueueMaxBytes { get; init; }
}
