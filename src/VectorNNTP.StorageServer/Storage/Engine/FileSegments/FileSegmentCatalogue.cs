namespace VectorNNTP.StorageServer.Storage.Engine.FileSegments;

/// <summary>
/// In-memory segment catalogue with Retired fencing, reconstructed from segment files on store open.
/// </summary>
/// <remarks>
/// Phase 2B does not introduce a durable catalogue database. Physical segment files under
/// CacheDir are authoritative for discovery; this catalogue tracks Size/Live/Dead/Generation
/// for the open store instance and persists Retired via on-disk <c>.retired</c> rename.
/// </remarks>
public sealed class FileSegmentCatalogue : ISegmentCatalogue
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, SegmentInfo> _entries = new();
    private ulong _nextGeneration = 1;
    private Action<SegmentId, ulong, DateTimeOffset>? _onRetire;

    /// <summary>Installs an optional retire hook (rename segment file to .retired).</summary>
    internal void SetRetireHook(Action<SegmentId, ulong, DateTimeOffset>? onRetire) =>
        _onRetire = onRetire;

    /// <inheritdoc />
    public bool TryGet(SegmentId segmentId, out SegmentInfo info)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(segmentId.Value, out info);
        }
    }

    /// <inheritdoc />
    public void Upsert(in SegmentInfo info)
    {
        lock (_gate)
        {
            UpsertUnlocked(info);
        }
    }

    /// <inheritdoc />
    public bool TryRetire(SegmentId segmentId, ulong expectedGeneration, DateTimeOffset utcNow)
    {
        Action<SegmentId, ulong, DateTimeOffset>? hook;
        lock (_gate)
        {
            if (!_entries.TryGetValue(segmentId.Value, out var existing))
            {
                return false;
            }

            if (existing.State == SegmentState.Retired
                && existing.Generation == expectedGeneration)
            {
                return true;
            }

            if (existing.State != SegmentState.Closed || existing.Generation != expectedGeneration)
            {
                return false;
            }

            hook = _onRetire;
        }

        // Rename / physical transition first so a failed rename does not leave catalogue Retired.
        hook?.Invoke(segmentId, expectedGeneration, utcNow);

        lock (_gate)
        {
            if (!_entries.TryGetValue(segmentId.Value, out var existing))
            {
                return false;
            }

            if (existing.State == SegmentState.Retired
                && existing.Generation == expectedGeneration)
            {
                return true;
            }

            if (existing.State != SegmentState.Closed || existing.Generation != expectedGeneration)
            {
                return false;
            }

            _entries[segmentId.Value] = existing with
            {
                State = SegmentState.Retired,
                ClosedUtc = existing.ClosedUtc ?? utcNow,
            };
            return true;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SegmentInfo> Snapshot()
    {
        lock (_gate)
        {
            return _entries.Values.OrderBy(static s => s.SegmentId.Value).ToArray();
        }
    }

    /// <summary>Allocates the next catalogue generation.</summary>
    public ulong AllocateGeneration()
    {
        lock (_gate)
        {
            return _nextGeneration++;
        }
    }

    /// <summary>
    /// Applies live/dead accounting without relocating bytes (tests / future eviction).
    /// Does not free physical holes — next append remains at EOF.
    /// </summary>
    public void ApplyLiveDeadDelta(SegmentId segmentId, long liveDelta, long deadDelta)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(segmentId.Value, out var existing))
            {
                throw new InvalidOperationException($"Unknown segment {segmentId}.");
            }

            _entries[segmentId.Value] = existing with
            {
                LiveBytes = Math.Max(0L, existing.LiveBytes + liveDelta),
                DeadBytes = Math.Max(0L, existing.DeadBytes + deadDelta),
            };
        }
    }

    internal void ReplaceAll(IEnumerable<SegmentInfo> entries)
    {
        lock (_gate)
        {
            _entries.Clear();
            _nextGeneration = 1;
            foreach (var info in entries.OrderBy(static e => e.SegmentId.Value))
            {
                UpsertUnlocked(info);
            }
        }
    }

    internal void RecordAppend(SegmentId segmentId, long recordBytes, long sizeBytes)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(segmentId.Value, out var existing))
            {
                throw new InvalidOperationException($"Unknown segment {segmentId}.");
            }

            if (existing.State != SegmentState.Active)
            {
                throw new InvalidOperationException($"Segment {segmentId} is not Active.");
            }

            _entries[segmentId.Value] = existing with
            {
                SizeBytes = sizeBytes,
                LiveBytes = existing.LiveBytes + recordBytes,
            };
        }
    }

    private void UpsertUnlocked(in SegmentInfo info)
    {
        if (_entries.TryGetValue(info.SegmentId.Value, out var existing))
        {
            if (existing.State == SegmentState.Retired)
            {
                throw new InvalidOperationException(
                    $"Segment {info.SegmentId} is Retired and cannot be upserted (retirement is terminal).");
            }

            if (info.Generation < existing.Generation)
            {
                throw new InvalidOperationException(
                    $"Segment {info.SegmentId} generation regression is forbidden " +
                    $"({info.Generation} < {existing.Generation}).");
            }

            if (info.State == SegmentState.Retired
                && existing.State is not (SegmentState.Closed or SegmentState.Retired))
            {
                throw new InvalidOperationException(
                    $"Segment {info.SegmentId} must be Closed before Upsert to Retired; use TryRetire.");
            }
        }

        _entries[info.SegmentId.Value] = info;
        if (info.Generation >= _nextGeneration)
        {
            _nextGeneration = info.Generation + 1;
        }
    }
}
