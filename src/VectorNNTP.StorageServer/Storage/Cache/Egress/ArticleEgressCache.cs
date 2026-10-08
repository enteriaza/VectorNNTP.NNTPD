using System.Buffers.Binary;
using System.IO.Hashing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.StorageServer.Storage.Cache.Egress;

/// <summary>
/// Disposable NVMe read cache under <c>{ControlDir}/egress</c>.
/// </summary>
/// <remarks>
/// <para>
/// The memory index is the only directory of entries. Startup renames any previous
/// <c>live/</c> aside, deletes <c>trash-*</c> directories under <c>egress/</c>, and
/// serves with an empty index. Payload bytes are not read before the engine can
/// serve authoritative articles. A delete that fails stays in an unreclaimed list
/// until a later pass removes the path.
/// </para>
/// <para>
/// A hit checks <see cref="ArticleId"/>, sequence, ArtHash, and ArtSize, then XxHash3
/// of the payload. Location is not part of the key. Eviction removes cache records only.
/// </para>
/// </remarks>
internal sealed class ArticleEgressCache : IDisposable
{
    /// <summary>Append-only slab size. One record larger than this occupies its own slab.</summary>
    public const int DefaultSlabBytes = 16 * 1024 * 1024;

    /// <summary>Maximum queued fills. A full queue drops the fill.</summary>
    public const int DefaultFillQueueDepth = 64;

    /// <summary>Maximum queued payload bytes.</summary>
    public const long DefaultFillQueueMaxBytes = 64L * 1024 * 1024;

    /// <summary>Lossy repeat-admission slots. A collision skips a fill.</summary>
    public const int SightingSlots = 65536;

    private readonly bool _enabled;
    private readonly ILogger _logger;
    private readonly string _controlDir;
    private readonly string _liveDir;
    private readonly long _capacityBytes;
    private readonly long _reserveBytes;
    private readonly long _journalHard;
    private readonly long _journalCheckpoint;
    private readonly long _indexCheckpoint;
    private readonly int _utilizationPercent;
    private readonly int _usagePercent;
    private readonly int _freePercent;
    private readonly int _slabBytes;
    private readonly int _fillQueueDepth;
    private readonly long _fillQueueMaxBytes;
    private readonly IEgressVolumeSpace _space;
    private readonly object _index = new();
    private readonly object _queueGate = new();
    private readonly Dictionary<ArticleId, Entry> _map = new();
    private readonly LinkedList<Entry> _probation = new();
    private readonly LinkedList<Entry> _protected = new();
    private readonly Dictionary<int, Slab> _slabs = new();
    private readonly Queue<FillRequest> _queue = new();
    private readonly HashSet<PendingKey> _pending = new();
    private readonly ulong[] _sighting = new ulong[SightingSlots];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private readonly List<UnreclaimedPath> _unreclaimed = [];
    private Slab? _open;
    private int _nextSlab;
    private long _occupancy;
    private int _writing;
    private int _fillsEnabled = 1;
    private int _disposed;
    private TaskCompletionSource? _idle;
    private long _queueBytes;
    private long _unreclaimedBytes;
    private int _testFailDeletes;

    private long _hitCount;
    private long _missCount;
    private long _populateCount;
    private long _populateDroppedCount;
    private long _evictionCount;
    private long _invalidationCount;
    private long _corruptionCount;
    private long _admissionRejectedCount;

    private ArticleEgressCache()
    {
        _enabled = false;
        _logger = NullLogger.Instance;
        _controlDir = string.Empty;
        _liveDir = string.Empty;
        _space = new DriveEgressVolumeSpace(Path.GetTempPath());
        _worker = Task.CompletedTask;
    }

    private ArticleEgressCache(EgressCacheStart start, ILogger logger, string liveDir)
    {
        _enabled = true;
        _logger = logger;
        _controlDir = start.ControlDir;
        _liveDir = liveDir;
        _capacityBytes = start.CapacityBytes;
        _reserveBytes = start.ReserveBytes;
        _journalHard = Math.Max(0, start.JournalHardLimitBytes);
        _journalCheckpoint = Math.Max(0, start.JournalCheckpointThresholdBytes);
        _indexCheckpoint = Math.Max(0, start.IndexCheckpointThresholdBytes);
        _utilizationPercent = start.MaximumUtilizationPercent;
        _usagePercent = start.MaximumUsageCapacityPercent;
        _freePercent = start.FreeCapacityPercent;
        _slabBytes = start.SlabBytes > 0 ? start.SlabBytes : DefaultSlabBytes;
        _fillQueueDepth = start.FillQueueDepth > 0 ? start.FillQueueDepth : DefaultFillQueueDepth;
        _fillQueueMaxBytes = start.FillQueueMaxBytes > 0 ? start.FillQueueMaxBytes : DefaultFillQueueMaxBytes;
        _space = start.Space ?? new DriveEgressVolumeSpace(start.ControlDir);
        _testFailDeletes = start.TestFailNextDeletes;
        _worker = Task.Run(WorkerAsync);
    }

    /// <summary>A cache that performs no IO and reports no hits.</summary>
    public static ArticleEgressCache Disabled { get; } = new();

    /// <summary>Gets whether this instance can store records.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>Gets verified hits returned to readers.</summary>
    public long HitCount => Volatile.Read(ref _hitCount);

    /// <summary>Gets lookups that did not return a hit.</summary>
    public long MissCount => Volatile.Read(ref _missCount);

    /// <summary>Gets records published into the memory index.</summary>
    public long PopulateCount => Volatile.Read(ref _populateCount);

    /// <summary>Gets fills dropped because the queue was full.</summary>
    public long PopulateDroppedCount => Volatile.Read(ref _populateDroppedCount);

    /// <summary>Gets entries removed to free cache space.</summary>
    public long EvictionCount => Volatile.Read(ref _evictionCount);

    /// <summary>Gets entries dropped for identity, incarnation, or explicit removal.</summary>
    public long InvalidationCount => Volatile.Read(ref _invalidationCount);

    /// <summary>Gets records rejected by framing or ArtHash.</summary>
    public long CorruptionCount => Volatile.Read(ref _corruptionCount);

    /// <summary>Gets fills refused by capacity, reserve, or the ControlDir floor.</summary>
    public long AdmissionRejectedCount => Volatile.Read(ref _admissionRejectedCount);

    /// <summary>Gets bytes of trash or slab files whose delete has not succeeded.</summary>
    public long UnreclaimedBytes
    {
        get
        {
            lock (_index)
            {
                return _unreclaimedBytes;
            }
        }
    }

    /// <summary>
    /// Test-only. The next successful calls consume one failure each and leave the path on disk.
    /// </summary>
    internal int TestFailNextDeletes
    {
        get => Volatile.Read(ref _testFailDeletes);
        set => Volatile.Write(ref _testFailDeletes, value < 0 ? 0 : value);
    }

    /// <summary>
    /// Test-only. Invoked on the fill worker immediately before a record is admitted.
    /// A thrown exception is logged and that fill is dropped.
    /// </summary>
    internal Action? TestThrowOnPublish { get; set; }

    /// <summary>Gets live accounted bytes.</summary>
    public long CacheBytes
    {
        get
        {
            lock (_index)
            {
                return _occupancy;
            }
        }
    }

    /// <summary>Gets the configured occupancy ceiling.</summary>
    public long CapacityBytes => _capacityBytes;

    /// <summary>Gets the configured ControlDir reserve.</summary>
    public long ReserveBytes => _reserveBytes;

    /// <summary>Gets the number of live entries.</summary>
    public int EntryCount
    {
        get
        {
            lock (_index)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>Gets the number of queued fills.</summary>
    public int FillQueueDepth
    {
        get
        {
            lock (_queueGate)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>Gets queued payload bytes.</summary>
    public long FillQueueBytes
    {
        get
        {
            lock (_queueGate)
            {
                return _queueBytes;
            }
        }
    }

    /// <summary>
    /// Opens the cache or returns <see cref="Disabled"/> when capacity is zero or <c>live/</c> cannot be replaced.
    /// </summary>
    /// <param name="start">Limits and ControlDir.</param>
    /// <param name="logger">Diagnostic logger. Null uses a null logger.</param>
    /// <returns>An enabled cache, or <see cref="Disabled"/>.</returns>
    public static ArticleEgressCache Open(EgressCacheStart start, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        var log = logger ?? NullLogger.Instance;
        if (start.CapacityBytes <= 0)
        {
            return Disabled;
        }

        var root = Path.Combine(start.ControlDir, "egress");
        var live = Path.Combine(root, "live");
        ArticleEgressCache? cache = null;
        try
        {
            Directory.CreateDirectory(root);
            if (Directory.Exists(live))
            {
                if (IsReparsePoint(live))
                {
                    ArticleEgressCacheLogMessages.Disabled(log, start.ControlDir, "reparse");
                    return Disabled;
                }

                var trashDir = Path.Combine(root, "trash-" + Guid.NewGuid().ToString("N"));
                Directory.Move(live, trashDir);
            }

            Directory.CreateDirectory(live);
            cache = new ArticleEgressCache(start, log, live);
            cache.ReclaimOrphanedTrash();
            return cache;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            cache?.Dispose();
            ArticleEgressCacheLogMessages.Disabled(log, start.ControlDir, ex.GetType().Name);
            return Disabled;
        }
    }

    /// <summary>
    /// Copies a coherent payload. A framing or hash failure removes the entry and returns false.
    /// </summary>
    /// <param name="artId">Article identity.</param>
    /// <param name="sequence">Live index sequence.</param>
    /// <param name="artHash">Live index ArtHash.</param>
    /// <param name="artSize">Live index ArtSize.</param>
    /// <param name="payload">Canonical ArtData when this returns true.</param>
    /// <returns>True when the bytes match the requested incarnation.</returns>
    public bool TryCopy(
        ArticleId artId,
        ulong sequence,
        ulong artHash,
        int artSize,
        out byte[] payload)
    {
        payload = [];
        if (!_enabled)
        {
            return false;
        }

        Slab? slab;
        long offset;
        int length;
        lock (_index)
        {
            if (!_map.TryGetValue(artId, out var entry))
            {
                Interlocked.Increment(ref _missCount);
                return false;
            }

            if (entry.Sequence != sequence || entry.ArtHash != artHash || entry.ArtSize != artSize)
            {
                Unlink(entry);
                Interlocked.Increment(ref _invalidationCount);
                Interlocked.Increment(ref _missCount);
                return false;
            }

            entry.Referenced = true;
            if (entry.Probation)
            {
                entry.Probation = false;
                _probation.Remove(entry);
                _protected.AddFirst(entry);
            }

            slab = entry.Slab;
            offset = entry.Offset;
            length = entry.Length;
            slab.Pins++;
        }

        byte[] record;
        try
        {
            record = ReadRecord(slab, offset, length);
        }
        finally
        {
            lock (_index)
            {
                slab.Pins--;
                RetireIfEmpty(slab);
            }
        }

        if (!TrySliceVerified(record, artId, sequence, artHash, artSize, out payload))
        {
            Drop(artId);
            Interlocked.Increment(ref _corruptionCount);
            Interlocked.Increment(ref _missCount);
            return false;
        }

        Interlocked.Increment(ref _hitCount);
        return true;
    }

    /// <summary>
    /// Queues a fill after a proved SATA read. Never throws. A full queue drops the fill.
    /// </summary>
    /// <param name="artId">Article identity.</param>
    /// <param name="sequence">Live sequence.</param>
    /// <param name="artHash">Proved ArtHash.</param>
    /// <param name="artSize">Proved ArtSize.</param>
    /// <param name="payload">Proved ArtData.</param>
    /// <param name="waiterCount">Other callers joined to the same coalesced read.</param>
    public void ConsiderFill(
        ArticleId artId,
        ulong sequence,
        ulong artHash,
        int artSize,
        ReadOnlySpan<byte> payload,
        int waiterCount)
    {
        if (!_enabled || Volatile.Read(ref _fillsEnabled) == 0 || payload.Length != artSize || artSize < 1)
        {
            return;
        }

        if (EgressCacheRecordCodec.RecordLength(artSize) > _capacityBytes)
        {
            Interlocked.Increment(ref _admissionRejectedCount);
            return;
        }

        var key = SightingKey(artId, sequence);
        var slot = (int)(key & (SightingSlots - 1));
        var pending = new PendingKey(artId, sequence);
        var reserved = false;
        lock (_queueGate)
        {
            if (waiterCount < 1 && _sighting[slot] != key)
            {
                _sighting[slot] = key;
                return;
            }

            _sighting[slot] = key;
            if (_pending.Contains(pending))
            {
                return;
            }

            lock (_index)
            {
                if (_map.TryGetValue(artId, out var existing)
                    && existing.Sequence == sequence
                    && existing.ArtHash == artHash
                    && existing.ArtSize == artSize)
                {
                    return;
                }
            }

            if (_queue.Count >= _fillQueueDepth || _queueBytes + payload.Length > _fillQueueMaxBytes)
            {
                Interlocked.Increment(ref _populateDroppedCount);
                return;
            }

            _pending.Add(pending);
            reserved = true;
        }

        byte[] copy;
        try
        {
            copy = payload.ToArray();
        }
        catch (Exception)
        {
            if (reserved)
            {
                lock (_queueGate)
                {
                    _pending.Remove(pending);
                }
            }

            return;
        }

        var queuedOne = false;
        lock (_queueGate)
        {
            if (_queue.Count >= _fillQueueDepth || _queueBytes + copy.Length > _fillQueueMaxBytes)
            {
                _pending.Remove(pending);
                Interlocked.Increment(ref _populateDroppedCount);
            }
            else
            {
                _queue.Enqueue(new FillRequest(artId, sequence, artHash, artSize, copy));
                _queueBytes += copy.Length;
                queuedOne = true;
            }
        }

        if (queuedOne)
        {
            _signal.Release();
        }
    }

    /// <summary>Removes <paramref name="artId"/> from the memory index when present.</summary>
    /// <param name="artId">Article identity.</param>
    public void Drop(ArticleId artId)
    {
        if (!_enabled)
        {
            return;
        }

        lock (_index)
        {
            if (_map.TryGetValue(artId, out var entry))
            {
                Unlink(entry);
                Interlocked.Increment(ref _invalidationCount);
            }
        }
    }

    /// <summary>Completes when the fill queue is empty and no write is in progress.</summary>
    /// <param name="cancellationToken">Cancels the wait. Does not cancel the write.</param>
    /// <returns>A task that completes when the worker is idle.</returns>
    public Task DrainFillsAsync(CancellationToken cancellationToken = default)
    {
        if (!_enabled)
        {
            return Task.CompletedTask;
        }

        lock (_queueGate)
        {
            if (_queue.Count == 0 && _writing == 0)
            {
                return Task.CompletedTask;
            }

            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _idle.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Stops the fill worker. Does not wait for trash deletion and does not flush for durability.</summary>
    public void Dispose()
    {
        if (!_enabled || Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        _signal.Release();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        lock (_index)
        {
            foreach (var slab in _slabs.Values)
            {
                slab.Stream?.Dispose();
                slab.Stream = null;
            }

            _slabs.Clear();
            _map.Clear();
            _open = null;
        }

        _signal.Dispose();
        _shutdown.Dispose();
    }

    /// <summary>
    /// Deletes orphaned <c>trash-*</c> directories under this cache and retries failed slab deletes.
    /// One pass. <c>live/</c>, journal files, and paths outside <c>egress/</c> are not deleted.
    /// </summary>
    internal void ReclaimOrphanedTrash()
    {
        if (!_enabled)
        {
            return;
        }

        var root = Path.GetDirectoryName(_liveDir);
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        List<UnreclaimedPath> found = [];
        List<string> removed = [];
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (!IsEgressTrashDirectory(root, dir) || IsReparsePoint(dir))
                {
                    continue;
                }

                if (TryDeleteTree(dir))
                {
                    removed.Add(dir);
                    continue;
                }

                var measured = MeasureTree(dir);
                var bytes = measured < 0 ? _capacityBytes : measured;
                found.Add(new UnreclaimedPath(dir, bytes, IsDirectory: true));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        lock (_index)
        {
            RetryPendingDeletes();
            foreach (var path in removed)
            {
                ForgetUnreclaimed(path);
            }

            foreach (var item in found)
            {
                RememberUnreclaimed(item);
            }
        }
    }

    private async Task WorkerAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                FillRequest item;
                lock (_queueGate)
                {
                    if (_queue.Count == 0)
                    {
                        _writing = 0;
                        _idle?.TrySetResult();
                        _idle = null;
                        item = default;
                    }
                    else
                    {
                        item = _queue.Dequeue();
                        _queueBytes -= item.Payload.Length;
                        _writing = 1;
                    }
                }

                if (item.Payload is null)
                {
                    await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    if (Volatile.Read(ref _fillsEnabled) != 0)
                    {
                        Publish(item);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    if (Interlocked.Exchange(ref _fillsEnabled, 0) == 1)
                    {
                        ArticleEgressCacheLogMessages.FillsStopped(_logger, _controlDir, ex);
                    }
                }
                catch (Exception ex)
                {
                    ArticleEgressCacheLogMessages.FillFaulted(_logger, _controlDir, ex);
                }
                finally
                {
                    lock (_queueGate)
                    {
                        _pending.Remove(new PendingKey(item.ArtId, item.Sequence));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ArticleEgressCacheLogMessages.FillFaulted(_logger, _controlDir, ex);
            Interlocked.Exchange(ref _fillsEnabled, 0);
        }
        finally
        {
            lock (_queueGate)
            {
                _writing = 0;
                _idle?.TrySetResult();
                _idle = null;
            }
        }
    }

    private void Publish(FillRequest item)
    {
        TestThrowOnPublish?.Invoke();
        var recordLength = EgressCacheRecordCodec.RecordLength(item.ArtSize);
        if (!TryPrepareSpace(recordLength))
        {
            return;
        }

        Slab slab;
        long offset;
        lock (_index)
        {
            if (_map.TryGetValue(item.ArtId, out var existing)
                && existing.Sequence == item.Sequence
                && existing.ArtHash == item.ArtHash
                && existing.ArtSize == item.ArtSize)
            {
                return;
            }

            slab = ReserveSlab(recordLength);
            offset = slab.Length;
            slab.Length += recordLength;
            slab.Pins++;
        }

        var written = false;
        try
        {
            var buffer = new byte[recordLength];
            EgressCacheRecordCodec.Write(buffer, item.ArtId, item.Sequence, item.ArtHash, item.ArtSize, item.Payload);
            lock (slab.Gate)
            {
                if (slab.Stream is null)
                {
                    throw new IOException("Egress slab was closed before the record was published.");
                }

                slab.Stream.Position = offset;
                slab.Stream.Write(buffer);
                slab.Stream.Flush(flushToDisk: false);
            }

            written = true;
        }
        finally
        {
            lock (_index)
            {
                slab.Pins--;
                if (!written)
                {
                    slab.Length = offset;
                    RetireIfEmpty(slab);
                }
            }
        }

        lock (_index)
        {
            if (_map.TryGetValue(item.ArtId, out var previous))
            {
                Unlink(previous);
            }

            var entry = new Entry
            {
                Id = item.ArtId,
                Sequence = item.Sequence,
                ArtHash = item.ArtHash,
                ArtSize = item.ArtSize,
                Slab = slab,
                Offset = offset,
                Length = recordLength,
                Probation = true,
            };
            slab.LiveEntries++;
            _map[item.ArtId] = entry;
            _probation.AddFirst(entry);
            _occupancy += recordLength;
            Interlocked.Increment(ref _populateCount);
        }
    }

    private bool TryPrepareSpace(int recordLength)
    {
        if (recordLength < 1 || (long)recordLength > _capacityBytes)
        {
            Interlocked.Increment(ref _admissionRejectedCount);
            return false;
        }

        EvictForFloor();
        if (!SpaceAllows(recordLength))
        {
            EvictForFloor();
        }

        if (!SpaceAllows(recordLength))
        {
            Interlocked.Increment(ref _admissionRejectedCount);
            return false;
        }

        var high = Percent(_capacityBytes, _usagePercent);
        var low = Percent(_capacityBytes, Math.Max(0, _usagePercent - _freePercent));
        lock (_index)
        {
            RetryPendingDeletes();
            if (RecordCannotFit(recordLength))
            {
                Interlocked.Increment(ref _admissionRejectedCount);
                return false;
            }

            if (_occupancy >= high)
            {
                while (_occupancy > low && TryEvictOne())
                {
                }
            }

            while (_occupancy + _unreclaimedBytes + recordLength > _capacityBytes && TryEvictOne())
            {
            }

            if (_occupancy + _unreclaimedBytes + recordLength > _capacityBytes)
            {
                Interlocked.Increment(ref _admissionRejectedCount);
                return false;
            }
        }

        if (!SpaceAllows(recordLength))
        {
            Interlocked.Increment(ref _admissionRejectedCount);
            return false;
        }

        return true;
    }

    private void EvictForFloor()
    {
        if (!_space.TryRead(out var available, out var total))
        {
            return;
        }

        var guard = 0;
        while (available < ProtectedFree(total) && guard++ < 1_000_000)
        {
            lock (_index)
            {
                if (!TryEvictOne())
                {
                    return;
                }
            }

            if (!_space.TryRead(out available, out total))
            {
                return;
            }
        }
    }

    private bool SpaceAllows(int recordLength)
    {
        if (!_space.TryRead(out var available, out var total))
        {
            return false;
        }

        if (available < recordLength)
        {
            return false;
        }

        return available - recordLength >= ProtectedFree(total);
    }

    private long ProtectedFree(long totalBytes)
    {
        var sum = SaturatingAdd(_reserveBytes, _journalHard);
        sum = SaturatingAdd(sum, _journalCheckpoint);
        sum = SaturatingAdd(sum, _indexCheckpoint);
        if (totalBytes > 0 && _utilizationPercent is >= 0 and < 100)
        {
            var complement = 100 - _utilizationPercent;
            var floor = (totalBytes / 100 * complement) + (totalBytes % 100 * complement / 100);
            sum = SaturatingAdd(sum, floor);
        }

        return sum;
    }

    private bool TryEvictOne()
    {
        var victim = TakeVictim();
        if (victim is null)
        {
            return false;
        }

        Unlink(victim);
        Interlocked.Increment(ref _evictionCount);
        return true;
    }

    private Entry? TakeVictim()
    {
        var victim = TakeUnreferenced(_probation) ?? TakeUnreferenced(_protected);
        if (victim is not null)
        {
            return victim;
        }

        foreach (var entry in _probation)
        {
            entry.Referenced = false;
        }

        foreach (var entry in _protected)
        {
            entry.Referenced = false;
        }

        return TakeUnreferenced(_probation) ?? TakeUnreferenced(_protected) ?? TakeTail(_probation) ?? TakeTail(_protected);
    }

    private static Entry? TakeUnreferenced(LinkedList<Entry> list)
    {
        var node = list.Last;
        while (node is not null)
        {
            if (!node.Value.Referenced)
            {
                return node.Value;
            }

            node = node.Previous;
        }

        return null;
    }

    private static Entry? TakeTail(LinkedList<Entry> list) => list.Last?.Value;

    private void Unlink(Entry entry)
    {
        _map.Remove(entry.Id);
        if (entry.Probation)
        {
            _probation.Remove(entry);
        }
        else
        {
            _protected.Remove(entry);
        }

        _occupancy -= entry.Length;
        if (_occupancy < 0)
        {
            _occupancy = 0;
        }

        entry.Slab.LiveEntries--;
        RetireIfEmpty(entry.Slab);
    }

    private void RetireIfEmpty(Slab slab)
    {
        if (slab.LiveEntries > 0 || slab.Pins > 0)
        {
            return;
        }

        if (ReferenceEquals(slab, _open))
        {
            _open = null;
        }

        _slabs.Remove(slab.Id);
        slab.Stream?.Dispose();
        slab.Stream = null;
        var bytes = PhysicalLength(slab);
        if (TryDeleteFile(slab.Path))
        {
            return;
        }

        RememberUnreclaimed(new UnreclaimedPath(slab.Path, bytes, IsDirectory: false));
    }

    private Slab ReserveSlab(int recordLength)
    {
        if (_open is null || (_open.Length > 0 && _open.Length + recordLength > _slabBytes))
        {
            var id = ++_nextSlab;
            var path = Path.Combine(_liveDir, FormattableString.Invariant($"slab-{id:D8}.bin"));
            var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            _open = new Slab(id, stream, path);
            _slabs[id] = _open;
        }

        return _open;
    }

    private static byte[] ReadRecord(Slab slab, long offset, int length)
    {
        var buffer = new byte[length];
        lock (slab.Gate)
        {
            if (slab.Stream is null)
            {
                return [];
            }

            slab.Stream.Position = offset;
            var read = slab.Stream.Read(buffer, 0, buffer.Length);
            if (read != buffer.Length)
            {
                return [];
            }
        }

        return buffer;
    }

    private static bool TrySliceVerified(
        byte[] record,
        ArticleId artId,
        ulong sequence,
        ulong artHash,
        int artSize,
        out byte[] payload)
    {
        payload = [];
        if (!EgressCacheRecordCodec.TryReadHeader(record, out var storedId, out var storedSequence, out var storedHash, out var storedSize))
        {
            return false;
        }

        if (storedId != artId || storedSequence != sequence || storedHash != artHash || storedSize != artSize)
        {
            return false;
        }

        if (record.Length != EgressCacheRecordCodec.RecordLength(artSize))
        {
            return false;
        }

        if (XxHash3.HashToUInt64(record.AsSpan(EgressCacheRecordCodec.HeaderBytes, artSize)) != artHash)
        {
            return false;
        }

        payload = record[EgressCacheRecordCodec.HeaderBytes..];
        return true;
    }

    private static ulong SightingKey(ArticleId artId, ulong sequence)
    {
        Span<byte> digest = stackalloc byte[ArticleId.Length];
        artId.CopyTo(digest);
        var mixed = BinaryPrimitives.ReadUInt64LittleEndian(digest)
            ^ BinaryPrimitives.ReadUInt64LittleEndian(digest[8..])
            ^ sequence;
        return mixed == 0 ? 1 : mixed;
    }

    private static long Percent(long total, int percent)
    {
        if (percent <= 0 || total <= 0)
        {
            return 0;
        }

        return total / 100 * percent;
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right <= 0)
        {
            return left;
        }

        if (left > long.MaxValue - right)
        {
            return long.MaxValue;
        }

        return left + right;
    }

    /// <summary>
    /// True when live eviction cannot make <paramref name="recordLength"/> fit
    /// because unreclaimed files already consume the ceiling.
    /// </summary>
    /// <param name="recordLength">On-disk record length.</param>
    /// <returns>True when the record must be rejected without eviction.</returns>
    private bool RecordCannotFit(int recordLength)
    {
        if (_unreclaimedBytes >= _capacityBytes)
        {
            return true;
        }

        return (long)recordLength > _capacityBytes - _unreclaimedBytes;
    }

    /// <summary>Adds a path that is still on disk. A second add of the same path does not double-count.</summary>
    /// <param name="item">Path and the bytes still allocated.</param>
    private void RememberUnreclaimed(UnreclaimedPath item)
    {
        foreach (var existing in _unreclaimed)
        {
            if (PathsEqual(existing.Path, item.Path))
            {
                return;
            }
        }

        _unreclaimed.Add(item);
        _unreclaimedBytes = SaturatingAdd(_unreclaimedBytes, item.Bytes);
        ArticleEgressCacheLogMessages.ReclaimDeferred(_logger, _controlDir, item.Path);
    }

    /// <summary>Drops accounting for a path that is no longer on disk.</summary>
    /// <param name="path">Trash directory or slab file.</param>
    private void ForgetUnreclaimed(string path)
    {
        for (var i = _unreclaimed.Count - 1; i >= 0; i--)
        {
            if (!PathsEqual(_unreclaimed[i].Path, path))
            {
                continue;
            }

            _unreclaimedBytes -= _unreclaimed[i].Bytes;
            if (_unreclaimedBytes < 0)
            {
                _unreclaimedBytes = 0;
            }

            _unreclaimed.RemoveAt(i);
        }
    }

    /// <summary>One delete attempt for each pending path. Failures stay on the list.</summary>
    private void RetryPendingDeletes()
    {
        for (var i = _unreclaimed.Count - 1; i >= 0; i--)
        {
            var item = _unreclaimed[i];
            var deleted = item.IsDirectory ? TryDeleteTree(item.Path) : TryDeleteFile(item.Path);
            if (!deleted)
            {
                continue;
            }

            _unreclaimedBytes -= item.Bytes;
            if (_unreclaimedBytes < 0)
            {
                _unreclaimedBytes = 0;
            }

            _unreclaimed.RemoveAt(i);
        }
    }

    /// <summary>Deletes a trash directory. An injected failure leaves the directory in place.</summary>
    /// <param name="path">Direct child of <c>egress/</c> whose name starts with <c>trash-</c>.</param>
    /// <returns>True when the directory is gone.</returns>
    private bool TryDeleteTree(string path)
    {
        if (ConsumeInjectedDeleteFailure())
        {
            return false;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Deletes a slab file. An injected failure leaves the file in place.</summary>
    /// <param name="path">Slab path under <c>live/</c>.</param>
    /// <returns>True when the file is gone.</returns>
    private bool TryDeleteFile(string path)
    {
        if (ConsumeInjectedDeleteFailure())
        {
            return false;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Consumes one injected delete failure when the test counter is positive.</summary>
    /// <returns>True when this attempt must fail without touching the path.</returns>
    private bool ConsumeInjectedDeleteFailure()
    {
        var remaining = Volatile.Read(ref _testFailDeletes);
        while (remaining > 0)
        {
            if (Interlocked.CompareExchange(ref _testFailDeletes, remaining - 1, remaining) == remaining)
            {
                return true;
            }

            remaining = Volatile.Read(ref _testFailDeletes);
        }

        return false;
    }

    /// <summary>Sums file lengths under <paramref name="directory"/>. Does not follow reparse points.</summary>
    /// <param name="directory">Trash directory.</param>
    /// <returns>The byte sum, or <c>-1</c> when the tree cannot be measured.</returns>
    private static long MeasureTree(string directory)
    {
        try
        {
            long sum = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (IsReparsePoint(file))
                {
                    continue;
                }

                sum = SaturatingAdd(sum, new FileInfo(file).Length);
            }

            return sum;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>True for a direct <c>egress/trash-*</c> child that is not <c>live</c>.</summary>
    /// <param name="egressRoot"><c>{ControlDir}/egress</c>.</param>
    /// <param name="candidate">Directory returned by enumeration.</param>
    /// <returns>True when the cache may delete <paramref name="candidate"/>.</returns>
    private static bool IsEgressTrashDirectory(string egressRoot, string candidate)
    {
        var name = Path.GetFileName(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)
            || !name.StartsWith("trash-", StringComparison.Ordinal)
            || name.Length <= "trash-".Length)
        {
            return false;
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(candidate));
        return parent is not null
            && string.Equals(parent, Path.GetFullPath(egressRoot), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="path"/> is a junction or symlink.</summary>
    /// <param name="path">File or directory.</param>
    /// <returns>True when the cache must not delete <paramref name="path"/>.</returns>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Bytes still allocated by a slab whose delete has not been attempted yet.</summary>
    /// <param name="slab">Empty slab removed from the live map.</param>
    /// <returns>The larger of the logical slab length and the file length.</returns>
    private static long PhysicalLength(Slab slab)
    {
        var bytes = slab.Length;
        try
        {
            var info = new FileInfo(slab.Path);
            if (info.Exists && info.Length > bytes)
            {
                bytes = info.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return bytes < 0 ? 0 : bytes;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public ArticleId Id { get; set; }

        public ulong Sequence { get; set; }

        public ulong ArtHash { get; set; }

        public int ArtSize { get; set; }

        public Slab Slab { get; set; } = null!;

        public long Offset { get; set; }

        public int Length { get; set; }

        public bool Referenced { get; set; }

        public bool Probation { get; set; }
    }

    private sealed class Slab(int id, FileStream stream, string path)
    {
        public int Id { get; } = id;

        public FileStream? Stream { get; set; } = stream;

        public string Path { get; } = path;

        public long Length { get; set; }

        public int LiveEntries { get; set; }

        public int Pins { get; set; }

        public object Gate { get; } = new();
    }

    private readonly struct FillRequest(
        ArticleId artId,
        ulong sequence,
        ulong artHash,
        int artSize,
        byte[] payload)
    {
        public ArticleId ArtId { get; } = artId;

        public ulong Sequence { get; } = sequence;

        public ulong ArtHash { get; } = artHash;

        public int ArtSize { get; } = artSize;

        public byte[] Payload { get; } = payload;
    }

    private readonly record struct PendingKey(ArticleId ArtId, ulong Sequence);

    /// <summary>A trash directory or slab file that still occupies ControlDir bytes.</summary>
    /// <param name="Path">Absolute or cache-relative path. Not article payload.</param>
    /// <param name="Bytes">Bytes still allocated. Zero is a directory with no files.</param>
    /// <param name="IsDirectory">True when <see cref="Path"/> is a trash directory.</param>
    private readonly record struct UnreclaimedPath(string Path, long Bytes, bool IsDirectory);
}
