using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

/// <summary>
/// Filesystem-backed Model A article storage engine coordinating journal, segments, and index.
/// </summary>
/// <remarks>
/// <para>
/// Accept returns after durable journal Accept (ArtData embedded). Background persist (or
/// <see cref="RecoverAsync"/>) completes SATA append → PhysicalWritten → index Present →
/// IndexCommitted.
/// </para>
/// <para>
/// Recovery (Phase 1.5 / Option 1): Accept-only always performs a fresh SATA append from journal
/// ArtData and does not discover orphan prior appends. Accept+PhysicalWritten validates and
/// reuses the recorded location; it does not re-append unless the location is unusable — and
/// because the journal forbids superseding PhysicalWritten, an unusable location fails closed
/// and leaves the sequence outstanding (no false Present).
/// </para>
/// <para>
/// Optional <see cref="IArticleMemoryCache"/> accelerates reads (Phase 3B) and is populated
/// after durable IndexCommitted (Phase 3C). Durable index state remains authoritative: after
/// successful Evict/Invalidate the cache entry is removed (Phase 3D), and a cache hit for an
/// ArtId whose durable state is Evicted/Invalid is dropped rather than returned.
/// </para>
/// <para>
/// Phase 4A: logical death updates in-memory segment Live/Dead using
/// <see cref="StoredArticleLocation.Length"/>. Catalogue Live/Dead are reconstructed from the
/// article index on open/recovery. Physical segment reclamation/compaction is not implemented
/// here — see <see cref="SegmentLifecycle.IsReclaimable"/>.
/// </para>
/// </remarks>
public sealed class FileArticleStorageEngine : IArticleStorageEngine, IArticleStorageRecovery, IAsyncDisposable, IDisposable
{
    private readonly FileArticleJournal _journal;
    private readonly FileSegmentStore _segments;
    private readonly FileArticleIndex _index;
    private readonly IArticleMemoryCache _articleCache;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Queue<ulong> _pendingSequences = new();
    private readonly SemaphoreSlim _workerSignal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _workerCts = new();
    private readonly Task _worker;
    private long _physicalAppendCount;
    private int _disposed;
    private int _suspendBackgroundPersist;

    private FileArticleStorageEngine(
        FileArticleJournal journal,
        FileSegmentStore segments,
        FileArticleIndex index,
        IArticleMemoryCache articleCache,
        ILogger logger,
        TimeProvider timeProvider)
    {
        _journal = journal;
        _segments = segments;
        _index = index;
        _articleCache = articleCache;
        _logger = logger;
        _timeProvider = timeProvider;
        _worker = Task.Run(() => RunPhysicalWorkerAsync(_workerCts.Token));
    }

    /// <summary>Test fault injection points during durable persist (engine-owned only).</summary>
    internal enum PersistFaultPoint
    {
        None = 0,
        AfterSataAppend = 1,
        AfterPhysicalWritten = 2,
        AfterIndexCommit = 3,
        AfterIndexCommitted = 4,
        BeforeSataAppend = 5,
        BeforePhysicalWritten = 6,
        BeforeIndexCommit = 7,
        BeforeIndexCommitted = 8,
    }

    /// <summary>Gets the journal.</summary>
    public FileArticleJournal Journal => _journal;

    /// <summary>Gets the segment store.</summary>
    public FileSegmentStore Segments => _segments;

    /// <summary>Gets the durable index.</summary>
    public FileArticleIndex Index => _index;

    /// <summary>Gets the process-local article memory cache (may be disabled via MaxBytes = 0).</summary>
    public IArticleMemoryCache ArticleCache => _articleCache;

    /// <summary>Gets the segment catalogue owned by the segment store.</summary>
    public FileSegmentCatalogue Catalogue => _segments.Catalogue;

    /// <summary>Number of SATA appends performed by this engine instance (tests).</summary>
    public long PhysicalAppendCount => Volatile.Read(ref _physicalAppendCount);

    /// <summary>
    /// When true, Accept does not enqueue background SATA/index work (crash-after-Accept tests).
    /// Recovery via <see cref="RecoverAsync"/> still completes outstanding sequences.
    /// </summary>
    public bool SuspendBackgroundPersist
    {
        get => Volatile.Read(ref _suspendBackgroundPersist) != 0;
        set => Volatile.Write(ref _suspendBackgroundPersist, value ? 1 : 0);
    }

    /// <summary>Optional one-shot persist fault (cleared when consumed). Tests only.</summary>
    internal PersistFaultPoint TestFaultPoint { get; set; }

    /// <summary>
    /// When true, the next <see cref="TryEvict"/> / <see cref="TryInvalidate"/> on a Present
    /// article returns false before durable <c>TrySetState</c> (tests only; auto-cleared).
    /// </summary>
    internal bool TestFailNextLogicalDeath { get; set; }

    /// <summary>
    /// Opens journal, segment store, and index under <paramref name="options"/> and starts
    /// the background persist worker. Does not run recovery; callers that need crash recovery
    /// must invoke <see cref="RecoverAsync"/>.
    /// </summary>
    /// <param name="options">Validated control/segment directory bounds.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="timeProvider">Optional time provider.</param>
    /// <param name="articleCache">
    /// Optional process-local cache for <see cref="TryRead"/>. When null, a disabled cache
    /// (<c>MaxBytes = 0</c>) is used.
    /// </param>
    public static FileArticleStorageEngine Open(
        ArticleStorageRuntimeOptions options,
        ILogger? logger = null,
        TimeProvider? timeProvider = null,
        IArticleMemoryCache? articleCache = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ControlDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SegmentDir);

        var log = logger ?? NullLogger.Instance;
        FileArticleJournal? journal = null;
        FileSegmentStore? segments = null;
        FileArticleIndex? index = null;
        try
        {
            journal = FileArticleJournal.Open(options, log);
            segments = FileSegmentStore.Open(options, log);
            index = FileArticleIndex.Open(options, log);
            var engine = new FileArticleStorageEngine(
                journal,
                segments,
                index,
                articleCache ?? new ArticleMemoryCache(maxBytes: 0),
                log,
                timeProvider ?? TimeProvider.System);
            journal = null;
            segments = null;
            index = null;
            engine.RebuildSegmentAccountingFromIndex();
            FileArticleStorageEngineLogMessages.Opened(log, options.ControlDir, options.SegmentDir);
            return engine;
        }
        catch
        {
            index?.Dispose();
            segments?.Dispose();
            journal?.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public StorageWritePressure GetWritePressure() => _journal.Pressure;

    /// <inheritdoc />
    public Task<ArticleAcceptResult> AcceptAsync(ArticleRecord record, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (record.ParseStatus != ArticleParseStatus.CanonicalV1 || record.ArtSize <= 0)
        {
            return Task.FromResult(ArticleAcceptResult.RejectedInvalid(record.ArtId, "not-canonical-v1"));
        }

        var artData = record.ArtData;
        if (artData.Length != record.ArtSize
            || !ArticleStorageIntegrity.TryProve(
                artData.Span,
                record.ArtId,
                record.ArtHash,
                record.ArtSize))
        {
            return Task.FromResult(ArticleAcceptResult.RejectedInvalid(record.ArtId, "integrity"));
        }

        JournalAcceptRecord journalRecord;
        lock (_gate)
        {
            if (_index.TryGet(record.ArtId, out var existing)
                && existing.State == ArticleStorageState.Present)
            {
                if (existing.ArtHash == record.ArtHash && existing.ArtSize == record.ArtSize)
                {
                    // Idempotent duplicate: best-effort LRU refresh; never replace with conflict.
                    _ = _articleCache.Put(in record);
                    return Task.FromResult(ArticleAcceptResult.Duplicate(record.ArtId));
                }

                return Task.FromResult(ArticleAcceptResult.Conflict(record.ArtId));
            }

            if (!_journal.TryAppendNewAccept(
                    record.ArtId,
                    record.ArtHash,
                    record.ArtSize,
                    _timeProvider.GetUtcNow(),
                    artData,
                    out journalRecord!,
                    out var rejectOutcome))
            {
                return Task.FromResult(rejectOutcome switch
                {
                    ArticleAcceptOutcome.Duplicate => ArticleAcceptResult.Duplicate(record.ArtId),
                    ArticleAcceptOutcome.Conflict => ArticleAcceptResult.Conflict(record.ArtId),
                    ArticleAcceptOutcome.RejectedInvalid =>
                        ArticleAcceptResult.RejectedInvalid(record.ArtId, "integrity"),
                    _ => ArticleAcceptResult.RejectedPressure(record.ArtId),
                });
            }

            if (!SuspendBackgroundPersist)
            {
                _pendingSequences.Enqueue(journalRecord.Sequence);
            }
        }

        if (!SuspendBackgroundPersist)
        {
            _ = _workerSignal.Release();
        }

        FileArticleStorageEngineLogMessages.Accepted(
            _logger,
            record.ArtId.ToString() ?? string.Empty,
            journalRecord.Sequence,
            record.ArtSize);
        return Task.FromResult(ArticleAcceptResult.Accepted(record.ArtId, journalRecord.Sequence));
    }

    /// <inheritdoc />
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var incomplete = _journal.EnumerateIncomplete();
        FileArticleStorageEngineLogMessages.RecoveryStarted(_logger, incomplete.Count);
        foreach (var item in incomplete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RecoverOneAsync(item, cancellationToken).ConfigureAwait(false);
        }

        FileArticleStorageEngineLogMessages.RecoveryCompleted(_logger);
        RebuildSegmentAccountingFromIndex();
    }

    /// <summary>
    /// Rebuilds catalogue LiveBytes/DeadBytes from the durable article index.
    /// </summary>
    /// <remarks>
    /// ArticleIndex remains authoritative for logical state. Catalogue SizeBytes remains the
    /// physical file extent from segment discovery. This method only repairs in-memory live/dead
    /// views after open or recovery.
    /// </remarks>
    public void RebuildSegmentAccountingFromIndex()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _segments.Catalogue.RebuildLiveDeadFromIndex(_index.Snapshot());
    }

    /// <summary>Waits until no incomplete journal sequences remain.</summary>
    public async Task DrainPendingAsync(CancellationToken cancellationToken)
    {
        while (_journal.EnumerateIncomplete().Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SuspendBackgroundPersist)
            {
                await RecoverAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(1), _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Truncates committed journal prefix via <see cref="FileArticleJournal.CheckpointTruncateCommitted"/>.
    /// Incomplete transactions are retained.
    /// </summary>
    public long CheckpointTruncateCommitted()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            var released = _journal.CheckpointTruncateCommitted();
            FileArticleStorageEngineLogMessages.CheckpointCompleted(_logger, released);
            return released;
        }
        catch (Exception ex)
        {
            FileArticleStorageEngineLogMessages.CheckpointFailed(_logger, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public bool TryRead(ArticleId artId, out ArticleReadResult result)
    {
        result = default;

        // Phase 3B: memory-local acceleration. Hit skips segment IO.
        // Phase 3D: if durable index knows the ArtId and it is not Present, drop stale cache.
        if (_articleCache.TryGet(artId, out var cached))
        {
            if (_index.TryGet(artId, out var cachedIndexMeta)
                && cachedIndexMeta.State != ArticleStorageState.Present)
            {
                BestEffortCacheRemove(artId);
                return false;
            }

            result = new ArticleReadResult(
                new StoredArticleMetadata(
                    cached.ArtId,
                    cached.ArtHash,
                    cached.ArtSize,
                    default,
                    ArticleStorageState.Present,
                    _timeProvider.GetUtcNow()),
                cached.ArtData);
            return true;
        }

        if (!_index.TryGet(artId, out var metadata) || metadata.State != ArticleStorageState.Present)
        {
            return false;
        }

        if (!_segments.TryReadProven(
                metadata.Location,
                metadata.ArtId,
                metadata.ArtHash,
                metadata.ArtSize,
                out var artData)
            || !ArticleStorageIntegrity.TryProve(
                artData.Span,
                metadata.ArtId,
                metadata.ArtHash,
                metadata.ArtSize))
        {
            _ = TryInvalidate(artId);
            return false;
        }

        var durableBefore = _index.DurableWriteCount;
        _index.TouchHint(artId, _timeProvider.GetUtcNow());
        if (_index.DurableWriteCount != durableBefore)
        {
            throw new InvalidOperationException("TouchHint must not perform durable index writes.");
        }

        _ = _index.TryGet(artId, out metadata);
        result = new ArticleReadResult(metadata, artData);

        // Best-effort populate; Put rejection must not fail the durable read.
        if (TryCreateCacheRecord(in metadata, artData, out var cacheRecord))
        {
            _ = _articleCache.Put(in cacheRecord);
        }

        return true;
    }

    /// <inheritdoc />
    public bool TryEvict(ArticleId artId) => TransitionLogicalDeath(artId, ArticleStorageState.Evicted);

    /// <inheritdoc />
    public bool TryInvalidate(ArticleId artId) => TransitionLogicalDeath(artId, ArticleStorageState.Invalid);

    /// <inheritdoc />
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _workerCts.CancelAsync().ConfigureAwait(false);
        try
        {
            _ = _workerSignal.Release();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _workerCts.Dispose();
        _workerSignal.Dispose();
        _index.Dispose();
        _segments.Dispose();
        _journal.Dispose();
        FileArticleStorageEngineLogMessages.Closed(_logger);
        GC.SuppressFinalize(this);
    }

    private async Task RecoverOneAsync(JournalIncompleteSequence incomplete, CancellationToken cancellationToken)
    {
        var accept = incomplete.Accept;
        if (incomplete.PhysicalWritten is { } written)
        {
            FileArticleStorageEngineLogMessages.RecoverPhysicalWritten(
                _logger,
                accept.Sequence,
                written.Location.SegmentId.Value,
                written.Location.Offset,
                written.Location.Length);
            await CompleteFromPhysicalWrittenAsync(accept, written, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Accept-only (Option 1): never scan SATA or discover orphans; always fresh append.
        FileArticleStorageEngineLogMessages.RecoverAcceptOnly(
            _logger,
            accept.Sequence,
            accept.ArtId.ToString() ?? string.Empty);
        var location = await AppendPhysicalAsync(accept.ArtData, cancellationToken).ConfigureAwait(false);
        ThrowIfTestFault(PersistFaultPoint.AfterSataAppend, accept.Sequence);
        var pw = new JournalPhysicalWrittenRecord(1, accept.Sequence, location);
        ThrowIfTestFault(PersistFaultPoint.BeforePhysicalWritten, accept.Sequence);
        var pwOutcome = await _journal.AppendPhysicalWrittenAsync(pw, cancellationToken).ConfigureAwait(false);
        if (pwOutcome == JournalAppendOutcome.Conflict)
        {
            throw new InvalidOperationException(
                $"PhysicalWritten conflict for sequence {accept.Sequence} during recovery.");
        }

        ThrowIfTestFault(PersistFaultPoint.AfterPhysicalWritten, accept.Sequence);
        await CompleteFromPhysicalWrittenAsync(accept, pw, cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteFromPhysicalWrittenAsync(
        JournalAcceptRecord accept,
        JournalPhysicalWrittenRecord written,
        CancellationToken cancellationToken)
    {
        if (!TryProvePhysicalLocation(accept, written.Location, out _))
        {
            FileArticleStorageEngineLogMessages.PhysicalWrittenUnusable(
                _logger,
                accept.Sequence,
                written.Location.SegmentId.Value,
                written.Location.Offset);

            // Journal PhysicalWritten is immutable once set (Conflict on different location).
            // Cannot supersede with a re-append location without Phase 2A contract change.
            // Fail closed: leave outstanding; do not commit Present over corrupt bytes.
            if (_index.TryGet(accept.ArtId, out var existing)
                && existing.State == ArticleStorageState.Present
                && LocationsEqual(existing.Location, written.Location))
            {
                _ = TryInvalidate(accept.ArtId);
            }

            throw new InvalidOperationException(
                $"Physical range for sequence {accept.Sequence} failed integrity proof during recovery.");
        }

        ThrowIfTestFault(PersistFaultPoint.BeforeIndexCommit, accept.Sequence);

        if (_index.TryGet(accept.ArtId, out var present)
            && present.State == ArticleStorageState.Present
            && present.ArtHash == accept.ArtHash
            && present.ArtSize == accept.ArtSize
            && LocationsEqual(present.Location, written.Location))
        {
            await AppendIndexCommittedAsync(accept, cancellationToken).ConfigureAwait(false);
            return;
        }

        var metadata = new StoredArticleMetadata(
            accept.ArtId,
            accept.ArtHash,
            accept.ArtSize,
            written.Location,
            ArticleStorageState.Present,
            _timeProvider.GetUtcNow());

        if (!_index.TryCommitPresent(in metadata))
        {
            if (_index.TryGet(accept.ArtId, out var again)
                && again.State == ArticleStorageState.Present
                && again.ArtHash == accept.ArtHash
                && again.ArtSize == accept.ArtSize
                && LocationsEqual(again.Location, written.Location))
            {
                await AppendIndexCommittedAsync(accept, cancellationToken).ConfigureAwait(false);
                return;
            }

            throw new InvalidOperationException(
                $"Index commit failed for sequence {accept.Sequence} during recovery.");
        }

        ThrowIfTestFault(PersistFaultPoint.AfterIndexCommit, accept.Sequence);
        await AppendIndexCommittedAsync(accept, cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendIndexCommittedAsync(
        JournalAcceptRecord accept,
        CancellationToken cancellationToken)
    {
        ThrowIfTestFault(PersistFaultPoint.BeforeIndexCommitted, accept.Sequence);
        var ic = await _journal
            .AppendIndexCommittedAsync(new JournalIndexCommittedRecord(1, accept.Sequence), cancellationToken)
            .ConfigureAwait(false);
        if (ic is JournalAppendOutcome.Rejected or JournalAppendOutcome.Conflict)
        {
            throw new InvalidOperationException(
                $"IndexCommitted rejected for sequence {accept.Sequence}.");
        }

        // Phase 3C: best-effort RAM populate only after durable IndexCommitted.
        // Failure here must not undo or fail the already-durable transaction.
        TryPopulateCacheAfterDurableCommit(accept);

        ThrowIfTestFault(PersistFaultPoint.AfterIndexCommitted, accept.Sequence);
        FileArticleStorageEngineLogMessages.RecoveredIndexCommitted(
            _logger,
            accept.Sequence,
            accept.ArtId.ToString() ?? string.Empty);
    }

    /// <summary>
    /// Populates the process-local cache from journal Accept ArtData after IndexCommitted.
    /// </summary>
    private void TryPopulateCacheAfterDurableCommit(JournalAcceptRecord accept)
    {
        var metadata = new StoredArticleMetadata(
            accept.ArtId,
            accept.ArtHash,
            accept.ArtSize,
            default,
            ArticleStorageState.Present,
            _timeProvider.GetUtcNow());
        if (!TryCreateCacheRecord(in metadata, accept.ArtData, out var cacheRecord))
        {
            return;
        }

        _ = _articleCache.Put(in cacheRecord);
    }

    private bool TryProvePhysicalLocation(
        JournalAcceptRecord accept,
        in StoredArticleLocation location,
        out ReadOnlyMemory<byte> artData)
    {
        artData = default;

        // Location.Length is the full physical record length from FileSegmentStore.
        if (_segments.TryReadProven(
                location,
                accept.ArtId,
                accept.ArtHash,
                accept.ArtSize,
                out artData)
            && ArticleStorageIntegrity.TryProve(
                artData.Span,
                accept.ArtId,
                accept.ArtHash,
                accept.ArtSize))
        {
            return true;
        }

        // Fallback: some callers may store ArtSize as Length; TryRead extracts ArtData.
        if (_segments.TryRead(location, out artData)
            && ArticleStorageIntegrity.TryProve(
                artData.Span,
                accept.ArtId,
                accept.ArtHash,
                accept.ArtSize))
        {
            return true;
        }

        return false;
    }

    private async Task<StoredArticleLocation> AppendPhysicalAsync(
        ReadOnlyMemory<byte> artData,
        CancellationToken cancellationToken)
    {
        ThrowIfTestFault(PersistFaultPoint.BeforeSataAppend, 0);
        var appender = await _segments.GetActiveAppenderAsync(cancellationToken).ConfigureAwait(false);
        var location = await appender.AppendAsync(artData, cancellationToken).ConfigureAwait(false);
        _ = Interlocked.Increment(ref _physicalAppendCount);
        return location;
    }

    private bool TransitionLogicalDeath(ArticleId artId, ArticleStorageState state)
    {
        if (!_index.TryGet(artId, out var existing))
        {
            return false;
        }

        if (existing.State == state)
        {
            // Ensure RAM cannot serve a logically dead article.
            BestEffortCacheRemove(artId);
            return true;
        }

        if (existing.State != ArticleStorageState.Present)
        {
            return false;
        }

        if (TestFailNextLogicalDeath)
        {
            TestFailNextLogicalDeath = false;
            return false;
        }

        if (!_index.TrySetState(artId, state, _timeProvider.GetUtcNow()))
        {
            // Durable transition failed — leave cache untouched.
            return false;
        }

        // FileSegmentStore already counted LiveBytes at append; move live → dead.
        try
        {
            Catalogue.ApplyLiveDeadDelta(
                existing.Location.SegmentId,
                liveDelta: -existing.Location.Length,
                deadDelta: existing.Location.Length);
        }
        catch (InvalidOperationException)
        {
            // Catalogue entry may be absent in edge tests; logical index transition still stands.
        }

        // Durable success first; cache cleanup is best-effort and must not fail the operation.
        BestEffortCacheRemove(artId);
        return true;
    }

    /// <summary>
    /// Removes <paramref name="artId"/> from the process-local cache without affecting durable
    /// outcomes. On unexpected failure, clears the whole cache as a coherence salvage.
    /// </summary>
    private void BestEffortCacheRemove(ArticleId artId)
    {
        try
        {
            _ = _articleCache.Remove(artId);
        }
        catch (Exception)
        {
            try
            {
                _articleCache.Clear();
            }
            catch (Exception)
            {
                // Cache is non-authoritative; durable state already reflects logical death when
                // this runs after TrySetState. TryRead also drops Evicted/Invalid cache hits.
            }
        }
    }

    /// <summary>
    /// Builds a CanonicalV1 <see cref="ArticleRecord"/> for cache insertion from durable bytes.
    /// </summary>
    private static bool TryCreateCacheRecord(
        in StoredArticleMetadata metadata,
        ReadOnlyMemory<byte> artData,
        out ArticleRecord record)
    {
        record = default;
        if (artData.Length != metadata.ArtSize
            || !ArticleStorageIntegrity.TryProve(
                artData.Span,
                metadata.ArtId,
                metadata.ArtHash,
                metadata.ArtSize))
        {
            return false;
        }

        var bytes = artData.ToArray();
        var fields = ArticleFieldTable.Locate(bytes, NntpArticleHeaderName.Date);
        record = new ArticleRecord(
            metadata.ArtId,
            metadata.ArtHash,
            default,
            artLines: 0,
            canonicalUtc: default,
            ArticleParseStatus.CanonicalV1,
            bytes,
            fields);
        return true;
    }

    private async Task RunPhysicalWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _workerSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                while (true)
                {
                    ulong sequence;
                    lock (_gate)
                    {
                        if (!_pendingSequences.TryDequeue(out sequence))
                        {
                            break;
                        }
                    }

                    try
                    {
                        await PersistSequenceAsync(sequence, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        FileArticleStorageEngineLogMessages.PersistStageFailed(
                            _logger,
                            sequence,
                            "persist",
                            ex);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task PersistSequenceAsync(ulong sequence, CancellationToken cancellationToken)
    {
        var incomplete = _journal.EnumerateIncomplete().FirstOrDefault(s => s.Accept.Sequence == sequence);
        if (incomplete.Accept is null)
        {
            return;
        }

        await RecoverOneAsync(incomplete, cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfTestFault(PersistFaultPoint point, ulong sequence)
    {
        if (TestFaultPoint != point)
        {
            return;
        }

        TestFaultPoint = PersistFaultPoint.None;
        var ex = new IOException($"Injected persist fault at {point} for sequence {sequence}.");
        FileArticleStorageEngineLogMessages.PersistStageFailed(_logger, sequence, point.ToString(), ex);
        throw ex;
    }

    private static bool LocationsEqual(in StoredArticleLocation left, in StoredArticleLocation right) =>
        left.SegmentId.Value == right.SegmentId.Value
        && left.Offset == right.Offset
        && left.Length == right.Length;
}
