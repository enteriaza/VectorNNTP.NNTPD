namespace VectorNNTP.StorageServer.Storage.Engine.Memory;

/// <summary>In-memory segment catalogue with live/dead accounting and Retired fencing.</summary>
public sealed class MemorySegmentCatalogue : ISegmentCatalogue
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, SegmentInfo> _entries = new();
    private ulong _nextGeneration = 1;

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

    /// <inheritdoc />
    public bool TryRetire(SegmentId segmentId, ulong expectedGeneration, DateTimeOffset utcNow)
    {
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

    /// <summary>Allocates the next catalogue generation (tests / engine).</summary>
    public ulong AllocateGeneration()
    {
        lock (_gate)
        {
            return _nextGeneration++;
        }
    }

    /// <summary>Applies live/dead delta after Present commit, eviction, or relocate accounting.</summary>
    public void ApplyLiveDeadDelta(SegmentId segmentId, long liveDelta, long deadDelta, long sizeBytes)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(segmentId.Value, out var existing))
            {
                existing = new SegmentInfo(
                    segmentId,
                    SegmentState.Active,
                    Generation: _nextGeneration++,
                    sizeBytes,
                    LiveBytes: 0,
                    DeadBytes: 0,
                    CreatedUtc: DateTimeOffset.UtcNow,
                    ClosedUtc: null);
            }

            var live = Math.Max(0L, existing.LiveBytes + liveDelta);
            var dead = Math.Max(0L, existing.DeadBytes + deadDelta);
            var size = Math.Max(existing.SizeBytes, sizeBytes);
            _entries[segmentId.Value] = existing with
            {
                LiveBytes = live,
                DeadBytes = dead,
                SizeBytes = size,
            };
        }
    }

    /// <summary>Returns a deep-enough copy of segment bytes accounting for immutability tests.</summary>
    public bool TryGetRaw(SegmentId segmentId, out SegmentInfo info) => TryGet(segmentId, out info);
}
