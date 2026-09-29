namespace VectorNNTP.StorageServer.Storage.Engine.Memory;

/// <summary>In-memory append-only segment store (deterministic contract fake).</summary>
public sealed class MemorySegmentStore : ISegmentStore
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, byte[]> _segments = new();
    private readonly Dictionary<ulong, long> _sizes = new();
    private ulong _nextSegmentId = 1;
    private ulong? _activeSegmentId;
    private long _appendCount;
    private Func<CancellationToken, Task>? _appendGate;

    /// <summary>Gets how many append operations completed (tests).</summary>
    public long AppendCount
    {
        get
        {
            lock (_gate)
            {
                return _appendCount;
            }
        }
    }

    /// <summary>
    /// Optional gate invoked before each append completes (tests: prove Accept does not wait).
    /// </summary>
    public void SetAppendGate(Func<CancellationToken, Task>? gate) =>
        Volatile.Write(ref _appendGate, gate);

    /// <summary>Exports segment bytes for cold-start recovery tests.</summary>
    public MemorySegmentStoreSnapshot ExportSnapshot()
    {
        lock (_gate)
        {
            var copy = new Dictionary<ulong, byte[]>();
            foreach (var (id, bytes) in _segments)
            {
                copy[id] = bytes.ToArray();
            }

            return new MemorySegmentStoreSnapshot(_nextSegmentId, _activeSegmentId, _appendCount, copy);
        }
    }

    /// <summary>Creates a store rehydrated from <paramref name="snapshot"/> (cold-start tests).</summary>
    public static MemorySegmentStore ImportSnapshot(MemorySegmentStoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var store = new MemorySegmentStore();
        store.LoadSnapshot(snapshot);
        return store;
    }

    private void LoadSnapshot(MemorySegmentStoreSnapshot snapshot)
    {
        lock (_gate)
        {
            _segments.Clear();
            _sizes.Clear();
            foreach (var (id, bytes) in snapshot.Segments)
            {
                var copy = bytes.ToArray();
                _segments[id] = copy;
                _sizes[id] = copy.Length;
            }

            _nextSegmentId = snapshot.NextSegmentId;
            _activeSegmentId = snapshot.ActiveSegmentId;
            _appendCount = snapshot.AppendCount;
        }
    }

    /// <inheritdoc />
    public ValueTask<IAppendableSegment> GetActiveAppenderAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_activeSegmentId is not { } active)
            {
                active = _nextSegmentId++;
                _activeSegmentId = active;
                _segments[active] = [];
                _sizes[active] = 0;
            }

            return ValueTask.FromResult<IAppendableSegment>(
                new MemoryAppendableSegment(this, new SegmentId(active)));
        }
    }

    /// <inheritdoc />
    public ValueTask CloseActiveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _activeSegmentId = null;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Corrupts the first stored byte at <paramref name="location"/> (tests).</summary>
    internal bool TryCorruptFirstByte(in StoredArticleLocation location)
    {
        lock (_gate)
        {
            if (!_segments.TryGetValue(location.SegmentId.Value, out var bytes))
            {
                return false;
            }

            if (location.Offset < 0
                || location.Length < 1
                || location.Offset >= bytes.Length)
            {
                return false;
            }

            bytes[location.Offset] ^= 0xFF;
            return true;
        }
    }

    /// <inheritdoc />
    public bool TryRead(in StoredArticleLocation location, out ReadOnlyMemory<byte> artData)
    {
        lock (_gate)
        {
            if (!_segments.TryGetValue(location.SegmentId.Value, out var bytes))
            {
                artData = default;
                return false;
            }

            if (location.Offset < 0
                || location.Length < 0
                || location.Offset > bytes.Length
                || location.Offset + location.Length > bytes.Length)
            {
                artData = default;
                return false;
            }

            artData = bytes.AsMemory((int)location.Offset, location.Length);
            return true;
        }
    }

    /// <summary>Gets the current size of a segment (tests).</summary>
    internal long GetSize(SegmentId segmentId)
    {
        lock (_gate)
        {
            return _sizes.TryGetValue(segmentId.Value, out var size) ? size : 0;
        }
    }

    internal async ValueTask<StoredArticleLocation> AppendCoreAsync(
        SegmentId segmentId,
        ReadOnlyMemory<byte> artData,
        CancellationToken cancellationToken)
    {
        var gate = Volatile.Read(ref _appendGate);
        if (gate is not null)
        {
            await gate(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_activeSegmentId != segmentId.Value)
            {
                throw new InvalidOperationException("Append target is not the active segment.");
            }

            if (!_segments.TryGetValue(segmentId.Value, out var existing))
            {
                throw new InvalidOperationException("Unknown segment.");
            }

            var offset = existing.Length;
            var grown = new byte[existing.Length + artData.Length];
            existing.AsSpan().CopyTo(grown);
            artData.Span.CopyTo(grown.AsSpan(existing.Length));
            _segments[segmentId.Value] = grown;
            _sizes[segmentId.Value] = grown.Length;
            _appendCount++;
            return new StoredArticleLocation(segmentId, offset, artData.Length);
        }
    }

    private sealed class MemoryAppendableSegment(MemorySegmentStore owner, SegmentId segmentId) : IAppendableSegment
    {
        public SegmentId SegmentId => segmentId;

        public long SizeBytes => owner.GetSize(segmentId);

        public ValueTask<StoredArticleLocation> AppendAsync(
            ReadOnlyMemory<byte> artData,
            CancellationToken cancellationToken) =>
            owner.AppendCoreAsync(segmentId, artData, cancellationToken);
    }
}
