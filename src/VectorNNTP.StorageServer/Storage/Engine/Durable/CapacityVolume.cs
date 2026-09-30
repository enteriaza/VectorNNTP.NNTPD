using VectorNNTP.StorageServer.Storage;

namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

/// <summary>
/// One physical volume's capacity reader and process-local reservation ledger.
/// </summary>
/// <remarks>
/// Ledger mutations are serialized by this volume. A shared volume is one instance,
/// so article, compaction, and checkpoint reservations on that volume share the lock and the ledger.
/// </remarks>
internal sealed class CapacityVolume
{
    private readonly object _gate = new();
    private readonly ProcessLocalCapacityLedger _ledger;

    /// <summary>Creates a volume bound to <paramref name="reader"/> and <paramref name="ledger"/>.</summary>
    public CapacityVolume(
        IStorageCapacityReader reader,
        ProcessLocalCapacityLedger ledger,
        StorageVolumeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Value);
        Reader = reader;
        _ledger = ledger;
        Identity = identity;
    }

    /// <summary>Gets the physical capacity source for this volume.</summary>
    public IStorageCapacityReader Reader { get; }

    /// <summary>Gets the physical volume identity.</summary>
    public StorageVolumeIdentity Identity { get; }

    /// <summary>Runs <paramref name="action"/> while this volume's ledger is exclusively held.</summary>
    public T WithLedger<T>(Func<ProcessLocalCapacityLedger, T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            return action(_ledger);
        }
    }
}

/// <summary>
/// Segment and control capacity volumes for one storage engine.
/// </summary>
/// <remarks>
/// When the directories share a physical volume, <see cref="Segment"/> and <see cref="Control"/>
/// are the same instance. When capacity admission is disabled, both are null and no volume is resolved.
/// </remarks>
internal sealed class CapacityVolumes
{
    private CapacityVolumes(CapacityVolume? segment, CapacityVolume? control)
    {
        Segment = segment;
        Control = control;
    }

    /// <summary>Disabled admission: no readers, ledgers, or volume resolution.</summary>
    public static CapacityVolumes Disabled { get; } = new(null, null);

    /// <summary>Gets the segment volume, or null when admission is disabled.</summary>
    public CapacityVolume? Segment { get; }

    /// <summary>Gets the control volume, or null when admission is disabled.</summary>
    public CapacityVolume? Control { get; }

    /// <summary>
    /// Resolves <paramref name="segmentDir"/> and <paramref name="controlDir"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Capacity is enabled and either directory's volume cannot be resolved.
    /// </exception>
    public static CapacityVolumes Resolve(
        string segmentDir,
        string controlDir,
        bool capacityEnabled,
        IStorageCapacityReader? segmentReader,
        IStorageCapacityReader? controlReader,
        IStorageVolumeProbe? volumeProbe)
    {
        if (!capacityEnabled)
        {
            return Disabled;
        }

        var probe = volumeProbe ?? OsStorageVolumeProbe.Shared;
        if (!probe.TryResolve(segmentDir, out var segmentIdentity))
        {
            throw new InvalidOperationException(
                $"Capacity admission is enabled but the volume for segment directory '{segmentDir}' cannot be resolved.");
        }

        if (!probe.TryResolve(controlDir, out var controlIdentity))
        {
            throw new InvalidOperationException(
                $"Capacity admission is enabled but the volume for control directory '{controlDir}' cannot be resolved.");
        }

        if (segmentIdentity == controlIdentity)
        {
            var reader = segmentReader ?? controlReader ?? new CacheDirectoryCapacityReader(segmentDir);
            var shared = new CapacityVolume(reader, new ProcessLocalCapacityLedger(), segmentIdentity);
            return new CapacityVolumes(shared, shared);
        }

        var segment = new CapacityVolume(
            segmentReader ?? new CacheDirectoryCapacityReader(segmentDir),
            new ProcessLocalCapacityLedger(),
            segmentIdentity);
        var control = new CapacityVolume(
            controlReader ?? new CacheDirectoryCapacityReader(controlDir),
            new ProcessLocalCapacityLedger(),
            controlIdentity);
        return new CapacityVolumes(segment, control);
    }
}
