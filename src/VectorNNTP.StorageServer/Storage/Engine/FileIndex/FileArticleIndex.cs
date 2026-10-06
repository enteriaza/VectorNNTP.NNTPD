using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Configuration;

namespace VectorNNTP.StorageServer.Storage.Engine.FileIndex;

/// <summary>
/// Filesystem-backed durable article index under <see cref="ArticleStorageRuntimeOptions.ControlDir"/>.
/// </summary>
/// <remarks>
/// <para>
/// Single append-only file <c>article.index</c>. Each durable mutation appends a full
/// <see cref="StoredArticleMetadata"/> snapshot; replay is last-write-wins per
/// <see cref="ArticleId"/>. Startup reads one schema 2 or schema 3 frame at a time, so the
/// file-byte working set does not grow with the file. Durability uses
/// <see cref="FileStream.Flush(bool)"/> with <c>flushToDisk: true</c>.
/// </para>
/// <para>
/// <see cref="TouchHint"/> updates in-memory LastAccess only and must not perform a durable
/// write. Index durability is distinct from journal Accept durability and does not prove
/// physical segment bytes — <see cref="ISegmentStore"/> must still validate ArtData on read.
/// </para>
/// <para>
/// Evicted and Invalid are logical states. While the segment file still exists they stop a
/// later accept from adopting those bytes. After the segment has been physically reclaimed
/// and is absent from the catalogue, those rows are dropped from memory. The drop is not a
/// new index frame. A later checkpoint omits them. A crash before that checkpoint replays
/// the old frames, and open drops them again because the segment is gone. Present rows are
/// never dropped this way. Relocation changes index metadata only and never mutates segment bytes.
/// </para>
/// <para>
/// <see cref="WriteSnapshot"/> writes <c>article.index.snap</c> from the in-memory projection.
/// It does not append to <c>article.index</c> and does not change which mutations are
/// acknowledged. <see cref="Checkpoint"/> installs that snapshot and then replaces
/// <c>article.index</c> with a <c>VNID</c> file whose payload is only the frames past the
/// covered length. Startup loads a valid snapshot. A legacy index is then replayed from the
/// covered length. A replacement whose generation equals the snapshot is replayed from its
/// payload only. An invalid installed snapshot or an invalid replacement fails Open.
/// <c>article.index.snap.tmp</c> and <c>article.index.repl.tmp</c> are ignored.
/// </para>
/// </remarks>
public sealed class FileArticleIndex : IArticleIndex, IDisposable, IAsyncDisposable
{
    /// <summary>Engine-owned index filename beneath ControlDir.</summary>
    public const string IndexFileName = "article.index";

    /// <summary>Installed snapshot filename beneath ControlDir. Not read as startup authority.</summary>
    public const string SnapshotFileName = "article.index.snap";

    /// <summary>In-progress snapshot filename. Never authoritative.</summary>
    public const string SnapshotTempFileName = "article.index.snap.tmp";

    /// <summary>In-progress index replacement. Never authoritative.</summary>
    public const string ReplacementTempFileName = "article.index.repl.tmp";

    private const int CopyBufferBytes = 64 * 1024;

    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _snapshotFlight = new(1, 1);
    private readonly Dictionary<ArticleId, StoredArticleMetadata> _entries = new();
    private readonly LinkedList<ArticleId> _scanOrder = new();
    private readonly Dictionary<ArticleId, LinkedListNode<ArticleId>> _scanNodes = new();
    private LinkedListNode<ArticleId>? _scanCursor;
    private readonly string _indexPath;
    private FileStream _stream;
    private long _durableWriteCount;
    private long _durableFlushCount;
    private long _touchHintCount;
    private readonly Dictionary<ArticleId, long> _useCounts = new();
    private ulong _installedSnapshotGeneration;
    private long _structuralGeneration;
    private long _snapshotCallCount;
    private long _closedAccountingIndexCopies;
    private ArticleId[] _snapshotArticleIds = [];
    private CheckpointCapacityReservation? _checkpointCapacity;
    private int _snapshotWriters;
    private int _maxSnapshotWriters;
    private bool _disposed;

    private FileArticleIndex(string indexPath, FileStream stream, ILogger logger)
    {
        _indexPath = indexPath;
        _stream = stream;
        _logger = logger;
    }

    /// <summary>Gets the absolute index file path.</summary>
    public string IndexPath => _indexPath;

    /// <summary>
    /// Gets the durable frame payload eligible for prefix retirement, in bytes.
    /// </summary>
    /// <remarks>
    /// This is <c>article.index</c> length minus a <c>VNID</c> header when that header is present.
    /// A legacy log counts its entire length. The value is not the in-memory row count and not
    /// the snapshot file length.
    /// </remarks>
    public long IndexPhysicalBytes
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _stream.Length - FrameBaseUnlocked();
            }
        }
    }

    /// <summary>Number of durable mutations appended (tests).</summary>
    public long DurableWriteCount
    {
        get
        {
            lock (_gate)
            {
                return _durableWriteCount;
            }
        }
    }

    /// <summary>Number of durability flushes of the index stream. Tests only.</summary>
    internal long DurableFlushCount
    {
        get
        {
            lock (_gate)
            {
                return _durableFlushCount;
            }
        }
    }

    /// <summary>Copies the open index file. Restores the stream position. Tests only.</summary>
    internal byte[] CopyIndexBytes()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var length = _stream.Length;
            if (length > int.MaxValue)
            {
                throw new InvalidOperationException("Index test copy exceeds a single byte array.");
            }

            var buffer = new byte[(int)length];
            var position = _stream.Position;
            try
            {
                _stream.Position = 0;
                var filled = 0;
                while (filled < buffer.Length)
                {
                    var read = _stream.Read(buffer, filled, buffer.Length - filled);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("Short read copying the open article index.");
                    }

                    filled += read;
                }

                return buffer;
            }
            finally
            {
                _stream.Position = position;
            }
        }
    }

    /// <summary>Highest number of snapshot writers inside the single-flight section (tests).</summary>
    internal int MaxSnapshotWriters => Volatile.Read(ref _maxSnapshotWriters);

    /// <summary>
    /// Attaches process-local checkpoint reservations. Null leaves checkpoint IO unchanged.
    /// </summary>
    internal void AttachCheckpointCapacity(CheckpointCapacityReservation capacity)
    {
        ArgumentNullException.ThrowIfNull(capacity);
        _checkpointCapacity = capacity;
    }

    /// <summary>Invoked once a snapshot has left the index gate and before the temp file is written (tests).</summary>
    internal Action? TestDuringSnapshotWrite { get; set; }

    /// <summary>Invoked after the temp snapshot is durable and validated, before install (tests).</summary>
    internal Action? TestBeforeSnapshotInstall { get; set; }

    /// <summary>Invoked when a second snapshot observes the single-flight gate already taken (tests).</summary>
    internal Action? TestOnSnapshotFlightContended { get; set; }

    /// <summary>Invoked after snapshot bytes are written and before <c>Flush(true)</c> (tests).</summary>
    internal Action? TestBeforeSnapshotFlush { get; set; }

    /// <summary>
    /// Invoked after the stable tail has been copied and before the install gate (tests).
    /// Runs inside the checkpoint flight and outside the index gate. Must not call
    /// <see cref="Checkpoint"/> or <see cref="WriteSnapshot"/>.
    /// </summary>
    internal Action? TestDuringReplacementWrite { get; set; }

    /// <summary>
    /// Invoked under the index gate after the catch-up copy and before replacement <c>Flush(true)</c> (tests).
    /// Must not call back into this index.
    /// </summary>
    internal Action? TestBeforeReplacementFlush { get; set; }

    /// <summary>
    /// Invoked under the index gate after the replacement is durable and verified, before it is installed (tests).
    /// Must not call back into this index.
    /// </summary>
    internal Action? TestBeforeReplacementInstall { get; set; }

    /// <summary>
    /// When set, snapshot and replacement temp deletion leaves the file in place. Tests only.
    /// </summary>
    internal bool TestFailCheckpointTempDelete { get; set; }

    private long _snapshotFrameBase;
    private ulong? _installedSnapshotReservationId;
    private ulong? _retainedSnapshotTempReservationId;
    private long _retainedSnapshotTempReservationBytes;
    private ulong? _retainedReplacementReservationId;
    private long _retainedReplacementReservationBytes;
    private bool _retainSnapshotTempReservation;
    private bool _retainReplacementReservation;

    /// <summary>
    /// Invoked under the index gate before a durable frame is written (tests).
    /// Throwing leaves the index file unchanged when it runs before the write.
    /// </summary>
    internal Action? TestBeforeDurableAppend { get; set; }

    /// <summary>
    /// Invoked immediately before a durability flush of the index stream (tests).
    /// A throw means the frames written since the previous flush are not durable.
    /// </summary>
    internal Action? TestBeforeDurableFlush { get; set; }

    /// <summary>
    /// Invoked after a checkpoint replacement is authoritative and the index gate is not held.
    /// Reports Present rows in the installed snapshot and the covered length of the retired prefix.
    /// </summary>
    internal Action<IndexPrefixRetirement>? OnIndexPrefixRetired { get; set; }

    /// <summary>Number of soft TouchHint calls (tests).</summary>
    public long TouchHintCount
    {
        get
        {
            lock (_gate)
            {
                return _touchHintCount;
            }
        }
    }

    /// <summary>
    /// Opens or creates <c>article.index</c> under <paramref name="options"/>.ControlDir and
    /// restores the published projection. A valid <c>article.index.snap</c> supplies the covered
    /// prefix. A legacy index is replayed from that covered length. A <c>VNID</c> replacement
    /// whose generation equals the snapshot is replayed from its payload. Without a snapshot,
    /// a legacy index is replayed from the start. A torn final frame may be truncated.
    /// </summary>
    public static FileArticleIndex Open(
        ArticleStorageRuntimeOptions options,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ControlDir);

        var log = logger ?? NullLogger.Instance;
        Directory.CreateDirectory(options.ControlDir);
        var path = Path.Combine(options.ControlDir, IndexFileName);
        var stream = OpenIndexStream(path, FileMode.OpenOrCreate);

        var index = new FileArticleIndex(path, stream, log);
        try
        {
            index.ReplayAndRecoverUnlocked();
            FileArticleIndexLogMessages.Opened(
                log,
                path,
                stream.Length,
                index._entries.Count);
            return index;
        }
        catch
        {
            index.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public bool TryGet(ArticleId artId, out StoredArticleMetadata metadata)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _entries.TryGetValue(artId, out metadata);
        }
    }

    /// <summary>
    /// Returns a snapshot of all index entries (Present, Evicted, and Invalid).
    /// </summary>
    /// <remarks>
    /// Used to rebuild segment Live/Dead accounting after open/recovery. Does not perform IO.
    /// </remarks>
    public IReadOnlyList<StoredArticleMetadata> Snapshot()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _ = Interlocked.Increment(ref _snapshotCallCount);
            return _entries.Values.ToArray();
        }
    }

    /// <summary>Times <see cref="Snapshot"/> has copied the entry dictionary. Tests only.</summary>
    internal long SnapshotCallCount => Volatile.Read(ref _snapshotCallCount);

    /// <summary>
    /// Times closed-segment accounting has classified the index. One maintenance pass
    /// copies once while the index is unchanged. Tests only.
    /// </summary>
    internal long ClosedAccountingIndexCopies => Volatile.Read(ref _closedAccountingIndexCopies);

    /// <summary>
    /// Changes when a row is added, removed, relocated, or changes state.
    /// <see cref="TouchHint"/> does not change it.
    /// </summary>
    internal long StructuralGeneration => Volatile.Read(ref _structuralGeneration);

    /// <summary>
    /// One pass over the index. Rows whose segment is not in <paramref name="segmentIds"/>
    /// are omitted. The caller must not use the result after <see cref="StructuralGeneration"/>
    /// changes.
    /// </summary>
    internal ClosedSegmentAccountingView CopyClosedSegmentAccounting(HashSet<ulong> segmentIds)
    {
        ArgumentNullException.ThrowIfNull(segmentIds);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var rows = new Dictionary<ulong, ClosedSegmentAccountingView.SegmentRows>(segmentIds.Count);
            foreach (var metadata in _entries.Values)
            {
                var segmentId = metadata.Location.SegmentId.Value;
                if (!segmentIds.Contains(segmentId))
                {
                    continue;
                }

                if (!rows.TryGetValue(segmentId, out var bucket))
                {
                    bucket = new ClosedSegmentAccountingView.SegmentRows();
                    rows[segmentId] = bucket;
                }

                _ = bucket.Named.Add(metadata.Location);
                if (metadata.State == ArticleStorageState.Present)
                {
                    bucket.PresentBytes += metadata.Location.Length;
                }
                else if (metadata.State is ArticleStorageState.Evicted or ArticleStorageState.Invalid)
                {
                    bucket.DeadBytes += metadata.Location.Length;
                }
            }

            _ = Interlocked.Increment(ref _closedAccountingIndexCopies);
            return new ClosedSegmentAccountingView(_structuralGeneration, rows);
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void NoteStructuralChangeUnlocked()
    {
        _ = Interlocked.Increment(ref _structuralGeneration);
    }

    /// <summary>
    /// Copies article identity under the index lock. The lock is not held after this method returns.
    /// ArtData and segment locations are not copied.
    /// </summary>
    internal ArticleIndexIdentity[] CopyIdentities()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var rows = new ArticleIndexIdentity[_entries.Count];
            var index = 0;
            foreach (var metadata in _entries.Values)
            {
                rows[index++] = new ArticleIndexIdentity(
                    metadata.ArtId,
                    metadata.ArtHash,
                    metadata.ArtSize,
                    metadata.State);
            }

            return rows;
        }
    }

    /// <inheritdoc />
    public bool TryCommitPresent(in StoredArticleMetadata metadata) =>
        TryCommitPresentReporting(metadata, out _) is DurableIndexAppend.Appended or DurableIndexAppend.Unchanged;

    /// <summary>
    /// Commits Present and reports whether a new physical frame was appended.
    /// <paramref name="frameOffset"/> is the file offset of that frame when the result is
    /// <see cref="DurableIndexAppend.Appended"/>.
    /// </summary>
    internal DurableIndexAppend TryCommitPresentReporting(
        in StoredArticleMetadata metadata,
        out long frameOffset)
    {
        frameOffset = -1;
        if (metadata.State != ArticleStorageState.Present)
        {
            return DurableIndexAppend.Rejected;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(metadata.ArtId, out var existing)
                && existing.State == ArticleStorageState.Present)
            {
                if (existing.ArtHash == metadata.ArtHash
                    && existing.ArtSize == metadata.ArtSize
                    && existing.Sequence == metadata.Sequence
                    && LocationsEqual(existing.Location, metadata.Location))
                {
                    return DurableIndexAppend.Unchanged;
                }

                // Same identity + different location requires TryRelocate; hash/size conflict rejected.
                return DurableIndexAppend.Rejected;
            }

            frameOffset = _stream.Length;
            AppendDurableUnlocked(metadata);
            _entries[metadata.ArtId] = metadata;
            RememberScanOrderUnlocked(metadata.ArtId);
            NoteStructuralChangeUnlocked();
            return DurableIndexAppend.Appended;
        }
    }

    /// <summary>
    /// Appends Present frames that are not already durable, then flushes the index once.
    /// Entries change only after that flush returns. <paramref name="results"/> and
    /// <paramref name="frameOffsets"/> are parallel to <paramref name="metadata"/>.
    /// </summary>
    internal void CommitPresentBatch(
        IReadOnlyList<StoredArticleMetadata> metadata,
        DurableIndexAppend[] results,
        long[] frameOffsets)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(frameOffsets);
        if (results.Length != metadata.Count || frameOffsets.Length != metadata.Count)
        {
            throw new ArgumentException("Present batch results must align with the metadata.");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var pending = new List<(int Index, StoredArticleMetadata Metadata, byte[] Frame)>();
            var pendingIds = new HashSet<ArticleId>();
            for (var i = 0; i < metadata.Count; i++)
            {
                frameOffsets[i] = -1;
                var item = metadata[i];
                if (item.State != ArticleStorageState.Present)
                {
                    results[i] = DurableIndexAppend.Rejected;
                    continue;
                }

                if (_entries.TryGetValue(item.ArtId, out var existing)
                    && existing.State == ArticleStorageState.Present)
                {
                    results[i] = existing.ArtHash == item.ArtHash
                        && existing.ArtSize == item.ArtSize
                        && existing.Sequence == item.Sequence
                        && LocationsEqual(existing.Location, item.Location)
                        ? DurableIndexAppend.Unchanged
                        : DurableIndexAppend.Rejected;
                    continue;
                }

                if (!pendingIds.Add(item.ArtId))
                {
                    results[i] = DurableIndexAppend.Rejected;
                    continue;
                }

                pending.Add((i, item, ArticleIndexRecordCodec.Encode(item)));
            }

            if (pending.Count == 0)
            {
                return;
            }

            foreach (var entry in pending)
            {
                TestBeforeDurableAppend?.Invoke();
                frameOffsets[entry.Index] = _stream.Length;
                _stream.Seek(0, SeekOrigin.End);
                _stream.Write(entry.Frame, 0, entry.Frame.Length);
            }

            DurableIndexFlushUnlocked();
            foreach (var entry in pending)
            {
                _entries[entry.Metadata.ArtId] = entry.Metadata;
                RememberScanOrderUnlocked(entry.Metadata.ArtId);
                NoteStructuralChangeUnlocked();
                results[entry.Index] = DurableIndexAppend.Appended;
                _durableWriteCount++;
            }
        }
    }

    /// <inheritdoc />
    public ArticleRelocateOutcome TryRelocate(
        ArticleId artId,
        in StoredArticleLocation expectedLocation,
        in StoredArticleLocation newLocation,
        ulong artHash,
        int artSize) =>
        TryRelocateReporting(
            artId,
            expectedLocation,
            newLocation,
            artHash,
            artSize,
            out _);

    /// <summary>
    /// Relocates and reports the file offset of the new Present frame when a frame was appended.
    /// </summary>
    internal ArticleRelocateOutcome TryRelocateReporting(
        ArticleId artId,
        in StoredArticleLocation expectedLocation,
        in StoredArticleLocation newLocation,
        ulong artHash,
        int artSize,
        out long frameOffset)
    {
        frameOffset = -1;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(artId, out var existing)
                || existing.State != ArticleStorageState.Present)
            {
                return ArticleRelocateOutcome.NotPresent;
            }

            if (existing.ArtHash != artHash || existing.ArtSize != artSize)
            {
                return ArticleRelocateOutcome.IdentityMismatch;
            }

            if (LocationsEqual(existing.Location, newLocation))
            {
                return ArticleRelocateOutcome.IdempotentNoOp;
            }

            if (!LocationsEqual(existing.Location, expectedLocation))
            {
                return ArticleRelocateOutcome.ExpectedLocationMismatch;
            }

            var updated = existing with { Location = newLocation };
            frameOffset = _stream.Length;
            AppendDurableUnlocked(updated);
            _entries[artId] = updated;
            NoteStructuralChangeUnlocked();
            return ArticleRelocateOutcome.Relocated;
        }
    }

    /// <inheritdoc />
    public bool TrySetState(ArticleId artId, ArticleStorageState state, DateTimeOffset utcNow)
    {
        if (TryTransitionPresentOnce(artId, state, utcNow, out _, out _))
        {
            return true;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _entries.TryGetValue(artId, out var existing) && existing.State == state;
        }
    }

    /// <summary>
    /// Durably moves a Present entry to <paramref name="state"/> at most once.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> when the entry is missing, already in
    /// <paramref name="state"/>, or in the other terminal state. Callers that adjust
    /// live/dead bytes must do so only on <see langword="true"/>, using
    /// <paramref name="transitioned"/>.
    /// </remarks>
    internal bool TryTransitionPresentOnce(
        ArticleId artId,
        ArticleStorageState state,
        DateTimeOffset utcNow,
        out StoredArticleMetadata transitioned,
        out long frameOffset)
    {
        transitioned = default;
        frameOffset = -1;
        if (state is not (ArticleStorageState.Evicted or ArticleStorageState.Invalid))
        {
            return false;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(artId, out var existing)
                || existing.State != ArticleStorageState.Present)
            {
                return false;
            }

            transitioned = existing with { State = state, LastAccessUtc = utcNow };
            frameOffset = _stream.Length;
            AppendDurableUnlocked(transitioned);
            _entries[artId] = transitioned;
            NoteStructuralChangeUnlocked();
            return true;
        }
    }

    /// <summary>
    /// Invalidates a Present article only when the locked entry still matches
    /// <paramref name="expectedLocation"/>, <paramref name="expectedArtHash"/>, and
    /// <paramref name="expectedArtSize"/>.
    /// </summary>
    /// <remarks>
    /// Compare and durable transition share one index critical section. A mismatch, including
    /// a concurrent relocation, leaves the entry unchanged. Already Evicted or Invalid entries
    /// are not rewritten. Does not update segment Live/Dead accounting.
    /// </remarks>
    /// <param name="artId">Article to invalidate.</param>
    /// <param name="expectedLocation">Physical location that was read and failed.</param>
    /// <param name="expectedArtHash">ArtHash observed for that failed read.</param>
    /// <param name="expectedArtSize">ArtSize observed for that failed read.</param>
    /// <param name="utcNow">Timestamp stored on the Invalid entry.</param>
    /// <param name="transitioned">The Invalid entry written when the compare succeeds.</param>
    /// <returns><see langword="true"/> when this call transitioned Present to Invalid.</returns>
    public bool TryInvalidatePresentAt(
        ArticleId artId,
        in StoredArticleLocation expectedLocation,
        ulong expectedArtHash,
        int expectedArtSize,
        DateTimeOffset utcNow,
        out StoredArticleMetadata transitioned) =>
        TryInvalidatePresentAtReporting(
            artId,
            expectedLocation,
            expectedArtHash,
            expectedArtSize,
            utcNow,
            out transitioned,
            out _);

    /// <summary>
    /// Same compare-and-append as <see cref="TryInvalidatePresentAt"/>, also reporting the file offset
    /// of the Invalid frame when one was written.
    /// </summary>
    internal bool TryInvalidatePresentAtReporting(
        ArticleId artId,
        in StoredArticleLocation expectedLocation,
        ulong expectedArtHash,
        int expectedArtSize,
        DateTimeOffset utcNow,
        out StoredArticleMetadata transitioned,
        out long frameOffset)
    {
        transitioned = default;
        frameOffset = -1;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(artId, out var existing)
                || existing.ArtId != artId
                || existing.State != ArticleStorageState.Present
                || existing.ArtHash != expectedArtHash
                || existing.ArtSize != expectedArtSize
                || !LocationsEqual(existing.Location, expectedLocation))
            {
                return false;
            }

            var updated = existing with { State = ArticleStorageState.Invalid, LastAccessUtc = utcNow };
            frameOffset = _stream.Length;
            AppendDurableUnlocked(updated);
            _entries[artId] = updated;
            NoteStructuralChangeUnlocked();
            transitioned = updated;
            return true;
        }
    }

    /// <summary>
    /// Copies up to <paramref name="maxVisited"/> index rows into <paramref name="present"/>,
    /// continuing from the previous window. Only <see cref="ArticleStorageState.Present"/> rows
    /// are copied. The cursor also advances across Evicted and Invalid rows.
    /// </summary>
    /// <param name="maxVisited">Maximum rows to examine in this window.</param>
    /// <param name="present">Destination for Present snapshots. Filled from index 0.</param>
    /// <param name="visited">Rows examined, including non-Present rows.</param>
    /// <param name="wrapped">True when this window reached the end of the scan order.</param>
    /// <returns>The number of Present snapshots written to <paramref name="present"/>.</returns>
    /// <remarks>
    /// Holds the index lock only for this window. Successor order is a linked list updated on
    /// insert and forget, so the walk does not rescan the retained population to find the cursor.
    /// A row removed during the walk is skipped. Reaching the end arms the next call at the first row.
    /// </remarks>
    internal int CopyRetentionWindow(
        int maxVisited,
        Span<RetentionScanCandidate> present,
        out int visited,
        out bool wrapped)
    {
        visited = 0;
        wrapped = false;
        if (maxVisited <= 0 || present.IsEmpty)
        {
            return 0;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_scanOrder.Count == 0)
            {
                return 0;
            }

            _scanCursor ??= _scanOrder.First;
            var copied = 0;
            while (visited < maxVisited && _scanCursor is not null)
            {
                var node = _scanCursor;
                _scanCursor = node.Next;
                visited++;
                if (_entries.TryGetValue(node.Value, out var metadata)
                    && metadata.State == ArticleStorageState.Present
                    && copied < present.Length)
                {
                    present[copied++] = new RetentionScanCandidate(
                        metadata.ArtId,
                        metadata.Location,
                        metadata.Sequence,
                        metadata.AcceptedUtc);
                }

                if (_scanCursor is null)
                {
                    wrapped = true;
                    _scanCursor = _scanOrder.First;
                    break;
                }
            }

            return copied;
        }
    }

    /// <summary>
    /// Evicts <paramref name="artId"/> only when the locked row is still the scanned Present article.
    /// </summary>
    /// <param name="artId">Article identity from the scan window.</param>
    /// <param name="expectedLocation">Location copied with the candidate.</param>
    /// <param name="expectedSequence">Journal sequence copied with the candidate.</param>
    /// <param name="expectedAcceptedUtc">Arrival instant copied with the candidate.</param>
    /// <param name="utcNow">Timestamp stored on the tombstone as <see cref="StoredArticleMetadata.LastAccessUtc"/>.</param>
    /// <param name="transitioned">The Evicted row when this call appends one.</param>
    /// <param name="frameOffset">File offset of the appended frame; otherwise -1.</param>
    /// <returns>True when this call appended an Evicted frame.</returns>
    /// <remarks>
    /// Compare and append share one index critical section. A relocation, a newer Accept, an
    /// existing tombstone, or <see cref="DateTimeOffset.MinValue"/> leaves the row unchanged.
    /// <see cref="StoredArticleMetadata.AcceptedUtc"/> is copied from the current row.
    /// </remarks>
    internal bool TryExpirePresentIfUnchanged(
        ArticleId artId,
        in StoredArticleLocation expectedLocation,
        ulong expectedSequence,
        DateTimeOffset expectedAcceptedUtc,
        DateTimeOffset utcNow,
        out StoredArticleMetadata transitioned,
        out long frameOffset)
    {
        transitioned = default;
        frameOffset = -1;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(artId, out var existing)
                || existing.State != ArticleStorageState.Present
                || existing.Sequence != expectedSequence
                || existing.AcceptedUtc != expectedAcceptedUtc
                || existing.AcceptedUtc == DateTimeOffset.MinValue
                || !LocationsEqual(existing.Location, expectedLocation))
            {
                return false;
            }

            transitioned = existing with { State = ArticleStorageState.Evicted, LastAccessUtc = utcNow };
            frameOffset = _stream.Length;
            AppendDurableUnlocked(transitioned);
            _entries[artId] = transitioned;
            NoteStructuralChangeUnlocked();
            return true;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Soft in-memory LastAccess hint only. Must not require a durable NVMe write; loss across
    /// crash is acceptable and not required for recovery correctness.
    /// </remarks>
    public void TouchHint(ArticleId artId, DateTimeOffset utcNow)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _touchHintCount++;
            if (!_entries.TryGetValue(artId, out var existing)
                || existing.State != ArticleStorageState.Present)
            {
                return;
            }

            _entries[artId] = existing with { LastAccessUtc = utcNow };
            _useCounts[artId] = _useCounts.GetValueOrDefault(artId) + 1;
        }
    }

    /// <summary>
    /// Process-local successful-read count used as the LFU key. Zero when the article has not
    /// been read in this process. Not durable; restart does not restore it. Not an LRU timestamp.
    /// </summary>
    public long UseCount(ArticleId artId)
    {
        lock (_gate)
        {
            return _useCounts.GetValueOrDefault(artId);
        }
    }

    /// <summary>
    /// Number of process-local LFU counters. A counter exists only for an indexed article.
    /// </summary>
    internal int UseCountEntryCount
    {
        get
        {
            lock (_gate)
            {
                return _useCounts.Count;
            }
        }
    }

    /// <summary>
    /// Drops Evicted and Invalid rows whose location is <paramref name="segmentId"/>.
    /// Present rows are left in place. Does not append an index frame.
    /// </summary>
    /// <param name="segmentId">Segment that has been physically reclaimed.</param>
    /// <returns>The number of rows removed.</returns>
    internal int ForgetReclaimedSegment(SegmentId segmentId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ForgetWhereUnlocked(row =>
                row.Location.SegmentId == segmentId
                && row.State is ArticleStorageState.Evicted or ArticleStorageState.Invalid);
        }
    }

    /// <summary>
    /// Drops Evicted and Invalid rows whose segment fails <paramref name="segmentExists"/>.
    /// Present rows are left in place. Does not append an index frame.
    /// </summary>
    /// <param name="segmentExists">True when the catalogue still contains that segment.</param>
    /// <returns>The number of rows removed.</returns>
    internal int ForgetRowsForAbsentSegments(Func<SegmentId, bool> segmentExists)
    {
        ArgumentNullException.ThrowIfNull(segmentExists);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ForgetWhereUnlocked(row =>
                row.State is ArticleStorageState.Evicted or ArticleStorageState.Invalid
                && !segmentExists(row.Location.SegmentId));
        }
    }

    /// <summary>Inserts <paramref name="artId"/> at the end of the retention scan order once.</summary>
    /// <remarks>Caller holds <see cref="_gate"/>. In-place updates keep the existing node.</remarks>
    private void RememberScanOrderUnlocked(ArticleId artId)
    {
        if (_scanNodes.ContainsKey(artId))
        {
            return;
        }

        var node = _scanOrder.AddLast(artId);
        _scanNodes.Add(artId, node);
    }

    /// <summary>Drops <paramref name="artId"/> from the retention scan order.</summary>
    /// <remarks>Caller holds <see cref="_gate"/>. The cursor advances when it pointed at this node.</remarks>
    private void ForgetScanOrderUnlocked(ArticleId artId)
    {
        if (!_scanNodes.Remove(artId, out var node))
        {
            return;
        }

        if (_scanCursor == node)
        {
            _scanCursor = node.Next;
        }

        _scanOrder.Remove(node);
    }

    /// <summary>Rebuilds scan order from the current rows. Used after open replay.</summary>
    private void RebuildScanOrderUnlocked()
    {
        _scanOrder.Clear();
        _scanNodes.Clear();
        _scanCursor = null;
        foreach (var artId in _entries.Keys)
        {
            var node = _scanOrder.AddLast(artId);
            _scanNodes.Add(artId, node);
        }
    }

    /// <summary>
    /// Removes matching rows and their LFU counters. Caller holds <see cref="_gate"/>.
    /// </summary>
    private int ForgetWhereUnlocked(Func<StoredArticleMetadata, bool> remove)
    {
        List<ArticleId>? drop = null;
        foreach (var pair in _entries)
        {
            if (!remove(pair.Value))
            {
                continue;
            }

            drop ??= new List<ArticleId>();
            drop.Add(pair.Key);
        }

        if (drop is null)
        {
            DropOrphanUseCountsUnlocked();
            return 0;
        }

        foreach (var artId in drop)
        {
            _ = _entries.Remove(artId);
            ForgetScanOrderUnlocked(artId);
            _ = _useCounts.Remove(artId);
        }

        NoteStructuralChangeUnlocked();

        DropOrphanUseCountsUnlocked();
        FileArticleIndexLogMessages.ReclaimedRowsForgotten(_logger, _indexPath, drop.Count);
        return drop.Count;
    }

    /// <summary>
    /// Removes LFU counters that no longer have an index row. Caller holds <see cref="_gate"/>.
    /// </summary>
    private void DropOrphanUseCountsUnlocked()
    {
        if (_useCounts.Count == 0)
        {
            return;
        }

        List<ArticleId>? orphans = null;
        foreach (var artId in _useCounts.Keys)
        {
            if (_entries.ContainsKey(artId))
            {
                continue;
            }

            orphans ??= new List<ArticleId>();
            orphans.Add(artId);
        }

        if (orphans is null)
        {
            return;
        }

        foreach (var artId in orphans)
        {
            _ = _useCounts.Remove(artId);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stream.Dispose();
            FileArticleIndexLogMessages.Closed(_logger, _indexPath);
        }

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Writes and installs <c>article.index.snap</c> from the current in-memory projection.
    /// </summary>
    /// <remarks>
    /// The index gate is held only to copy the dictionary and to rename the temp file.
    /// The copy is one <see cref="StoredArticleMetadata"/> array for the call and is not retained.
    /// <c>article.index</c> is not appended, truncated, or replaced. A failed attempt deletes
    /// the temp file and leaves any previously installed snapshot in place.
    /// </remarks>
    /// <returns>The installed snapshot identity. Generation is index-local, not a journal sequence.</returns>
    internal ArticleIndexSnapshotHeader WriteSnapshot()
    {
        EnterSnapshotFlight();
        try
        {
            return WriteSnapshotBody();
        }
        finally
        {
            ExitSnapshotFlight();
        }
    }

    /// <summary>
    /// Installs a snapshot, then replaces <c>article.index</c> with the frames past that snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order: durable snapshot, durable replacement, same-directory <see cref="File.Move(string, string, bool)"/>,
    /// then the previous index bytes are the replaced file. The index gate is not held across snapshot
    /// or tail IO. The live stream allows readers so that tail copy can use a second handle.
    /// Bytes below a length captured under the gate stay put because the log is append-only.
    /// The gate is held again to copy the catch-up tail, check those bytes, and install the
    /// replacement. Appends during the flight land in the catch-up or, after install, on the new
    /// file. A failure before install deletes <c>article.index.repl.tmp</c> and leaves the previous
    /// <c>article.index</c> in place. The snapshot, once installed, is left in place.
    /// </para>
    /// <para>
    /// The move is the same call the article journal uses. The write handle is closed first because
    /// Windows cannot replace a file this process still has open. Source and destination are in the
    /// same control directory. Crash before the move returns: the previous index plus the installed
    /// snapshot still recover, and <c>article.index.repl.tmp</c> is not read. Crash after the move
    /// completes: the snapshot plus the replacement recover. A crash inside the move is not repaired.
    /// The next open fails closed unless the installed file is a valid legacy log or a valid
    /// replacement for the installed snapshot. Directory fsync is not used.
    /// </para>
    /// </remarks>
    /// <returns>The installed snapshot and the number of index bytes copied into the replacement.</returns>
    internal ArticleIndexCheckpointResult Checkpoint()
    {
        EnterSnapshotFlight();
        try
        {
            EnsureAlignedForCheckpoint();
            var snapshot = WriteSnapshotBody();
            var retiredPhysicalBytes = Math.Max(0L, snapshot.CoveredIndexLength - _snapshotFrameBase);
            var deltaBytes = RetireCoveredPrefix(snapshot);
            OnIndexPrefixRetired?.Invoke(new IndexPrefixRetirement(
                snapshot.CoveredIndexLength,
                ArticleIndexDeltaFile.HeaderLength,
                snapshot.Generation,
                _snapshotArticleIds));
            return new ArticleIndexCheckpointResult(snapshot, deltaBytes, retiredPhysicalBytes);
        }
        finally
        {
            ExitSnapshotFlight();
        }
    }

    private void EnterSnapshotFlight()
    {
        if (!_snapshotFlight.Wait(TimeSpan.Zero))
        {
            TestOnSnapshotFlightContended?.Invoke();
            _snapshotFlight.Wait();
        }

        var writers = Interlocked.Increment(ref _snapshotWriters);
        UpdateMaxSnapshotWriters(writers);
    }

    private void ExitSnapshotFlight()
    {
        Interlocked.Decrement(ref _snapshotWriters);
        _snapshotFlight.Release();
    }

    private ArticleIndexSnapshotHeader WriteSnapshotBody()
    {
        var tempPath = SnapshotTempPath();
        var installed = false;
        ulong? reservationId = null;
        var snapshotReservedBytes = 0L;
        try
        {
            StoredArticleMetadata[] copy;
            long coveredIndexLength;
            ulong generation;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                copy = new StoredArticleMetadata[_entries.Count];
                if (copy.Length > 0)
                {
                    _entries.Values.CopyTo(copy, 0);
                }

                _snapshotFrameBase = FrameBaseUnlocked();
                coveredIndexLength = _stream.Length;
                generation = _installedSnapshotGeneration + 1;
                var articleIds = new ArticleId[copy.Length];
                for (var i = 0; i < copy.Length; i++)
                {
                    articleIds[i] = copy[i].ArtId;
                }

                _snapshotArticleIds = articleIds;
            }

            if (_checkpointCapacity is not null)
            {
                var encodedLength = ArticleIndexSnapshotCodec.EncodedLength(copy.Length);
                reservationId = TakeSnapshotTempReservation(encodedLength, out snapshotReservedBytes);
            }

            TestDuringSnapshotWrite?.Invoke();
            using (var temp = new FileStream(
                       tempPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: CopyBufferBytes,
                       FileOptions.None))
            {
                ArticleIndexSnapshotCodec.Write(temp, generation, coveredIndexLength, copy);
                TestBeforeSnapshotFlush?.Invoke();
                temp.Flush(flushToDisk: true);
            }

            _ = ArticleIndexSnapshotCodec.Read(tempPath);
            TestBeforeSnapshotInstall?.Invoke();

            var snapshotPath = SnapshotPath();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                File.Move(tempPath, snapshotPath, overwrite: true);
                installed = true;
                _installedSnapshotGeneration = generation;
            }

            if (reservationId is ulong installedId)
            {
                if (_installedSnapshotReservationId is ulong previous)
                {
                    _checkpointCapacity!.Release(previous);
                }

                _installedSnapshotReservationId = installedId;
                reservationId = null;
            }

            FileArticleIndexLogMessages.SnapshotInstalled(
                _logger,
                snapshotPath,
                generation,
                copy.Length,
                coveredIndexLength);
            return new ArticleIndexSnapshotHeader(
                generation,
                ArticleIndexSnapshotCodec.LegacyDeltaGeneration,
                coveredIndexLength,
                (ulong)copy.Length);
        }
        catch (Exception ex)
        {
            if (ex is not CheckpointCapacityDeniedException)
            {
                FileArticleIndexLogMessages.SnapshotFailed(_logger, ex, tempPath);
            }

            if (!installed)
            {
                if (TryDelete(tempPath))
                {
                    if (reservationId is null
                        && _retainedSnapshotTempReservationId is ulong orphan
                        && _checkpointCapacity is not null)
                    {
                        _checkpointCapacity.Release(orphan);
                        _retainedSnapshotTempReservationId = null;
                        _retainedSnapshotTempReservationBytes = 0;
                    }
                }
                else if (reservationId is ulong retained)
                {
                    _retainSnapshotTempReservation = true;
                    _retainedSnapshotTempReservationId = retained;
                    _retainedSnapshotTempReservationBytes = snapshotReservedBytes;
                }
            }

            throw;
        }
        finally
        {
            if (!_retainSnapshotTempReservation && reservationId is ulong id)
            {
                _checkpointCapacity!.Release(id);
            }

            _retainSnapshotTempReservation = false;
        }
    }

    private ulong TakeSnapshotTempReservation(long encodedLength, out long reservedBytes)
    {
        if (_retainedSnapshotTempReservationId is ulong retained)
        {
            var known = _retainedSnapshotTempReservationBytes;
            if (encodedLength > known
                && !_checkpointCapacity!.TryIncrease(retained, encodedLength - known))
            {
                reservedBytes = known;
                throw new CheckpointCapacityDeniedException(encodedLength - known);
            }

            _retainedSnapshotTempReservationId = null;
            _retainedSnapshotTempReservationBytes = 0;
            reservedBytes = Math.Max(known, encodedLength);
            return retained;
        }

        var created = _checkpointCapacity!.TryReserve(encodedLength);
        if (created is null)
        {
            reservedBytes = 0;
            throw new CheckpointCapacityDeniedException(encodedLength);
        }

        reservedBytes = encodedLength;
        return created.Value;
    }

    private void EnsureAlignedForCheckpoint()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var frameBase = FrameBaseUnlocked();
            var length = _stream.Length;
            if (length < frameBase || !ArticleIndexRecordCodec.EndsOnFrameBoundary(_stream, frameBase, length))
            {
                throw new ArticleIndexCorruptException(
                    $"Article index length {length} is not an aligned checkpoint boundary.",
                    length);
            }
        }
    }

    private long RetireCoveredPrefix(ArticleIndexSnapshotHeader snapshot)
    {
        var replPath = ReplacementTempPath();
        var moved = false;
        ulong? reservationId = null;
        var replacementReservedBytes = 0L;
        try
        {
            var copyEnd = MeasureReplacementCopyEnd(snapshot);
            var stable = copyEnd - snapshot.CoveredIndexLength;
            if (_checkpointCapacity is not null)
            {
                var bytes = checked(ArticleIndexDeltaFile.HeaderLength + stable);
                reservationId = TakeReplacementReservation(bytes, out replacementReservedBytes);
            }

            WriteReplacementPrefixAt(snapshot, replPath, copyEnd);
            TestDuringReplacementWrite?.Invoke();
            var deltaBytes = InstallReplacement(
                snapshot,
                replPath,
                copyEnd,
                ref moved,
                reservationId,
                stable);
            FileArticleIndexLogMessages.CheckpointInstalled(
                _logger,
                _indexPath,
                snapshot.Generation,
                snapshot.CoveredIndexLength,
                deltaBytes);
            return deltaBytes;
        }
        catch (Exception ex)
        {
            if (!moved)
            {
                if (TryDelete(replPath))
                {
                    if (reservationId is null
                        && _retainedReplacementReservationId is ulong orphan
                        && _checkpointCapacity is not null)
                    {
                        _checkpointCapacity.Release(orphan);
                        _retainedReplacementReservationId = null;
                        _retainedReplacementReservationBytes = 0;
                    }
                }
                else if (reservationId is ulong retained)
                {
                    _retainReplacementReservation = true;
                    _retainedReplacementReservationId = retained;
                    _retainedReplacementReservationBytes = replacementReservedBytes;
                }
            }

            if (ex is not CheckpointCapacityDeniedException)
            {
                FileArticleIndexLogMessages.CheckpointFailed(_logger, ex, _indexPath);
            }

            throw;
        }
        finally
        {
            if (!_retainReplacementReservation && reservationId is ulong id)
            {
                _checkpointCapacity!.Release(id);
            }

            _retainReplacementReservation = false;
        }
    }

    private ulong TakeReplacementReservation(long bytes, out long reservedBytes)
    {
        if (_retainedReplacementReservationId is ulong retained)
        {
            var known = _retainedReplacementReservationBytes;
            if (bytes > known && !_checkpointCapacity!.TryIncrease(retained, bytes - known))
            {
                reservedBytes = known;
                throw new CheckpointCapacityDeniedException(bytes - known);
            }

            _retainedReplacementReservationId = null;
            _retainedReplacementReservationBytes = 0;
            reservedBytes = Math.Max(known, bytes);
            return retained;
        }

        var created = _checkpointCapacity!.TryReserve(bytes);
        if (created is null)
        {
            reservedBytes = 0;
            throw new CheckpointCapacityDeniedException(bytes);
        }

        reservedBytes = bytes;
        return created.Value;
    }

    private long MeasureReplacementCopyEnd(ArticleIndexSnapshotHeader snapshot)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var frameBase = FrameBaseUnlocked();
            var copyEnd = _stream.Length;
            ValidateSnapshotCoverage(snapshot.CoveredIndexLength, copyEnd, frameBase);
            return copyEnd;
        }
    }

    private void WriteReplacementPrefixAt(ArticleIndexSnapshotHeader snapshot, string replPath, long copyEnd)
    {
        using var read = OpenIndexReadStream();
        using var repl = CreateReplacementStream(replPath);
        ArticleIndexDeltaFile.WriteHeader(repl, snapshot.Generation);
        var stable = copyEnd - snapshot.CoveredIndexLength;
        CopyRange(read, snapshot.CoveredIndexLength, copyEnd, repl);
        AssertSameBytes(read, snapshot.CoveredIndexLength, repl, ArticleIndexDeltaFile.HeaderLength, stable);
    }

    private long InstallReplacement(
        ArticleIndexSnapshotHeader snapshot,
        string replPath,
        long copyEnd,
        ref bool moved,
        ulong? reservationId,
        long reservedPayload)
    {
        while (true)
        {
            long extra;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var end = _stream.Length;
                if (end < copyEnd)
                {
                    throw new InvalidOperationException("Article index shrank during checkpoint.");
                }

                var payload = end - snapshot.CoveredIndexLength;
                if (_checkpointCapacity is null || payload <= reservedPayload)
                {
                    using (var repl = OpenReplacementStream(replPath))
                    {
                        _ = repl.Seek(0, SeekOrigin.End);
                        CopyRange(_stream, copyEnd, end, repl);
                        TestBeforeReplacementFlush?.Invoke();
                        repl.Flush(flushToDisk: true);
                        if (repl.Length != ArticleIndexDeltaFile.HeaderLength + payload)
                        {
                            throw new ArticleIndexCorruptException(
                                $"Article index replacement length {repl.Length} does not match the covered tail {payload}.",
                                snapshot.CoveredIndexLength);
                        }

                        AssertSameBytes(
                            _stream,
                            snapshot.CoveredIndexLength,
                            repl,
                            ArticleIndexDeltaFile.HeaderLength,
                            payload);
                        TestBeforeReplacementInstall?.Invoke();
                    }

                    ReplaceIndexUnlocked(replPath, ref moved);
                    return payload;
                }

                extra = payload - reservedPayload;
            }

            if (reservationId is not ulong id || !_checkpointCapacity!.TryIncrease(id, extra))
            {
                throw new CheckpointCapacityDeniedException(extra);
            }

            reservedPayload = checked(reservedPayload + extra);
        }
    }

    private void ReplaceIndexUnlocked(string replPath, ref bool moved)
    {
        _stream.Dispose();
        try
        {
            File.Move(replPath, _indexPath, overwrite: true);
            moved = true;
        }
        catch (Exception moveEx)
        {
            if (!TryReopenIndex())
            {
                _disposed = true;
                throw new IOException(
                    "Article index checkpoint failed and the previous index could not be reopened.",
                    moveEx);
            }

            throw;
        }

        if (!TryReopenIndex())
        {
            _disposed = true;
            throw new IOException("Article index replacement was installed and the new index could not be opened.");
        }
    }

    private bool TryReopenIndex()
    {
        try
        {
            _stream = OpenIndexStream(_indexPath, FileMode.Open);
            _ = _stream.Seek(0, SeekOrigin.End);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private long FrameBaseUnlocked()
    {
        var length = _stream.Length;
        var position = _stream.Position;
        try
        {
            return ArticleIndexDeltaFile.TryReadHeader(_stream, length, out _)
                ? ArticleIndexDeltaFile.HeaderLength
                : 0;
        }
        finally
        {
            _stream.Position = position;
        }
    }

    private static void CopyRange(Stream source, long sourceOffset, long sourceEnd, Stream destination)
    {
        if (sourceEnd < sourceOffset)
        {
            throw new IOException("Article index tail range is inverted.");
        }

        var remaining = sourceEnd - sourceOffset;
        if (remaining == 0)
        {
            return;
        }

        _ = source.Seek(sourceOffset, SeekOrigin.Begin);
        var buffer = new byte[CopyBufferBytes];
        while (remaining > 0)
        {
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = source.Read(buffer, 0, requested);
            if (read == 0)
            {
                throw new EndOfStreamException("Short read copying the article index tail.");
            }

            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static void AssertSameBytes(Stream left, long leftOffset, Stream right, long rightOffset, long count)
    {
        if (count < 0)
        {
            throw new IOException("Article index comparison length is negative.");
        }

        if (count == 0)
        {
            return;
        }

        _ = left.Seek(leftOffset, SeekOrigin.Begin);
        _ = right.Seek(rightOffset, SeekOrigin.Begin);
        var leftBuffer = new byte[CopyBufferBytes];
        var rightBuffer = new byte[CopyBufferBytes];
        var remaining = count;
        while (remaining > 0)
        {
            var requested = (int)Math.Min(leftBuffer.Length, remaining);
            ReadExact(left, leftBuffer, requested);
            ReadExact(right, rightBuffer, requested);
            if (!leftBuffer.AsSpan(0, requested).SequenceEqual(rightBuffer.AsSpan(0, requested)))
            {
                throw new ArticleIndexCorruptException(
                    "Article index replacement bytes do not match the covered tail.",
                    leftOffset);
            }

            remaining -= requested;
        }
    }

    private static void ReadExact(Stream stream, byte[] buffer, int count)
    {
        var filled = 0;
        while (filled < count)
        {
            var read = stream.Read(buffer, filled, count - filled);
            if (read == 0)
            {
                throw new EndOfStreamException("Short read verifying the article index replacement.");
            }

            filled += read;
        }
    }

    private FileStream OpenIndexReadStream() =>
        new(
            _indexPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: CopyBufferBytes,
            FileOptions.None);

    private static FileStream CreateReplacementStream(string path) =>
        new(
            path,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: CopyBufferBytes,
            FileOptions.None);

    private static FileStream OpenReplacementStream(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: CopyBufferBytes,
            FileOptions.None);

    private static FileStream OpenIndexStream(string path, FileMode mode) =>
        new(
            path,
            mode,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: CopyBufferBytes,
            FileOptions.None);

    private void UpdateMaxSnapshotWriters(int writers)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref _maxSnapshotWriters);
            if (writers <= observed)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _maxSnapshotWriters, writers, observed) != observed);
    }

    private string SnapshotPath() =>
        Path.Combine(Path.GetDirectoryName(_indexPath) ?? string.Empty, SnapshotFileName);

    private string SnapshotTempPath() =>
        Path.Combine(Path.GetDirectoryName(_indexPath) ?? string.Empty, SnapshotTempFileName);

    private string ReplacementTempPath() =>
        Path.Combine(Path.GetDirectoryName(_indexPath) ?? string.Empty, ReplacementTempFileName);

    private bool TryDelete(string path)
    {
        if (TestFailCheckpointTempDelete && File.Exists(path))
        {
            return false;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return !File.Exists(path);
    }

    /// <summary>Current index file length. Tests and capacity binding use it to see whether a frame landed.</summary>
    internal long DurableLength
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _stream.Length;
            }
        }
    }

    /// <summary>
    /// Physical index frames that still occupy the authoritative index.
    /// A replacement whose generation matches the snapshot contributes every snapshot row
    /// plus every frame in the replacement payload. Any earlier index file is scanned in full
    /// because its prefix has not been replaced. Snapshot rows are not added in that case.
    /// </summary>
    internal RetainedIndexFrame[] CopyRetainedIndexFrames()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var restore = _stream.Position;
            try
            {
                var length = _stream.Length;
                var snapshotPath = SnapshotPath();
                var hasSnapshot = File.Exists(snapshotPath);
                var replacementInstalled = false;
                ArticleIndexSnapshotHeader snapshot = default;
                if (hasSnapshot
                    && ArticleIndexDeltaFile.TryReadHeader(_stream, length, out var fileGeneration))
                {
                    snapshot = ArticleIndexSnapshotCodec.ReadHeader(snapshotPath);
                    replacementInstalled = fileGeneration == snapshot.Generation;
                }

                var frames = new List<RetainedIndexFrame>();
                if (replacementInstalled)
                {
                    var rows = new Dictionary<ArticleId, StoredArticleMetadata>();
                    _ = ArticleIndexSnapshotCodec.Apply(snapshotPath, rows);
                    foreach (var row in rows.Values)
                    {
                        frames.Add(new RetainedIndexFrame(row.ArtId, FileOffset: -1, snapshot.Generation));
                    }

                    CollectIndexFramesUnlocked(ArticleIndexDeltaFile.HeaderLength, length, frames);
                }
                else
                {
                    var start = ArticleIndexDeltaFile.TryReadHeader(_stream, length, out _)
                        ? ArticleIndexDeltaFile.HeaderLength
                        : 0L;
                    CollectIndexFramesUnlocked(start, length, frames);
                }

                return frames.ToArray();
            }
            finally
            {
                _stream.Position = restore;
            }
        }
    }

    private void CollectIndexFramesUnlocked(long start, long length, List<RetainedIndexFrame> frames)
    {
        if (length < start)
        {
            return;
        }

        var position = _stream.Position;
        try
        {
            _stream.Position = start;
            var frame = new byte[ArticleIndexRecordCodec.MaxFrameLength];
            var offset = start;
            while (offset + 4 <= length)
            {
                _stream.Position = offset;
                var filled = 0;
                while (filled < 4)
                {
                    var read = _stream.Read(frame, filled, 4 - filled);
                    if (read == 0)
                    {
                        return;
                    }

                    filled += read;
                }

                var declared = BinaryPrimitives.ReadUInt32LittleEndian(frame);
                if (!ArticleIndexRecordCodec.IsAcceptedFrameLength(declared) || offset + declared > length)
                {
                    return;
                }

                while (filled < declared)
                {
                    var read = _stream.Read(frame, filled, (int)declared - filled);
                    if (read == 0)
                    {
                        return;
                    }

                    filled += read;
                }

                if (ArticleIndexRecordCodec.TryDecode(frame.AsSpan(0, (int)declared), out _, out var metadata, out _))
                {
                    frames.Add(new RetainedIndexFrame(metadata.ArtId, offset, SnapshotGeneration: 0));
                }

                offset += declared;
            }
        }
        finally
        {
            _stream.Position = position;
        }
    }

    private void AppendDurableUnlocked(in StoredArticleMetadata metadata)
    {
        TestBeforeDurableAppend?.Invoke();
        var frame = ArticleIndexRecordCodec.Encode(metadata);
        _stream.Seek(0, SeekOrigin.End);
        _stream.Write(frame, 0, frame.Length);
        DurableIndexFlushUnlocked();
        _durableWriteCount++;
    }

    private void DurableIndexFlushUnlocked()
    {
        TestBeforeDurableFlush?.Invoke();
        _stream.Flush(flushToDisk: true);
        _durableFlushCount++;
    }

    private void ReplayAndRecoverUnlocked()
    {
        var fileLength = _stream.Length;
        var snapshotPath = SnapshotPath();
        var hasSnapshot = File.Exists(snapshotPath);
        ArticleIndexSnapshotHeader snapshot = default;
        if (hasSnapshot)
        {
            snapshot = ArticleIndexSnapshotCodec.Apply(snapshotPath, _entries);
            _installedSnapshotGeneration = snapshot.Generation;
        }

        var deltaStart = ResolveDeltaStartUnlocked(fileLength, hasSnapshot, snapshot, snapshotPath);
        if (deltaStart == fileLength)
        {
            _stream.Seek(0, SeekOrigin.End);
        }
        else
        {
            _stream.Seek(deltaStart, SeekOrigin.Begin);
            ArticleIndexReplayer.Replay(_stream, fileLength, _entries, HandleDecodeFailureUnlocked, deltaStart);
            _stream.Seek(0, SeekOrigin.End);
        }

        RebuildScanOrderUnlocked();
    }

    private long ResolveDeltaStartUnlocked(
        long fileLength,
        bool hasSnapshot,
        ArticleIndexSnapshotHeader snapshot,
        string snapshotPath)
    {
        if (!ArticleIndexDeltaFile.TryReadHeader(_stream, fileLength, out var fileGeneration))
        {
            if (!hasSnapshot)
            {
                return 0;
            }

            ValidateSnapshotCoverage(snapshot.CoveredIndexLength, fileLength, frameBase: 0);
            FileArticleIndexLogMessages.SnapshotReplay(
                _logger,
                snapshotPath,
                snapshot.Generation,
                snapshot.CoveredIndexLength,
                fileLength);
            return snapshot.CoveredIndexLength;
        }

        if (!hasSnapshot)
        {
            throw new ArticleIndexCorruptException(
                "Article index replacement has no installed snapshot.",
                0);
        }

        if (fileGeneration > snapshot.Generation)
        {
            throw new ArticleIndexCorruptException(
                $"Article index replacement generation {fileGeneration} is newer than snapshot generation {snapshot.Generation}.",
                0);
        }

        if (fileGeneration == snapshot.Generation)
        {
            FileArticleIndexLogMessages.ReplacementReplay(
                _logger,
                _indexPath,
                fileGeneration,
                ArticleIndexDeltaFile.HeaderLength,
                fileLength);
            return ArticleIndexDeltaFile.HeaderLength;
        }

        ValidateSnapshotCoverage(snapshot.CoveredIndexLength, fileLength, ArticleIndexDeltaFile.HeaderLength);
        FileArticleIndexLogMessages.SnapshotReplay(
            _logger,
            snapshotPath,
            snapshot.Generation,
            snapshot.CoveredIndexLength,
            fileLength);
        return snapshot.CoveredIndexLength;
    }

    private void ValidateSnapshotCoverage(long coveredIndexLength, long indexLength, long frameBase)
    {
        if (coveredIndexLength < frameBase || coveredIndexLength > indexLength)
        {
            throw new ArticleIndexCorruptException(
                $"Article index snapshot covered length {coveredIndexLength} exceeds index length {indexLength}.",
                coveredIndexLength);
        }

        if (!ArticleIndexRecordCodec.EndsOnFrameBoundary(_stream, frameBase, coveredIndexLength))
        {
            throw new ArticleIndexCorruptException(
                $"Article index snapshot covered length {coveredIndexLength} is not aligned to a frame boundary.",
                coveredIndexLength);
        }
    }

    private void HandleDecodeFailureUnlocked(
        long offset,
        long fileLength,
        ArticleIndexFrameError error)
    {
        switch (error)
        {
            case ArticleIndexFrameError.Incomplete:
                // Fewer than the declared frame length at EOF is a genuine torn write.
                TruncateTornTailUnlocked(offset, fileLength, "incomplete-final-frame");
                return;

            case ArticleIndexFrameError.CorruptChecksum:
                // A full fixed-size frame with a bad CRC is corruption, including at EOF.
                // EOF does not prove a torn write once RecordLength bytes are present.
                FileArticleIndexLogMessages.MidFileCorrupt(
                    _logger,
                    _indexPath,
                    offset,
                    error.ToString());
                throw new ArticleIndexCorruptException(
                    $"Article index corrupt checksum at offset {offset} (complete fixed-size frame).",
                    offset);

            case ArticleIndexFrameError.CorruptLength:
            case ArticleIndexFrameError.Corrupt:
                FileArticleIndexLogMessages.MidFileCorrupt(
                    _logger,
                    _indexPath,
                    offset,
                    error.ToString());
                throw new ArticleIndexCorruptException(
                    $"Article index corrupt at offset {offset} ({error}).",
                    offset);

            default:
                throw new ArticleIndexCorruptException(
                    $"Article index decode failed at offset {offset} ({error}).",
                    offset);
        }
    }

    private void TruncateTornTailUnlocked(long validEnd, long fileLength, string reason)
    {
        FileArticleIndexLogMessages.TruncatingTornTail(
            _logger,
            _indexPath,
            validEnd,
            fileLength,
            reason);
        _stream.SetLength(validEnd);
        _stream.Flush(flushToDisk: true);
    }

    private static bool LocationsEqual(in StoredArticleLocation left, in StoredArticleLocation right) =>
        left.SegmentId.Value == right.SegmentId.Value
        && left.Offset == right.Offset
        && left.Length == right.Length;
}

/// <summary>Result of one explicit index checkpoint.</summary>
/// <param name="Snapshot">Snapshot installed before the historical prefix was retired.</param>
/// <param name="DeltaBytes">Index bytes copied from the covered length through the install boundary.</param>
/// <param name="RetiredPhysicalBytes">
/// Frame payload covered by the snapshot. Excludes a <c>VNID</c> header and excludes the tail
/// copied into the replacement.
/// </param>
internal readonly record struct ArticleIndexCheckpointResult(
    ArticleIndexSnapshotHeader Snapshot,
    long DeltaBytes,
    long RetiredPhysicalBytes);

/// <summary>Whether <see cref="FileArticleIndex.TryCommitPresent"/> appended a physical frame.</summary>
internal enum DurableIndexAppend
{
    /// <summary>No frame was written.</summary>
    Rejected = 0,

    /// <summary>The same Present frame was already durable.</summary>
    Unchanged = 1,

    /// <summary>A new Present frame was appended.</summary>
    Appended = 2,
}

/// <summary>
/// Index rows grouped by segment for one closed-segment accounting pass.
/// Built under the index lock and safe to read after that lock is released
/// until <see cref="FileArticleIndex.StructuralGeneration"/> changes.
/// </summary>
internal sealed class ClosedSegmentAccountingView
{
    private readonly Dictionary<ulong, SegmentRows> _rows;

    internal ClosedSegmentAccountingView(long generation, Dictionary<ulong, SegmentRows> rows)
    {
        Generation = generation;
        _rows = rows;
    }

    internal long Generation { get; }

    internal bool TryGetRows(ulong segmentId, out SegmentRows rows) => _rows.TryGetValue(segmentId, out rows!);

    /// <summary>Locations and byte totals for one segment. Mutated only while the view is built.</summary>
    internal sealed class SegmentRows
    {
        internal long PresentBytes { get; set; }

        internal long DeadBytes { get; set; }

        internal HashSet<StoredArticleLocation> Named { get; } = [];
    }
}

/// <summary>Physical index frames that survived an installed index replacement.</summary>
/// <param name="CoveredIndexLength">Prefix of the previous index file that the replacement omitted.</param>
/// <param name="NewFrameBase">Offset of the first copied frame in the installed replacement.</param>
/// <param name="SnapshotGeneration">Generation of the snapshot that now holds one current row per article.</param>
/// <param name="SnapshotArticleIds">Article ids of every snapshot row, independent of logical state.</param>
internal readonly record struct IndexPrefixRetirement(
    long CoveredIndexLength,
    long NewFrameBase,
    ulong SnapshotGeneration,
    ArticleId[] SnapshotArticleIds);

/// <summary>One physical index frame still occupying authoritative index storage.</summary>
/// <param name="ArtId">Article the frame names.</param>
/// <param name="FileOffset">Offset in the current index file, or -1 when the frame lives in the snapshot.</param>
/// <param name="SnapshotGeneration">Snapshot generation when <paramref name="FileOffset"/> is -1; otherwise 0.</param>
internal readonly record struct RetainedIndexFrame(
    ArticleId ArtId,
    long FileOffset,
    ulong SnapshotGeneration);

/// <summary>One Present index row copied for a retention window.</summary>
/// <param name="ArtId">Article identity.</param>
/// <param name="Location">Location at the copy. A later relocation makes expiration a no-op.</param>
/// <param name="Sequence">Journal sequence at the copy. A newer Accept makes expiration a no-op.</param>
/// <param name="AcceptedUtc">Arrival instant at the copy. Expiration does not replace it.</param>
internal readonly record struct RetentionScanCandidate(
    ArticleId ArtId,
    StoredArticleLocation Location,
    ulong Sequence,
    DateTimeOffset AcceptedUtc);
