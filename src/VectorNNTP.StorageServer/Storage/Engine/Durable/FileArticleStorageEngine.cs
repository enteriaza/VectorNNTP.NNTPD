using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;

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
/// Phase 5E.1 / 5E.2: optional process-local capacity reservation under
/// <see cref="ArticleCapacityOptions"/>. Reservations are not kernel/cross-process filesystem
/// reservations. Article Accept and compaction destination appends share process-local counters
/// under distinct ceilings (<see cref="ArticleCapacityOptions.MaximumUtilization"/> vs
/// MaximumUtilization + <see cref="ArticleCapacityOptions.CompactionHeadroom"/>).
/// </para>
/// <para>
/// Phase 4A: logical death updates in-memory segment Live/Dead using
/// <see cref="StoredArticleLocation.Length"/>. Catalogue Live/Dead are reconstructed from the
/// article index on open/recovery. Physical segment reclamation/compaction is not implemented
/// here — see <see cref="SegmentLifecycle.IsReclaimable"/>.
/// </para>
/// <para>
/// Phase 5F.1a: <c>_pendingSequences</c> is a transient execution queue over durable incomplete
/// journal work. Retryable persist failures (<see cref="IOException"/>,
/// <see cref="UnauthorizedAccessException"/>) requeue with backoff without releasing article
/// capacity reservations. Non-retryable fail-closed outcomes (e.g. unusable PhysicalWritten,
/// PhysicalWritten rejected, catalogue/integrity failures) are not requeued. After
/// <see cref="RecoverAsync"/>, any still-incomplete sequences are re-linked into the pending
/// queue when background persist is enabled.
/// </para>
/// </remarks>
public sealed partial class FileArticleStorageEngine : IArticleStorageEngine, IArticleStorageRecovery, IAsyncDisposable, IDisposable
{
    private readonly FileArticleJournal _journal;
    private readonly FileSegmentStore _segments;
    private readonly FileArticleIndex _index;
    private readonly IArticleMemoryCache _articleCache;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IStorageCapacityReader? _capacityReader;
    private readonly bool _capacityAdmissionEnabled;
    private readonly double _capacityMaximumUtilization;
    private readonly double _capacityCompactionHeadroom;
    private readonly ProcessLocalCapacityLedger _capacityLedger = new();
    private readonly object _gate = new();
    private readonly Queue<ulong> _pendingSequences = new();
    private readonly HashSet<ulong> _pendingSet = new();
    private readonly HashSet<ulong> _persistInFlight = new();
    private readonly Dictionary<ulong, int> _persistRetryAttempts = new();
    private readonly SemaphoreSlim _workerSignal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _workerCts = new();
    private readonly Task _worker;
    private long _physicalAppendCount;
    private long _persistRetryScheduledCount;
    private int _disposed;
    private int _suspendBackgroundPersist;

    private FileArticleStorageEngine(
        FileArticleJournal journal,
        FileSegmentStore segments,
        FileArticleIndex index,
        IArticleMemoryCache articleCache,
        ILogger logger,
        TimeProvider timeProvider,
        IStorageCapacityReader? capacityReader,
        bool capacityAdmissionEnabled,
        double capacityMaximumUtilization,
        double capacityCompactionHeadroom)
    {
        _journal = journal;
        _segments = segments;
        _index = index;
        _articleCache = articleCache;
        _logger = logger;
        _timeProvider = timeProvider;
        _capacityReader = capacityReader;
        _capacityAdmissionEnabled = capacityAdmissionEnabled;
        _capacityMaximumUtilization = capacityMaximumUtilization;
        _capacityCompactionHeadroom = capacityCompactionHeadroom;
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

    /// <summary>Process-local article + compaction reserved bytes (tests / diagnostics).</summary>
    internal long ProcessLocalReservedBytes
    {
        get
        {
            lock (_gate)
            {
                return _capacityLedger.ReservedBytes;
            }
        }
    }

    /// <summary>Process-local article Accept reserved bytes (tests).</summary>
    internal long ProcessLocalArticleReservedBytes
    {
        get
        {
            lock (_gate)
            {
                return _capacityLedger.ArticleReservedBytes;
            }
        }
    }

    /// <summary>Process-local compaction destination reserved bytes (tests).</summary>
    internal long ProcessLocalCompactionReservedBytes
    {
        get
        {
            lock (_gate)
            {
                return _capacityLedger.CompactionReservedBytes;
            }
        }
    }

    /// <summary>Number of Accept sequences holding an article capacity reservation (tests).</summary>
    internal int ProcessLocalReservationCount
    {
        get
        {
            lock (_gate)
            {
                return _capacityLedger.ReservationCount;
            }
        }
    }

    /// <summary>Number of compaction relocation keys holding a reservation (tests).</summary>
    internal int ProcessLocalCompactionReservationCount
    {
        get
        {
            lock (_gate)
            {
                return _capacityLedger.CompactionReservationCount;
            }
        }
    }

    /// <summary>
    /// Observes current DriveInfo capacity and process-local reservations for maintenance
    /// pressure decisions (Phase 5F.2). Does not mutate reservations. Capacity read is outside
    /// <c>_gate</c>; reservation counters are sampled under the gate.
    /// </summary>
    internal CapacityAdmissionPressureSnapshot ObserveCapacityAdmissionPressure()
    {
        if (!_capacityAdmissionEnabled || _capacityReader is null)
        {
            return CapacityAdmissionPressureSnapshot.Disabled;
        }

        var snap = _capacityReader.Read();
        long articleReserved;
        long compactionReserved;
        lock (_gate)
        {
            articleReserved = _capacityLedger.ArticleReservedBytes;
            compactionReserved = _capacityLedger.CompactionReservedBytes;
        }

        return CapacityAdmissionPressureSnapshot.FromCapacityState(
            in snap,
            articleReserved,
            compactionReserved,
            _capacityMaximumUtilization,
            _capacityCompactionHeadroom);
    }

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
    /// Exception type thrown by <see cref="TestFaultPoint"/> (default <see cref="IOException"/>).
    /// Tests only.
    /// </summary>
    internal PersistFaultExceptionKind TestPersistFaultExceptionKind { get; set; }

    /// <summary>
    /// When set, overrides computed persist-retry backoff (use <see cref="TimeSpan.Zero"/> to
    /// avoid wall-clock delays in tests). Tests only.
    /// </summary>
    internal TimeSpan? TestPersistRetryDelay { get; set; }

    /// <summary>Number of persist retries scheduled (tests).</summary>
    internal long PersistRetryScheduledCount => Volatile.Read(ref _persistRetryScheduledCount);

    /// <summary>
    /// Invoked after Accept-only SATA append and before <c>AppendPhysicalWrittenAsync</c>.
    /// Receives the sequence and destination location. Tests only; cleared after invoke.
    /// </summary>
    internal Action<ulong, StoredArticleLocation>? TestHookAfterSataBeforePhysicalWritten { get; set; }

    /// <summary>
    /// Optional one-shot rewrite of the Accept-only SATA location before PhysicalWritten
    /// (tests only; cleared after invoke). Used to force <see cref="JournalAppendOutcome.Rejected"/>.
    /// </summary>
    internal Func<StoredArticleLocation, StoredArticleLocation>? TestRewritePhysicalLocationAfterAppend
    {
        get;
        set;
    }

    /// <summary>
    /// When true, the next <see cref="TryEvict"/> / <see cref="TryInvalidate"/> on a Present
    /// article returns false before durable <c>TrySetState</c> (tests only; auto-cleared).
    /// </summary>
    internal bool TestFailNextLogicalDeath { get; set; }

    /// <summary>Exception kind for <see cref="TestFaultPoint"/> (tests).</summary>
    internal enum PersistFaultExceptionKind : byte
    {
        /// <summary>Throw <see cref="IOException"/> (default; retryable).</summary>
        IoException = 0,

        /// <summary>Throw <see cref="UnauthorizedAccessException"/> (retryable after 5F.1c).</summary>
        UnauthorizedAccess = 1,

        /// <summary>Throw <see cref="InvalidOperationException"/> (non-retryable).</summary>
        InvalidOperation = 2,
    }

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
    /// <param name="capacityReader">
    /// Optional physical capacity source for process-local admission. When capacity admission is
    /// enabled and this is null, a <see cref="CacheDirectoryCapacityReader"/> over
    /// <see cref="ArticleStorageRuntimeOptions.SegmentDir"/> is used.
    /// </param>
    public static FileArticleStorageEngine Open(
        ArticleStorageRuntimeOptions options,
        ILogger? logger = null,
        TimeProvider? timeProvider = null,
        IArticleMemoryCache? articleCache = null,
        IStorageCapacityReader? capacityReader = null)
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
            IStorageCapacityReader? capacity = capacityReader;
            if (options.CapacityAdmissionEnabled && capacity is null)
            {
                capacity = new CacheDirectoryCapacityReader(options.SegmentDir);
            }

            var engine = new FileArticleStorageEngine(
                journal,
                segments,
                index,
                articleCache ?? new ArticleMemoryCache(maxBytes: 0),
                log,
                timeProvider ?? TimeProvider.System,
                capacity,
                options.CapacityAdmissionEnabled,
                options.CapacityMaximumUtilization,
                options.CapacityCompactionHeadroom);
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
        var reservedForAccept = false;
        var requiredBytes = 0L;
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

            if (_capacityAdmissionEnabled)
            {
                if (_capacityReader is null)
                {
                    throw new InvalidOperationException(
                        "Capacity admission is enabled but no IStorageCapacityReader is configured.");
                }

                requiredBytes = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
                var snap = _capacityReader.Read();
                if (!_capacityLedger.WouldFit(
                        snap.UsedBytes,
                        snap.TotalBytes,
                        requiredBytes,
                        _capacityMaximumUtilization))
                {
                    FileArticleStorageEngineLogMessages.RejectedCapacity(
                        _logger,
                        record.ArtId.ToString() ?? string.Empty,
                        requiredBytes,
                        snap.UsedBytes,
                        _capacityLedger.ArticleReservedBytes,
                        _capacityLedger.CompactionReservedBytes,
                        snap.TotalBytes,
                        snap.AvailableBytes,
                        _capacityMaximumUtilization,
                        _capacityCompactionHeadroom);
                    return Task.FromResult(ArticleAcceptResult.RejectedCapacity(record.ArtId));
                }

                _capacityLedger.TentativeAdd(requiredBytes);
                reservedForAccept = true;
            }

            try
            {
                if (!_journal.TryAppendNewAccept(
                        record.ArtId,
                        record.ArtHash,
                        record.ArtSize,
                        _timeProvider.GetUtcNow(),
                        artData,
                        out journalRecord!,
                        out var rejectOutcome))
                {
                    if (reservedForAccept)
                    {
                        _capacityLedger.RollbackUnbound(requiredBytes);
                        reservedForAccept = false;
                    }

                    return Task.FromResult(rejectOutcome switch
                    {
                        ArticleAcceptOutcome.Duplicate => ArticleAcceptResult.Duplicate(record.ArtId),
                        ArticleAcceptOutcome.Conflict => ArticleAcceptResult.Conflict(record.ArtId),
                        ArticleAcceptOutcome.RejectedInvalid =>
                            ArticleAcceptResult.RejectedInvalid(record.ArtId, "integrity"),
                        _ => ArticleAcceptResult.RejectedPressure(record.ArtId),
                    });
                }

                if (reservedForAccept)
                {
                    _capacityLedger.BindSequence(journalRecord.Sequence, requiredBytes);
                    reservedForAccept = false;
                }
            }
            catch
            {
                if (reservedForAccept)
                {
                    _capacityLedger.RollbackUnbound(requiredBytes);
                }

                throw;
            }

            if (!SuspendBackgroundPersist)
            {
                EnqueuePersistWorkUnlocked(journalRecord.Sequence);
            }
        }

        if (!SuspendBackgroundPersist)
        {
            SignalPersistWorker();
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
            await PersistSequenceExclusiveAsync(item.Accept.Sequence, cancellationToken)
                .ConfigureAwait(false);
        }

        FileArticleStorageEngineLogMessages.RecoveryCompleted(_logger);
        var abandonedDestinations = RecoverCompactions();
        ApplyRetiredCompactionsFromJournal();
        RebuildSegmentAccountingFromIndex();
        foreach (var dest in abandonedDestinations)
        {
            MarkAbandonedDestinationDead(dest);
        }

        // Durable recovery finished: re-link any still-incomplete work into the transient queue.
        EnqueueIncompleteFromJournal();
    }

    /// <summary>
    /// Recovers open compaction journal state without scanning SATA or inventing destinations.
    /// </summary>
    /// <remarks>
    /// Phase 4B.2: Intent-only Present@source remains retryable for a later RelocateArticle.
    /// Written destinations are proven; TryRelocate is retried when index still points at source.
    /// Evicted/Invalid never resurrect. Abandoned destination extents are returned so callers can
    /// mark them dead after Phase 4A index accounting rebuild.
    /// </remarks>
    private List<StoredArticleLocation> RecoverCompactions()
    {
        var abandoned = new List<StoredArticleLocation>();
        foreach (var compaction in _journal.EnumerateOpenCompactions())
        {
            var sourceId = compaction.Begin.SourceSegmentId;
            foreach (var relocation in compaction.Relocations)
            {
                if (RecoverOneRelocation(in compaction, in relocation, out var abandonedDest))
                {
                    abandoned.Add(abandonedDest);
                }
            }

            if (compaction.Committed)
            {
                continue;
            }

            var sourceStillPresent = _index.Snapshot()
                .Any(m => m.State == ArticleStorageState.Present
                          && m.Location.SegmentId.Value == sourceId.Value);
            if (!sourceStillPresent)
            {
                _ = _journal.AppendCompactionCommittedAsync(
                        new JournalCompactionCommittedRecord(1, compaction.Begin.CompactionId),
                        CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
        }

        return abandoned;
    }

    private bool RecoverOneRelocation(
        in CompactionJournalSnapshot compaction,
        in CompactionRelocationSnapshot relocation,
        out StoredArticleLocation abandonedDestination)
    {
        abandonedDestination = default;
        var intent = relocation.Intent;
        if (relocation.Written is null)
        {
            // Intent-only: abandon without resurrection, or leave Present@source retryable.
            return false;
        }

        var written = relocation.Written.Value;
        if (!_segments.TryReadProven(
                written.DestinationLocation,
                intent.ArtId,
                intent.ArtHash,
                intent.ArtSize,
                out _))
        {
            throw new InvalidOperationException(
                $"Compaction RelocationWritten destination failed integrity proof " +
                $"(compaction={intent.CompactionId}, relocation={intent.RelocationId}).");
        }

        if (!_index.TryGet(intent.ArtId, out var indexMeta)
            || indexMeta.State != ArticleStorageState.Present)
        {
            abandonedDestination = written.DestinationLocation;
            return true;
        }

        if (LocationsEqual(indexMeta.Location, written.DestinationLocation))
        {
            return false;
        }

        if (LocationsEqual(indexMeta.Location, intent.ExpectedSourceLocation))
        {
            var outcome = _index.TryRelocate(
                intent.ArtId,
                intent.ExpectedSourceLocation,
                written.DestinationLocation,
                intent.ArtHash,
                intent.ArtSize);
            if (outcome is ArticleRelocateOutcome.Relocated or ArticleRelocateOutcome.IdempotentNoOp)
            {
                return false;
            }

            abandonedDestination = written.DestinationLocation;
            return true;
        }

        abandonedDestination = written.DestinationLocation;
        return true;
    }

    private void MarkAbandonedDestinationDead(in StoredArticleLocation destination)
    {
        try
        {
            // After index rebuild, Present live is correct; orphan Written extents are unreferenced
            // and should count as DeadBytes only (not subtract Live again).
            Catalogue.ApplyLiveDeadDelta(
                destination.SegmentId,
                liveDelta: 0,
                deadDelta: destination.Length);
        }
        catch (InvalidOperationException)
        {
            // Catalogue may lack the segment in unit tests; index remains authoritative.
        }
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

            // Durable incompletes may lack a transient queue entry after PersistStageFailed.
            EnqueueIncompleteFromJournal();

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
            // Durable PW already owns physical capacity; release before index work (idempotent).
            ReleaseCapacityReservation(accept.Sequence);
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

        var rewrite = TestRewritePhysicalLocationAfterAppend;
        TestRewritePhysicalLocationAfterAppend = null;
        if (rewrite is not null)
        {
            location = rewrite(location);
        }

        var afterSataHook = TestHookAfterSataBeforePhysicalWritten;
        TestHookAfterSataBeforePhysicalWritten = null;
        afterSataHook?.Invoke(accept.Sequence, location);

        var pwCandidate = new JournalPhysicalWrittenRecord(1, accept.Sequence, location);
        ThrowIfTestFault(PersistFaultPoint.BeforePhysicalWritten, accept.Sequence);
        var pwOutcome = await _journal
            .AppendPhysicalWrittenAsync(pwCandidate, cancellationToken)
            .ConfigureAwait(false);

        switch (pwOutcome)
        {
            case JournalAppendOutcome.Applied:
            case JournalAppendOutcome.IdempotentNoOp:
                // Durable PW owns physical capacity; release before index work.
                ReleaseCapacityReservation(accept.Sequence);
                ThrowIfTestFault(PersistFaultPoint.AfterPhysicalWritten, accept.Sequence);
                await CompleteFromPhysicalWrittenAsync(accept, pwCandidate, cancellationToken)
                    .ConfigureAwait(false);
                return;

            case JournalAppendOutcome.Conflict:
                // Existing durable PW at a different location — never supersede; complete via it.
                if (!TryGetDurablePhysicalWritten(accept.Sequence, out var existingPw))
                {
                    throw new InvalidOperationException(
                        $"PhysicalWritten conflict for sequence {accept.Sequence} " +
                        "but no durable PhysicalWritten was found.");
                }

                ReleaseCapacityReservation(accept.Sequence);
                await CompleteFromPhysicalWrittenAsync(accept, existingPw, cancellationToken)
                    .ConfigureAwait(false);
                return;

            case JournalAppendOutcome.Rejected:
                // Unknown sequence or location length < ArtSize — not PhysicalWritten.
                // Do not release reservation; do not Present/IndexCommitted.
                throw new InvalidOperationException(
                    $"PhysicalWritten rejected for sequence {accept.Sequence} " +
                    "(prerequisite missing or location length below ArtSize).");

            default:
                throw new InvalidOperationException(
                    $"Unexpected PhysicalWritten outcome {pwOutcome} for sequence {accept.Sequence}.");
        }
    }

    private bool TryGetDurablePhysicalWritten(ulong sequence, out JournalPhysicalWrittenRecord written)
    {
        foreach (var item in _journal.EnumerateIncomplete())
        {
            if (item.Accept.Sequence == sequence && item.PhysicalWritten is { } pw)
            {
                written = pw;
                return true;
            }
        }

        written = default;
        return false;
    }

    private void ReleaseCapacityReservation(ulong sequence)
    {
        lock (_gate)
        {
            _ = _capacityLedger.Release(sequence);
        }
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

                        _ = _pendingSet.Remove(sequence);
                    }

                    try
                    {
                        await PersistSequenceExclusiveAsync(sequence, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        FileArticleStorageEngineLogMessages.PersistStageFailed(
                            _logger,
                            sequence,
                            "persist",
                            ex);
                        if (IsRetryablePersistFailure(ex))
                        {
                            SchedulePersistRetry(sequence);
                        }
                        else
                        {
                            FileArticleStorageEngineLogMessages.PersistNonRetryableFailure(
                                _logger,
                                sequence,
                                ex.GetType().Name,
                                ex.Message);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ThrowIfTestFault(PersistFaultPoint point, ulong sequence)
    {
        if (TestFaultPoint != point)
        {
            return;
        }

        TestFaultPoint = PersistFaultPoint.None;
        var kind = TestPersistFaultExceptionKind;
        TestPersistFaultExceptionKind = PersistFaultExceptionKind.IoException;
        Exception ex = kind switch
        {
            PersistFaultExceptionKind.UnauthorizedAccess =>
                new UnauthorizedAccessException($"Injected persist fault at {point} for sequence {sequence}."),
            PersistFaultExceptionKind.InvalidOperation =>
                new InvalidOperationException($"Injected persist fault at {point} for sequence {sequence}."),
            _ => new IOException($"Injected persist fault at {point} for sequence {sequence}."),
        };
        FileArticleStorageEngineLogMessages.PersistStageFailed(_logger, sequence, point.ToString(), ex);
        throw ex;
    }

    private static bool LocationsEqual(in StoredArticleLocation left, in StoredArticleLocation right) =>
        left.SegmentId.Value == right.SegmentId.Value
        && left.Offset == right.Offset
        && left.Length == right.Length;
}
