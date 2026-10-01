using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Engine;
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
/// reservations. <see cref="CapacityVolumes"/> resolves <c>SegmentDir</c> and <c>ControlDir</c>
/// only when admission is enabled. The same physical volume shares one reader and one ledger.
/// Different volumes keep a segment ledger and a control ledger.
/// Article Accept and compaction destination appends use the segment ledger.
/// Accept uses <see cref="ArticleCapacityOptions.MaximumUtilization"/>.
/// Compaction destination appends use that ceiling plus
/// <see cref="ArticleCapacityOptions.CompactionHeadroom"/>.
/// Each physical segment copy reserves <c>SegmentRecordCodec.RecordLengthForArtSize(ArtSize)</c>
/// on the segment ledger before the append. That reservation stays after durable PhysicalWritten
/// until a later reclamation phase. A retirement-seal retry reserves another copy before it appends.
/// Each durable Accept also reserves <c>ArtSize + 132</c> on the control ledger for the journal
/// sequence. That reservation is released only after a checkpoint replacement omits the sequence.
/// The same Accept reserves <see cref="ArticleIndexRecordCodec.RecordLength"/> bytes on the control
/// ledger before the durable Present index frame.
/// Every later physical index frame, including Evicted, Invalid, and relocation Present, reserves
/// another <see cref="ArticleIndexRecordCodec.RecordLength"/> bytes before it is appended. A reservation stays through IndexCommitted and logical
/// state changes, and is released only when an index checkpoint replacement retires that physical frame.
/// Each durable compaction-journal frame reserves its exact codec length on the control ledger
/// under MaximumUtilization + CompactionHeadroom immediately before it is appended:
/// CompactionBegin 36, RelocationIntent 92, RelocationWritten 48, CompactionCommitted 20,
/// CompactionRetired 36. Those reservations stay until a journal checkpoint replacement omits
/// that entire compaction. SequenceFence stays inside the checkpoint image reservation.
/// A checkpoint reserves the exact serialized temp length on the control ledger against
/// MaximumUtilization before the temp file is created. On a shared volume that ledger is the
/// segment ledger, so the reservation stays in both decisions until the extra temp file is gone.
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
/// <see cref="UnauthorizedAccessException"/>) requeue with backoff and keep reservations for
/// segment copies already written. Non-retryable failures release only copies that were not
/// written. Non-retryable fail-closed outcomes (e.g. unusable PhysicalWritten,
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
    private readonly CapacityVolume? _segmentCapacity;
    private readonly CapacityVolume? _controlCapacity;
    private readonly bool _capacityAdmissionEnabled;

    private PendingAcceptAdmission? _pendingAcceptAdmission;

    private readonly record struct PendingAcceptAdmission(
        ArticleId ArtId,
        ulong ArtHash,
        int ArtSize,
        long SegmentBytes,
        long JournalBytes,
        long IndexBytes);
    private readonly double _capacityMaximumUtilization;
    private readonly double _capacityCompactionHeadroom;
    private readonly object _gate = new();
    private readonly Queue<ulong> _pendingSequences = new();
    private readonly HashSet<ulong> _pendingSet = new();
    private readonly HashSet<ulong> _persistInFlight = new();
    private readonly Dictionary<ulong, int> _persistRetryAttempts = new();
    private readonly Dictionary<ulong, int> _persistBlockedRetryAttempts = new();

    /// <summary>
    /// Accept sequences journaled by this process that have not attempted a physical append
    /// which could leave bytes. Not durable: a restarted process does not rebuild it, so
    /// replayed Accept-only sequences are absent and keep physical orphan discovery.
    /// Evicted and Invalid re-accepts are never inserted.
    /// </summary>
    private readonly HashSet<ulong> _acceptWithoutPhysicalBytes = new();
    private readonly SemaphoreSlim _workerSignal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _workerCts = new();
    private readonly Task _worker;
    private long _physicalAppendCount;
    private long _persistRetryScheduledCount;
    private long _persistBlockedRetryScheduledCount;
    private int _disposed;
    private int _suspendBackgroundPersist;

    private FileArticleStorageEngine(
        FileArticleJournal journal,
        FileSegmentStore segments,
        FileArticleIndex index,
        IArticleMemoryCache articleCache,
        ILogger logger,
        TimeProvider timeProvider,
        CapacityVolumes capacity,
        bool capacityAdmissionEnabled,
        double capacityMaximumUtilization,
        double capacityCompactionHeadroom)
    {
        ArgumentNullException.ThrowIfNull(capacity);
        _journal = journal;
        _segments = segments;
        _index = index;
        _articleCache = articleCache;
        _logger = logger;
        _timeProvider = timeProvider;
        _segmentCapacity = capacity.Segment;
        _controlCapacity = capacity.Control;
        _capacityAdmissionEnabled = capacityAdmissionEnabled;
        _capacityMaximumUtilization = capacityMaximumUtilization;
        _capacityCompactionHeadroom = capacityCompactionHeadroom;
        journal.RetainRetiredCompaction = segmentId =>
            Catalogue.TryGet(segmentId, out var info) && info.State != SegmentState.Retired;
        if (capacityAdmissionEnabled)
        {
            var checkpointCapacity = CreateCheckpointCapacity();
            journal.CheckpointCapacity = checkpointCapacity;
            index.AttachCheckpointCapacity(checkpointCapacity);
            index.OnIndexPrefixRetired = ApplyIndexPrefixRetirement;
            ReconstructJournalSequenceReservations();
            ReconstructIndexPresentReservations();
            ReconstructCompactionJournalReservations();
        }

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

    /// <summary>Segment-volume capacity, or null when admission is disabled.</summary>
    internal CapacityVolume? SegmentCapacity => _segmentCapacity;

    /// <summary>Control-volume capacity, or null when admission is disabled.</summary>
    internal CapacityVolume? ControlCapacity => _controlCapacity;

    /// <summary>Process-local article + compaction + checkpoint bytes on the segment ledger (tests / diagnostics).</summary>
    internal long ProcessLocalReservedBytes =>
        ReadSegmentLedger(static ledger => ledger.ReservedBytes);

    /// <summary>Process-local article Accept reserved bytes on the segment ledger (tests).</summary>
    internal long ProcessLocalArticleReservedBytes =>
        ReadSegmentLedger(static ledger => ledger.ArticleReservedBytes);

    /// <summary>Process-local compaction destination reserved bytes on the segment ledger (tests).</summary>
    internal long ProcessLocalCompactionReservedBytes =>
        ReadSegmentLedger(static ledger => ledger.CompactionReservedBytes);

    /// <summary>Number of Accept sequences holding an article capacity reservation (tests).</summary>
    internal int ProcessLocalReservationCount =>
        ReadSegmentLedger(static ledger => ledger.ReservationCount);

    /// <summary>Number of physical segment-copy reservations on the segment ledger (tests).</summary>
    internal int ProcessLocalSegmentCopyCount =>
        ReadSegmentLedger(static ledger => ledger.SegmentCopyCount);

    /// <summary>Journal-sequence reserved bytes on the control ledger (tests).</summary>
    internal long ProcessLocalJournalReservedBytes =>
        ReadControlLedger(static ledger => ledger.JournalReservedBytes);

    /// <summary>Number of journal sequences holding a reservation on the control ledger (tests).</summary>
    internal int ProcessLocalJournalReservationCount =>
        ReadControlLedger(static ledger => ledger.JournalReservationCount);

    /// <summary>Present-frame reserved bytes on the control ledger, including not-yet-appended holds (tests).</summary>
    internal long ProcessLocalIndexReservedBytes =>
        ReadControlLedger(static ledger => ledger.IndexReservedBytes);

    /// <summary>Physical Present frames holding an index reservation on the control ledger (tests).</summary>
    internal int ProcessLocalIndexFrameCount =>
        ReadControlLedger(static ledger => ledger.IndexFrameCount);

    /// <summary>Compaction-journal frame bytes reserved on the control ledger (tests).</summary>
    internal long ProcessLocalCompactionJournalReservedBytes =>
        ReadControlLedger(static ledger => ledger.CompactionJournalReservedBytes);

    /// <summary>Compaction-journal frames reserved on the control ledger (tests).</summary>
    internal int ProcessLocalCompactionJournalFrameCount =>
        ReadControlLedger(static ledger => ledger.CompactionJournalFrameCount);

    /// <summary>Number of compaction relocation keys holding a reservation (tests).</summary>
    internal int ProcessLocalCompactionReservationCount =>
        ReadSegmentLedger(static ledger => ledger.CompactionReservationCount);

    /// <summary>
    /// Observes segment-volume capacity and that ledger's reservations for maintenance
    /// pressure decisions (Phase 5F.2). Does not mutate reservations. The capacity read is
    /// outside the volume lock; reservation counters are sampled under it.
    /// Checkpoint bytes are those currently on the segment ledger, which includes control
    /// checkpoint reservations only when both directories share that volume.
    /// </summary>
    internal CapacityAdmissionPressureSnapshot ObserveCapacityAdmissionPressure()
    {
        if (!_capacityAdmissionEnabled || _segmentCapacity is null)
        {
            return CapacityAdmissionPressureSnapshot.Disabled;
        }

        var snap = _segmentCapacity.Reader.Read();
        var counters = _segmentCapacity.WithLedger(static ledger => (
            ledger.ArticleReservedBytes,
            ledger.CompactionReservedBytes,
            ledger.CheckpointReservedBytes,
            ledger.JournalReservedBytes,
            ledger.IndexReservedBytes,
            ledger.CompactionJournalReservedBytes));

        return CapacityAdmissionPressureSnapshot.FromCapacityState(
            in snap,
            counters.ArticleReservedBytes,
            counters.CompactionReservedBytes,
            _capacityMaximumUtilization,
            _capacityCompactionHeadroom,
            checkpointReservedBytes: counters.CheckpointReservedBytes,
            journalReservedBytes: counters.JournalReservedBytes,
            indexReservedBytes: counters.IndexReservedBytes,
            compactionJournalReservedBytes: counters.CompactionJournalReservedBytes);
    }

    private T ReadControlLedger<T>(Func<ProcessLocalCapacityLedger, T> read)
        where T : struct
    {
        if (_controlCapacity is null)
        {
            return default;
        }

        return _controlCapacity.WithLedger(read);
    }

    private T ReadSegmentLedger<T>(Func<ProcessLocalCapacityLedger, T> read)
        where T : struct
    {
        if (_segmentCapacity is null)
        {
            return default;
        }

        return _segmentCapacity.WithLedger(read);
    }

    private bool TryReserveAcceptPair(ArticleRecord record, long segmentBytes, long journalBytes, long indexBytes)
    {
        if (_pendingAcceptAdmission is { } pending
            && pending.ArtId == record.ArtId
            && pending.ArtHash == record.ArtHash
            && pending.ArtSize == record.ArtSize
            && pending.SegmentBytes == segmentBytes
            && pending.JournalBytes == journalBytes
            && pending.IndexBytes == indexBytes)
        {
            return true;
        }

        var segment = RequireSegmentVolume();
        var control = RequireControlVolume();
        var segmentDecision = Admit(
            segment,
            segmentBytes,
            _capacityMaximumUtilization,
            static _ => false,
            ledger => ledger.TentativeAdd(segmentBytes));
        if (!segmentDecision.Admitted)
        {
            FileArticleStorageEngineLogMessages.RejectedCapacity(
                _logger,
                record.ArtId.ToString() ?? string.Empty,
                segmentBytes,
                segmentDecision.Snapshot.UsedBytes,
                segmentDecision.ArticleReservedBytes,
                segmentDecision.CompactionReservedBytes,
                segmentDecision.CheckpointReservedBytes,
                segmentDecision.Snapshot.TotalBytes,
                segmentDecision.Snapshot.AvailableBytes,
                _capacityMaximumUtilization,
                _capacityCompactionHeadroom);
            return false;
        }

        var journalDecision = Admit(
            control,
            journalBytes,
            _capacityMaximumUtilization,
            static _ => false,
            ledger => ledger.TentativeAddJournal(journalBytes));
        if (!journalDecision.Admitted)
        {
            segment.WithLedger(ledger =>
            {
                ledger.RollbackUnbound(segmentBytes);
                return 0;
            });
            FileArticleStorageEngineLogMessages.RejectedCapacity(
                _logger,
                record.ArtId.ToString() ?? string.Empty,
                journalBytes,
                journalDecision.Snapshot.UsedBytes,
                journalDecision.ArticleReservedBytes,
                journalDecision.CompactionReservedBytes,
                journalDecision.CheckpointReservedBytes,
                journalDecision.Snapshot.TotalBytes,
                journalDecision.Snapshot.AvailableBytes,
                _capacityMaximumUtilization,
                _capacityCompactionHeadroom);
            return false;
        }

        var indexDecision = Admit(
            control,
            indexBytes,
            _capacityMaximumUtilization,
            static _ => false,
            ledger => ledger.TentativeAddIndex(indexBytes));
        if (indexDecision.Admitted)
        {
            return true;
        }

        control.WithLedger(ledger =>
        {
            ledger.RollbackUnboundJournal(journalBytes);
            return 0;
        });
        segment.WithLedger(ledger =>
        {
            ledger.RollbackUnbound(segmentBytes);
            return 0;
        });
        FileArticleStorageEngineLogMessages.RejectedCapacity(
            _logger,
            record.ArtId.ToString() ?? string.Empty,
            indexBytes,
            indexDecision.Snapshot.UsedBytes,
            indexDecision.ArticleReservedBytes,
            indexDecision.CompactionReservedBytes,
            indexDecision.CheckpointReservedBytes,
            indexDecision.Snapshot.TotalBytes,
            indexDecision.Snapshot.AvailableBytes,
            _capacityMaximumUtilization,
            _capacityCompactionHeadroom);
        return false;
    }

    private void RollbackUnboundAccept(
        bool reservedSegment,
        bool reservedJournal,
        bool reservedIndex,
        long segmentBytes,
        long journalBytes,
        long indexBytes)
    {
        if (reservedSegment)
        {
            RequireSegmentVolume().WithLedger(ledger =>
            {
                ledger.RollbackUnbound(segmentBytes);
                return 0;
            });
        }

        if (reservedJournal)
        {
            RequireControlVolume().WithLedger(ledger =>
            {
                ledger.RollbackUnboundJournal(journalBytes);
                return 0;
            });
        }

        if (reservedIndex)
        {
            RequireControlVolume().WithLedger(ledger =>
            {
                ledger.RollbackUnboundIndex(indexBytes);
                return 0;
            });
        }
    }

    private void ReconstructJournalSequenceReservations()
    {
        var retained = _journal.CopyRetainedJournalSequences();
        if (retained.Length == 0)
        {
            return;
        }

        RequireControlVolume().WithLedger(ledger =>
        {
            foreach (var (sequence, artSize) in retained)
            {
                if (ledger.HoldsJournal(sequence))
                {
                    continue;
                }

                var bytes = ArticleJournalFrameCodec.SequenceReservationBytes(artSize);
                ledger.TentativeAddJournal(bytes);
                ledger.BindJournalSequence(sequence, bytes);
            }

            return 0;
        });
    }

    private void ReleaseRetiredJournalSequences(ulong[] omittedSequences)
    {
        if (!_capacityAdmissionEnabled || omittedSequences.Length == 0)
        {
            return;
        }

        RequireControlVolume().WithLedger(ledger =>
        {
            foreach (var sequence in omittedSequences)
            {
                _ = ledger.ReleaseJournal(sequence);
            }

            return 0;
        });
    }

    private void ReleaseRetiredCompactionJournals(ulong[] omittedCompactionIds)
    {
        if (!_capacityAdmissionEnabled || omittedCompactionIds.Length == 0)
        {
            return;
        }

        RequireControlVolume().WithLedger(ledger =>
        {
            foreach (var compactionId in omittedCompactionIds)
            {
                _ = ledger.ReleaseCompactionJournal(compactionId);
            }

            return 0;
        });
    }

    private void ReconstructCompactionJournalReservations()
    {
        var compactions = _journal.EnumerateCompactions();
        if (compactions.Count == 0)
        {
            return;
        }

        RequireControlVolume().WithLedger(ledger =>
        {
            foreach (var compaction in compactions)
            {
                AddRetainedCompactionJournalFrame(
                    ledger,
                    compaction.Begin.CompactionId,
                    CompactionJournalFrameKind.Begin,
                    relocationId: 0,
                    ArticleJournalFrameCodec.CompactionBeginFrameLength);
                foreach (var relocation in compaction.Relocations)
                {
                    AddRetainedCompactionJournalFrame(
                        ledger,
                        compaction.Begin.CompactionId,
                        CompactionJournalFrameKind.Intent,
                        relocation.Intent.RelocationId,
                        ArticleJournalFrameCodec.RelocationIntentFrameLength);
                    if (relocation.Written is not null)
                    {
                        AddRetainedCompactionJournalFrame(
                            ledger,
                            compaction.Begin.CompactionId,
                            CompactionJournalFrameKind.Written,
                            relocation.Intent.RelocationId,
                            ArticleJournalFrameCodec.RelocationWrittenFrameLength);
                    }
                }

                if (compaction.Committed)
                {
                    AddRetainedCompactionJournalFrame(
                        ledger,
                        compaction.Begin.CompactionId,
                        CompactionJournalFrameKind.Committed,
                        relocationId: 0,
                        ArticleJournalFrameCodec.CompactionCommittedFrameLength);
                }

                if (compaction.Retired is not null)
                {
                    AddRetainedCompactionJournalFrame(
                        ledger,
                        compaction.Begin.CompactionId,
                        CompactionJournalFrameKind.Retired,
                        relocationId: 0,
                        ArticleJournalFrameCodec.CompactionRetiredFrameLength);
                }
            }

            return 0;
        });
    }

    private static void AddRetainedCompactionJournalFrame(
        ProcessLocalCapacityLedger ledger,
        ulong compactionId,
        CompactionJournalFrameKind kind,
        ulong relocationId,
        int bytes)
    {
        if (ledger.HoldsCompactionJournalFrame(compactionId, kind, relocationId))
        {
            return;
        }

        _ = ledger.TryAddCompactionJournalFrame(compactionId, kind, relocationId, bytes);
    }

    private enum CompactionJournalAdmit
    {
        Denied = 0,
        AlreadyHeld = 1,
        NewlyReserved = 2,
    }

    private readonly record struct CompactionJournalAppend(JournalAppendOutcome Outcome, bool CapacityDenied);

    private async ValueTask<CompactionJournalAppend> AppendReservedCompactionJournalFrameAsync(
        ulong compactionId,
        CompactionJournalFrameKind kind,
        ulong relocationId,
        int frameBytes,
        Func<CancellationToken, ValueTask<JournalAppendOutcome>> append,
        CancellationToken cancellationToken)
    {
        if (!_capacityAdmissionEnabled)
        {
            return new CompactionJournalAppend(
                await append(cancellationToken).ConfigureAwait(false),
                CapacityDenied: false);
        }

        if (CompactionJournalFrameIsDurable(compactionId, kind, relocationId))
        {
            return new CompactionJournalAppend(
                await append(cancellationToken).ConfigureAwait(false),
                CapacityDenied: false);
        }

        var admit = TryAdmitCompactionJournalFrame(compactionId, kind, relocationId, frameBytes);
        if (admit == CompactionJournalAdmit.Denied)
        {
            return new CompactionJournalAppend(JournalAppendOutcome.Rejected, CapacityDenied: true);
        }

        try
        {
            var outcome = await append(cancellationToken).ConfigureAwait(false);
            if (admit == CompactionJournalAdmit.NewlyReserved
                && outcome is not (JournalAppendOutcome.Applied or JournalAppendOutcome.IdempotentNoOp))
            {
                ReleaseCompactionJournalFrame(compactionId, kind, relocationId);
            }

            return new CompactionJournalAppend(outcome, CapacityDenied: false);
        }
        catch (Exception ex)
        {
            var createdPending = ex is UnreconciledDurableTailException { CreatedByThisCall: true };
            if (!createdPending
                && admit == CompactionJournalAdmit.NewlyReserved
                && !CompactionJournalFrameIsDurable(compactionId, kind, relocationId))
            {
                ReleaseCompactionJournalFrame(compactionId, kind, relocationId);
            }

            throw;
        }
    }

    private bool CompactionJournalFrameIsDurable(
        ulong compactionId,
        CompactionJournalFrameKind kind,
        ulong relocationId)
    {
        if (!_journal.TryGetCompaction(compactionId, out var snapshot))
        {
            return false;
        }

        return kind switch
        {
            CompactionJournalFrameKind.Begin => true,
            CompactionJournalFrameKind.Committed => snapshot.Committed,
            CompactionJournalFrameKind.Retired => snapshot.Retired is not null,
            CompactionJournalFrameKind.Intent => snapshot.Relocations.Any(
                relocation => relocation.Intent.RelocationId == relocationId),
            CompactionJournalFrameKind.Written => snapshot.Relocations.Any(
                relocation => relocation.Intent.RelocationId == relocationId && relocation.Written is not null),
            _ => false,
        };
    }

    private CompactionJournalAdmit TryAdmitCompactionJournalFrame(
        ulong compactionId,
        CompactionJournalFrameKind kind,
        ulong relocationId,
        int frameBytes)
    {
        var volume = RequireControlVolume();
        var ceiling = _capacityMaximumUtilization + _capacityCompactionHeadroom;
        var added = false;
        var decision = Admit(
            volume,
            frameBytes,
            ceiling,
            ledger => ledger.HoldsCompactionJournalFrame(compactionId, kind, relocationId),
            ledger => added = ledger.TryAddCompactionJournalFrame(compactionId, kind, relocationId, frameBytes));
        if (decision.AlreadySatisfied || (decision.Admitted && !added))
        {
            return CompactionJournalAdmit.AlreadyHeld;
        }

        if (decision.Admitted)
        {
            return CompactionJournalAdmit.NewlyReserved;
        }

        FileArticleStorageEngineLogMessages.RejectedCompactionJournalCapacity(
            _logger,
            compactionId,
            kind.ToString(),
            relocationId,
            frameBytes,
            decision.Snapshot.UsedBytes,
            decision.ReservedBytes,
            decision.Snapshot.TotalBytes,
            ceiling);
        return CompactionJournalAdmit.Denied;
    }

    private void ReleaseCompactionJournalFrame(
        ulong compactionId,
        CompactionJournalFrameKind kind,
        ulong relocationId)
    {
        RequireControlVolume().WithLedger(ledger =>
        {
            _ = ledger.ReleaseCompactionJournalFrame(compactionId, kind, relocationId);
            return 0;
        });
    }

    private void ReconstructIndexPresentReservations()
    {
        var retained = _index.CopyRetainedIndexFrames();
        if (retained.Length == 0)
        {
            return;
        }

        var frameBytes = (long)ArticleIndexRecordCodec.RecordLength;
        RequireControlVolume().WithLedger(ledger =>
        {
            foreach (var frame in retained)
            {
                ledger.AddRetainedIndexFrame(
                    frame.ArtId,
                    frame.FileOffset,
                    frame.SnapshotGeneration,
                    frameBytes);
            }

            return 0;
        });
    }

    private void ApplyIndexPrefixRetirement(IndexPrefixRetirement retirement)
    {
        if (!_capacityAdmissionEnabled)
        {
            return;
        }

        RequireControlVolume().WithLedger(ledger =>
        {
            ledger.ApplyIndexPrefixRetirement(
                retirement.CoveredIndexLength,
                retirement.NewFrameBase,
                retirement.SnapshotGeneration,
                ArticleIndexRecordCodec.RecordLength,
                retirement.SnapshotArticleIds);
            return 0;
        });
    }

    private CapacityVolume RequireSegmentVolume() =>
        _segmentCapacity ?? throw new InvalidOperationException(
            "Capacity admission is enabled but no segment capacity volume is configured.");

    private CapacityVolume RequireControlVolume() =>
        _controlCapacity ?? throw new InvalidOperationException(
            "Capacity admission is enabled but no control capacity volume is configured.");

    /// <summary>
    /// True when every Closed segment has completed historical extent accounting.
    /// Derived from per-segment <see cref="SegmentInfo.ExtentAccountingComplete"/>.
    /// False after open and after every index rebuild while any Closed segment remains
    /// unaccounted. Not the authority for an individual compaction decision.
    /// </summary>
    internal bool IsUnreferencedExtentAccountingComplete =>
        Catalogue.AreAllClosedSegmentsAccounted();

    /// <summary>
    /// Invoked inside the catalogue lock, before a Closed segment's dead-byte replacement.
    /// Tests only. A concurrent live/dead mutation started here blocks until the replacement
    /// is published.
    /// </summary>
    internal Action? TestHookDuringClosedAccountingCommit { get; set; }

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
    /// Number of deferred retries scheduled after a non-retryable persist failure (tests).
    /// Distinct from <see cref="PersistRetryScheduledCount"/>.
    /// </summary>
    internal long PersistBlockedRetryScheduledCount => Volatile.Read(ref _persistBlockedRetryScheduledCount);

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
    /// Optional rewrite applied to each Accept-only append until the function returns null.
    /// Tests only. A null result clears the hook and keeps the real location.
    /// Used to force retirement-seal retries onto a sealed segment id.
    /// </summary>
    internal Func<StoredArticleLocation, StoredArticleLocation?>? TestRewriteSealedPhysicalLocation
    {
        get;
        set;
    }

    /// <summary>
    /// When true, the next <see cref="TryEvict"/> / <see cref="TryInvalidate"/> on a Present
    /// article returns false before durable <c>TrySetState</c> (tests only; auto-cleared).
    /// </summary>
    internal bool TestFailNextLogicalDeath { get; set; }

    /// <summary>
    /// Invoked after a cache-miss index snapshot and before the segment read.
    /// Tests only; cleared before invoke. Must not be used to hold the index lock across IO.
    /// </summary>
    internal Action<ArticleId, StoredArticleLocation>? TestHookAfterIndexSnapshotBeforeSegmentRead { get; set; }

    /// <summary>
    /// Invoked after a failed read is judged current and before the expected-location
    /// invalidation compare-and-set. Tests only; cleared before invoke. Runs outside the index lock.
    /// </summary>
    internal Action<ArticleId, StoredArticleLocation>? TestHookBeforeExpectedInvalidation { get; set; }

    /// <summary>
    /// Number of upcoming indexed proven reads to treat as failures without segment IO.
    /// Tests only. Relocation reads are unaffected.
    /// </summary>
    internal int TestFailNextIndexedProvenReads { get; set; }

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
    /// Optional segment-volume capacity source. When admission is enabled, the volumes share one
    /// physical volume, and this is null, a <see cref="CacheDirectoryCapacityReader"/> over
    /// <see cref="ArticleStorageRuntimeOptions.SegmentDir"/> is used for that shared volume.
    /// When the volumes differ and this is null, that reader is created for the segment volume only.
    /// </param>
    public static FileArticleStorageEngine Open(
        ArticleStorageRuntimeOptions options,
        ILogger? logger = null,
        TimeProvider? timeProvider = null,
        IArticleMemoryCache? articleCache = null,
        IStorageCapacityReader? capacityReader = null) =>
        Open(
            options,
            volumeProbe: null,
            capacityReader,
            controlCapacityReader: null,
            logger,
            timeProvider,
            articleCache);

    /// <summary>
    /// Opens the engine with an explicit volume probe and optional per-volume readers.
    /// </summary>
    /// <param name="options">Validated control/segment directory bounds.</param>
    /// <param name="volumeProbe">
    /// Volume resolver. Used only when admission is enabled. When null, the OS probe is used.
    /// </param>
    /// <param name="capacityReader">Optional segment-volume capacity source.</param>
    /// <param name="controlCapacityReader">
    /// Optional control-volume capacity source used only when the control volume differs from the
    /// segment volume.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="timeProvider">Optional time provider.</param>
    /// <param name="articleCache">Optional process-local cache.</param>
    internal static FileArticleStorageEngine Open(
        ArticleStorageRuntimeOptions options,
        IStorageVolumeProbe? volumeProbe,
        IStorageCapacityReader? capacityReader = null,
        IStorageCapacityReader? controlCapacityReader = null,
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
            var capacity = CapacityVolumes.Resolve(
                options.SegmentDir,
                options.ControlDir,
                options.CapacityAdmissionEnabled,
                capacityReader,
                controlCapacityReader,
                volumeProbe);

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
        var reservedSegment = false;
        var reservedJournal = false;
        var reservedIndex = false;
        var segmentBytes = 0L;
        var journalBytes = 0L;
        var indexBytes = 0L;
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
                segmentBytes = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
                journalBytes = ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize);
                indexBytes = ArticleIndexRecordCodec.RecordLength;
                if (!TryReserveAcceptPair(record, segmentBytes, journalBytes, indexBytes))
                {
                    return Task.FromResult(ArticleAcceptResult.RejectedCapacity(record.ArtId));
                }

                reservedSegment = true;
                reservedJournal = true;
                reservedIndex = true;
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
                    RollbackUnboundAccept(
                        reservedSegment,
                        reservedJournal,
                        reservedIndex,
                        segmentBytes,
                        journalBytes,
                        indexBytes);
                    reservedSegment = false;
                    reservedJournal = false;
                    reservedIndex = false;

                    return Task.FromResult(rejectOutcome switch
                    {
                        ArticleAcceptOutcome.Duplicate => ArticleAcceptResult.Duplicate(record.ArtId),
                        ArticleAcceptOutcome.Conflict => ArticleAcceptResult.Conflict(record.ArtId),
                        ArticleAcceptOutcome.RejectedInvalid =>
                            ArticleAcceptResult.RejectedInvalid(record.ArtId, "integrity"),
                        _ => ArticleAcceptResult.RejectedPressure(record.ArtId),
                    });
                }

                if (reservedSegment)
                {
                    RequireSegmentVolume().WithLedger(ledger =>
                    {
                        ledger.BindSequence(journalRecord.Sequence, segmentBytes);
                        return 0;
                    });
                    reservedSegment = false;
                }

                if (reservedJournal)
                {
                    RequireControlVolume().WithLedger(ledger =>
                    {
                        ledger.BindJournalSequence(journalRecord.Sequence, journalBytes);
                        return 0;
                    });
                    reservedJournal = false;
                }

                if (reservedIndex)
                {
                    RequireControlVolume().WithLedger(ledger =>
                    {
                        ledger.BindIndexUnbound(journalRecord.Sequence, indexBytes);
                        return 0;
                    });
                    reservedIndex = false;
                }

                if (!_index.TryGet(record.ArtId, out var indexed)
                    || indexed.State is not (ArticleStorageState.Evicted or ArticleStorageState.Invalid))
                {
                    _ = _acceptWithoutPhysicalBytes.Add(journalRecord.Sequence);
                }

                _pendingAcceptAdmission = null;
            }
            catch (Exception ex)
            {
                var samePending = _pendingAcceptAdmission is { } owned
                    && owned.ArtId == record.ArtId
                    && owned.ArtHash == record.ArtHash
                    && owned.ArtSize == record.ArtSize;
                if (ex is UnreconciledDurableTailException unreconciled)
                {
                    if (unreconciled.CreatedByThisCall)
                    {
                        if (_capacityAdmissionEnabled && _pendingAcceptAdmission is null)
                        {
                            _pendingAcceptAdmission = new PendingAcceptAdmission(
                                record.ArtId,
                                record.ArtHash,
                                record.ArtSize,
                                segmentBytes,
                                journalBytes,
                                indexBytes);
                        }
                    }
                    else if (_capacityAdmissionEnabled && !samePending)
                    {
                        RollbackUnboundAccept(
                            reservedSegment,
                            reservedJournal,
                            reservedIndex,
                            segmentBytes,
                            journalBytes,
                            indexBytes);
                    }

                    throw;
                }

                RollbackUnboundAccept(
                    reservedSegment,
                    reservedJournal,
                    reservedIndex,
                    segmentBytes,
                    journalBytes,
                    indexBytes);
                if (samePending)
                {
                    _pendingAcceptAdmission = null;
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
        var acceptOnlyCandidates = CollectAcceptOnlyRecoveryCandidates(incomplete);
        foreach (var item in incomplete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PersistSequenceExclusiveAsync(
                    item.Accept.Sequence,
                    cancellationToken,
                    acceptOnlyCandidates)
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

        // Index rebuild cleared per-segment accounting bits. Abandoned destinations are
        // already in DeadBytes. A later closed-segment scan replaces DeadBytes from one
        // complete proof, so those destinations are included once rather than added again.

        // Durable recovery finished: re-link any still-incomplete work into the transient queue.
        EnqueueIncompleteFromJournal();
    }

    /// <summary>
    /// One physical walk for Accept-only sequences that still need orphan discovery.
    /// Sequences with <c>PhysicalWritten</c>, and sequences this process can prove have
    /// never appended, are omitted. Returns null when there is nothing to scan.
    /// The snapshot is taken inside the walk, before this method returns and before
    /// any recovery reappend.
    /// </summary>
    private Dictionary<ulong, List<StoredArticleLocation>>? CollectAcceptOnlyRecoveryCandidates(
        IReadOnlyList<JournalIncompleteSequence> incomplete)
    {
        var accepts = new List<FileSegmentStore.AcceptOnlyMatchTarget>();
        foreach (var item in incomplete)
        {
            if (item.PhysicalWritten is not null || CanSkipProvenLocationScan(item.Accept))
            {
                continue;
            }

            var accept = item.Accept;
            accepts.Add(new FileSegmentStore.AcceptOnlyMatchTarget(
                accept.Sequence,
                accept.ArtId,
                accept.ArtHash,
                accept.ArtSize,
                accept.ArtData));
        }

        if (accepts.Count == 0)
        {
            _segments.DiscardActiveRepairPrefix();
            return null;
        }

        return _segments.FindAcceptOnlyCandidates(accepts);
    }

    /// <summary>
    /// Replaces DeadBytes on each Closed segment from one complete physical proof plus the
    /// durable index. Not part of storage-engine readiness. Skips Active and Retired.
    /// The proof reads a private stream and does not hold the segment write gate. The
    /// catalogue commit still requires the segment to be Closed and the current index to
    /// balance. A segment that cannot be proved, or that changed before commit, is left
    /// at its index-derived DeadBytes. A second successful scan does not add the same orphan again.
    /// </summary>
    internal void CompleteUnreferencedExtentAccounting()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        foreach (var info in Catalogue.Snapshot())
        {
            if (info.State != SegmentState.Closed || info.ExtentAccountingComplete)
            {
                continue;
            }

            if (!_segments.TryReadClosedProvedExtents(info.SegmentId, out var extents))
            {
                FileArticleStorageEngineLogMessages.ClosedExtentAccountingIncomplete(
                    _logger,
                    info.SegmentId.Value);
                continue;
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            var proved = extents;
            Catalogue.ExecuteLocked(() => CommitClosedExtentAccounting(info.SegmentId, proved));
        }
    }

    /// <summary>
    /// Caller holds the catalogue lock. Classification, DeadBytes replacement, and the
    /// accounted bit are published before that lock is released.
    /// </summary>
    private void CommitClosedExtentAccounting(SegmentId segmentId, List<ProvenSegmentExtent> proved)
    {
        TestHookDuringClosedAccountingCommit?.Invoke();
        if (!Catalogue.TryGet(segmentId, out var current)
            || current.State != SegmentState.Closed
            || current.ExtentAccountingComplete)
        {
            return;
        }

        long indexLive = 0;
        long indexDead = 0;
        var named = new HashSet<StoredArticleLocation>();
        foreach (var row in _index.Snapshot())
        {
            if (row.Location.SegmentId != current.SegmentId)
            {
                continue;
            }

            named.Add(row.Location);
            if (row.State == ArticleStorageState.Present)
            {
                indexLive += row.Location.Length;
            }
            else if (row.State is ArticleStorageState.Evicted or ArticleStorageState.Invalid)
            {
                indexDead += row.Location.Length;
            }
        }

        if (current.LiveBytes != indexLive)
        {
            FileArticleStorageEngineLogMessages.ClosedExtentAccountingIncomplete(
                _logger,
                current.SegmentId.Value);
            return;
        }

        long orphan = 0;
        var orphans = new List<ProvenSegmentExtent>();
        foreach (var extent in proved)
        {
            if (named.Contains(extent.Location))
            {
                continue;
            }

            orphan += extent.Location.Length;
            orphans.Add(extent);
        }

        if (!Catalogue.TryCommitClosedExtentAccounting(current.SegmentId, indexDead + orphan))
        {
            FileArticleStorageEngineLogMessages.ClosedExtentAccountingIncomplete(
                _logger,
                current.SegmentId.Value);
            return;
        }

        foreach (var extent in orphans)
        {
            FileArticleStorageEngineLogMessages.UnreferencedRecordMarkedDead(
                _logger,
                extent.ArtId.ToString() ?? string.Empty,
                extent.Location.SegmentId.Value,
                extent.Location.Offset,
                extent.Location.Length);
        }
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
                _ = AppendReservedCompactionJournalFrameAsync(
                        compaction.Begin.CompactionId,
                        CompactionJournalFrameKind.Committed,
                        relocationId: 0,
                        ArticleJournalFrameCodec.CompactionCommittedFrameLength,
                        ct => _journal.AppendCompactionCommittedAsync(
                            new JournalCompactionCommittedRecord(1, compaction.Begin.CompactionId),
                            ct),
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
            var outcome = RelocatePresentFrame(
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
    /// Checkpoints <c>article.index</c> through <see cref="FileArticleIndex.Checkpoint"/>.
    /// </summary>
    /// <remarks>
    /// Returns the frame-history bytes covered by the installed snapshot. A capacity denial
    /// returns <c>0</c> and leaves the index and its frame reservations unchanged. Any other
    /// failure propagates after the index has kept its previous authoritative file unless the
    /// existing checkpoint implementation has already installed a replacement.
    /// </remarks>
    /// <returns>Frame payload retired into the snapshot, or <c>0</c> when nothing was retired.</returns>
    public long CheckpointIndex()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            return _index.Checkpoint().RetiredPhysicalBytes;
        }
        catch (CheckpointCapacityDeniedException)
        {
            return 0;
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
            var released = _journal.CheckpointTruncateCommittedReporting(
                out var omittedSequences,
                out var omittedCompactions);
            ReleaseRetiredJournalSequences(omittedSequences);
            ReleaseRetiredCompactionJournals(omittedCompactions);
            FileArticleStorageEngineLogMessages.CheckpointCompleted(_logger, released);
            return released;
        }
        catch (CheckpointCapacityDeniedException)
        {
            return 0;
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

        // Cache is non-authoritative. A hit is usable only when the index still
        // publishes that exact identity as Present. Anything else is dropped and
        // the index path decides readability.
        if (_articleCache.TryGet(artId, out var cached))
        {
            var cacheCoherent = _index.TryGet(artId, out var cachedIndexMeta)
                && cachedIndexMeta.State == ArticleStorageState.Present
                && cachedIndexMeta.ArtHash == cached.ArtHash
                && cachedIndexMeta.ArtSize == cached.ArtSize;
            if (!cacheCoherent)
            {
                BestEffortCacheRemove(artId);
            }
            else
            {
                result = new ArticleReadResult(
                    new StoredArticleMetadata(
                        cached.ArtId,
                        cached.ArtHash,
                        cached.ArtSize,
                        cachedIndexMeta.Location,
                        ArticleStorageState.Present,
                        _timeProvider.GetUtcNow(),
                        cachedIndexMeta.Sequence),
                    cached.ArtData);
                return true;
            }
        }

        if (!_index.TryGet(artId, out var metadata) || metadata.State != ArticleStorageState.Present)
        {
            return false;
        }

        var hook = TestHookAfterIndexSnapshotBeforeSegmentRead;
        TestHookAfterIndexSnapshotBeforeSegmentRead = null;
        hook?.Invoke(artId, metadata.Location);

        return TryReadIndexedLocation(in metadata, allowOneRelocationRetry: true, out result);
    }

    /// <summary>
    /// Reads <paramref name="snapshot"/> without holding the index lock across segment IO.
    /// A failed read invalidates only when the index still names that same location and identity.
    /// A newer location is attempted at most once and is not invalidated by the stale failure.
    /// </summary>
    private bool TryReadIndexedLocation(
        in StoredArticleMetadata snapshot,
        bool allowOneRelocationRetry,
        out ArticleReadResult result)
    {
        result = default;
        var failProvenRead = TestFailNextIndexedProvenReads > 0;
        if (failProvenRead)
        {
            TestFailNextIndexedProvenReads--;
        }

        if (failProvenRead
            || !_segments.TryReadProven(
                snapshot.Location,
                snapshot.ArtId,
                snapshot.ArtHash,
                snapshot.ArtSize,
                out var artData)
            || !ArticleStorageIntegrity.TryProve(
                artData.Span,
                snapshot.ArtId,
                snapshot.ArtHash,
                snapshot.ArtSize))
        {
            if (!_index.TryGet(snapshot.ArtId, out var current)
                || current.State != ArticleStorageState.Present)
            {
                return false;
            }

            var stillCurrent = LocationsEqual(current.Location, snapshot.Location)
                && current.ArtHash == snapshot.ArtHash
                && current.ArtSize == snapshot.ArtSize;
            if (!stillCurrent)
            {
                if (allowOneRelocationRetry)
                {
                    return TryReadIndexedLocation(in current, allowOneRelocationRetry: false, out result);
                }

                return false;
            }

            _ = TryInvalidatePresentAt(in snapshot);
            return false;
        }

        var durableBefore = _index.DurableWriteCount;
        _index.TouchHint(snapshot.ArtId, _timeProvider.GetUtcNow());
        if (_index.DurableWriteCount != durableBefore)
        {
            throw new InvalidOperationException("TouchHint must not perform durable index writes.");
        }

        _ = _index.TryGet(snapshot.ArtId, out var published);
        result = new ArticleReadResult(published, artData);

        // Best-effort populate; Put rejection must not fail the durable read.
        if (TryCreateCacheRecord(in published, artData, out var cacheRecord))
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

    private async Task RecoverOneAsync(
        JournalIncompleteSequence incomplete,
        CancellationToken cancellationToken,
        IReadOnlyList<StoredArticleLocation>? acceptOnlyCandidates = null)
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
            // Durable PhysicalWritten does not release the segment-copy reservation.
            await CompleteFromPhysicalWrittenAsync(accept, written, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Accept-only: adopt one proven physical copy when the bytes match this Accept.
        // Otherwise append. Unproven records are never published.
        // A sequence journaled in this process that has never attempted a physical append
        // has no orphan to adopt. Replayed, retried-after-write, Evicted, and Invalid
        // sequences still search.
        FileArticleStorageEngineLogMessages.RecoverAcceptOnly(
            _logger,
            accept.Sequence,
            accept.ArtId.ToString() ?? string.Empty);
        // A non-null list is the startup walk's immutable candidates, including an empty
        // list when that walk proved there is no copy. Null means this call must decide.
        IReadOnlyList<StoredArticleLocation> proven = acceptOnlyCandidates
            ?? (CanSkipProvenLocationScan(accept)
                ? []
                : _segments.FindProvenLocations(
                    accept.ArtId,
                    accept.ArtHash,
                    accept.ArtSize,
                    accept.ArtData.Span));
        StoredArticleLocation location;
        if (proven.Count > 0 && TryChooseProvenLocation(accept, proven, out var chosen))
        {
            if (TryRegisterPrePhysicalWritten(accept.Sequence, chosen))
            {
                location = chosen;
                FileArticleStorageEngineLogMessages.AcceptOrphanAdopted(
                    _logger,
                    accept.Sequence,
                    location.SegmentId.Value,
                    location.Offset,
                    location.Length,
                    proven.Count);
            }
            else
            {
                ClearPrePhysicalWritten(accept.Sequence);
                location = await AppendAcceptLocationForPhysicalWrittenAsync(accept, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            location = await AppendAcceptLocationForPhysicalWrittenAsync(accept, cancellationToken)
                .ConfigureAwait(false);
        }
        var physicalWrittenDurable = false;
        try
        {
            var pwCandidate = new JournalPhysicalWrittenRecord(1, accept.Sequence, location);
            ThrowIfTestFault(PersistFaultPoint.BeforePhysicalWritten, accept.Sequence);
            var pwOutcome = await _journal
                .AppendPhysicalWrittenAsync(pwCandidate, cancellationToken)
                .ConfigureAwait(false);

            switch (pwOutcome)
            {
                case JournalAppendOutcome.Applied:
                case JournalAppendOutcome.IdempotentNoOp:
                    // Journal location is now authoritative; drop the process-local marker first.
                    ClearPrePhysicalWritten(accept.Sequence);
                    physicalWrittenDurable = true;
                    ThrowIfTestFault(PersistFaultPoint.AfterPhysicalWritten, accept.Sequence);
                    await CompleteFromPhysicalWrittenAsync(accept, pwCandidate, cancellationToken)
                        .ConfigureAwait(false);
                    MarkUnpublishedProvenCopiesDead(accept.ArtId, proven);
                    return;

                case JournalAppendOutcome.Conflict:
                    // Existing durable PW at a different location — never supersede; complete via it.
                    ClearPrePhysicalWritten(accept.Sequence);
                    physicalWrittenDurable = true;
                    if (!TryGetDurablePhysicalWritten(accept.Sequence, out var existingPw))
                    {
                        throw new InvalidOperationException(
                            $"PhysicalWritten conflict for sequence {accept.Sequence} " +
                            "but no durable PhysicalWritten was found.");
                    }

                    await CompleteFromPhysicalWrittenAsync(accept, existingPw, cancellationToken)
                        .ConfigureAwait(false);
                    MarkUnpublishedProvenCopiesDead(accept.ArtId, proven);
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
        catch
        {
            if (!physicalWrittenDurable)
            {
                ClearPrePhysicalWritten(accept.Sequence);
            }

            throw;
        }
    }

    /// <summary>
    /// Appends the Present frame for <paramref name="sequence"/> after its index-frame reservation is held.
    /// Returns <see langword="false"/> when the index rejects the frame and nothing was written.
    /// </summary>
    private bool PublishPresentFrame(ulong sequence, in StoredArticleMetadata metadata)
    {
        var frameBytes = (long)ArticleIndexRecordCodec.RecordLength;
        if (_capacityAdmissionEnabled && !EnsureUnboundIndexReservation(sequence, frameBytes))
        {
            throw new PersistCompletionDeferredException(
                $"Cannot reserve the {frameBytes}-byte index Present frame for sequence {sequence}.");
        }

        var lengthBefore = _index.DurableLength;
        try
        {
            var append = _index.TryCommitPresentReporting(metadata, out var frameOffset);
            switch (append)
            {
                case DurableIndexAppend.Appended:
                    BindIndexFrameFromSequence(sequence, metadata.ArtId, frameOffset);
                    return true;
                case DurableIndexAppend.Unchanged:
                    ReleaseUnboundIndexReservation(sequence);
                    return true;
                default:
                    ReleaseUnboundIndexReservation(sequence);
                    return false;
            }
        }
        catch
        {
            if (_index.DurableLength >= lengthBefore + frameBytes)
            {
                BindIndexFrameFromSequence(sequence, metadata.ArtId, lengthBefore);
            }
            else
            {
                ReleaseUnboundIndexReservation(sequence);
            }

            throw;
        }
    }

    private ArticleRelocateOutcome RelocatePresentFrame(
        ArticleId artId,
        in StoredArticleLocation expectedLocation,
        in StoredArticleLocation newLocation,
        ulong artHash,
        int artSize)
    {
        var frameBytes = (long)ArticleIndexRecordCodec.RecordLength;
        var reserved = false;
        if (_capacityAdmissionEnabled)
        {
            if (!TryReserveDirectIndexFrame(
                    artId,
                    frameBytes,
                    _capacityMaximumUtilization + _capacityCompactionHeadroom))
            {
                throw new PersistCompletionDeferredException(
                    $"Cannot reserve the {frameBytes}-byte index Present frame for {artId}.");
            }

            reserved = true;
        }

        var lengthBefore = _index.DurableLength;
        try
        {
            var outcome = _index.TryRelocateReporting(
                artId,
                expectedLocation,
                newLocation,
                artHash,
                artSize,
                out var frameOffset);
            if (!reserved)
            {
                return outcome;
            }

            if (outcome == ArticleRelocateOutcome.Relocated)
            {
                BindDirectIndexFrame(artId, frameOffset, frameBytes);
            }
            else
            {
                RollbackDirectIndexFrame(frameBytes);
            }

            return outcome;
        }
        catch
        {
            if (reserved)
            {
                FinishDirectIndexFrameAfterThrow(artId, lengthBefore, frameBytes);
            }

            throw;
        }
    }

    private bool EnsureUnboundIndexReservation(ulong sequence, long frameBytes)
    {
        var control = RequireControlVolume();
        if (control.WithLedger(ledger => ledger.HasUnboundIndex(sequence)))
        {
            return true;
        }

        var added = false;
        var decision = Admit(
            control,
            frameBytes,
            _capacityMaximumUtilization,
            ledger => ledger.HasUnboundIndex(sequence),
            ledger =>
            {
                ledger.TentativeAddIndex(frameBytes);
                ledger.BindIndexUnbound(sequence, frameBytes);
                added = true;
            });
        return decision.AlreadySatisfied || added;
    }

    private bool TryReserveDirectIndexFrame(ArticleId artId, long frameBytes, double ceilingUtilization)
    {
        var control = RequireControlVolume();
        var decision = Admit(
            control,
            frameBytes,
            ceilingUtilization,
            static _ => false,
            ledger => ledger.TentativeAddDirectIndex(frameBytes));
        if (decision.Admitted)
        {
            return true;
        }

        FileArticleStorageEngineLogMessages.RejectedCapacity(
            _logger,
            artId.ToString() ?? string.Empty,
            frameBytes,
            decision.Snapshot.UsedBytes,
            decision.ArticleReservedBytes,
            decision.CompactionReservedBytes,
            decision.CheckpointReservedBytes,
            decision.Snapshot.TotalBytes,
            decision.Snapshot.AvailableBytes,
            ceilingUtilization,
            _capacityCompactionHeadroom);
        return false;
    }

    private void BindIndexFrameFromSequence(ulong sequence, ArticleId artId, long frameOffset)
    {
        if (!_capacityAdmissionEnabled)
        {
            return;
        }

        RequireControlVolume().WithLedger(ledger =>
        {
            if (ledger.HasUnboundIndex(sequence))
            {
                ledger.BindIndexFrameFromSequence(sequence, artId, frameOffset);
            }

            return 0;
        });
    }

    private void BindDirectIndexFrame(ArticleId artId, long frameOffset, long frameBytes)
    {
        RequireControlVolume().WithLedger(ledger =>
        {
            ledger.BindDirectIndexFrame(artId, frameOffset, frameBytes);
            return 0;
        });
    }

    private void RollbackDirectIndexFrame(long frameBytes)
    {
        RequireControlVolume().WithLedger(ledger =>
        {
            ledger.RollbackDirectIndex(frameBytes);
            return 0;
        });
    }

    private void FinishDirectIndexFrameAfterThrow(ArticleId artId, long lengthBefore, long frameBytes)
    {
        if (_index.DurableLength >= lengthBefore + frameBytes)
        {
            BindDirectIndexFrame(artId, lengthBefore, frameBytes);
            return;
        }

        RollbackDirectIndexFrame(frameBytes);
    }

    private void ReleaseUnboundIndexReservation(ulong sequence)
    {
        if (!_capacityAdmissionEnabled)
        {
            return;
        }

        RequireControlVolume().WithLedger(ledger =>
        {
            _ = ledger.ReleaseUnboundIndex(sequence);
            return 0;
        });
    }

    /// <summary>
    /// Reserves one segment-volume copy of <c>ArtSize + 56</c> before a physical append
    /// when no unwritten copy reservation is already held.
    /// Returns true when this call added a reservation.
    /// Adoption must not call this. A failed reservation throws
    /// <see cref="PersistCompletionDeferredException"/> and does not append.
    /// Phase 2D recovery re-append reuses this helper.
    /// </summary>
    private bool ReserveSegmentCopyBeforeAppend(JournalAcceptRecord accept)
    {
        if (!_capacityAdmissionEnabled)
        {
            return false;
        }

        var volume = RequireSegmentVolume();
        var requiredBytes = SegmentRecordCodec.RecordLengthForArtSize(accept.ArtSize);
        lock (_gate)
        {
            if (volume.WithLedger(ledger => ledger.HasUnwrittenSegmentCopy(accept.Sequence)))
            {
                return false;
            }

            var added = false;
            var decision = Admit(
                volume,
                requiredBytes,
                _capacityMaximumUtilization,
                ledger => ledger.HasUnwrittenSegmentCopy(accept.Sequence),
                ledger =>
                {
                    if (ledger.HoldsArticle(accept.Sequence))
                    {
                        ledger.AddSegmentCopy(accept.Sequence, requiredBytes);
                    }
                    else
                    {
                        ledger.TentativeAdd(requiredBytes);
                        ledger.BindSequence(accept.Sequence, requiredBytes);
                    }

                    added = true;
                });
            if (decision.AlreadySatisfied)
            {
                return false;
            }

            if (!decision.Admitted)
            {
                throw new PersistCompletionDeferredException(
                    $"Accept sequence {accept.Sequence} cannot reserve {requiredBytes} bytes until capacity is free.");
            }

            return added;
        }
    }

    private void NoteSegmentCopyWritten(ulong sequence)
    {
        if (!_capacityAdmissionEnabled)
        {
            return;
        }

        var volume = RequireSegmentVolume();
        lock (_gate)
        {
            _ = volume.WithLedger(ledger =>
            {
                ledger.NoteSegmentCopyWritten(sequence);
                return true;
            });
        }
    }

    private void ReleaseUnwrittenSegmentCopy(ulong sequence)
    {
        if (!_capacityAdmissionEnabled)
        {
            return;
        }

        lock (_gate)
        {
            _ = RequireSegmentVolume().WithLedger(ledger => ledger.ReleaseUnwrittenSegmentCopy(sequence));
        }
    }

    /// <summary>Releases segment-copy reservations that were never physically written.</summary>
    private void ReleaseUnwrittenSegmentCopies(ulong sequence)
    {
        if (!_capacityAdmissionEnabled)
        {
            return;
        }

        lock (_gate)
        {
            _ = RequireSegmentVolume().WithLedger(ledger =>
            {
                while (ledger.ReleaseUnwrittenSegmentCopy(sequence))
                {
                }

                return true;
            });
        }
    }

    /// <summary>
    /// Prefers the published index location when it is one of the proven copies.
    /// Skips an Evicted or Invalid location so a later Accept is not closed as still dead.
    /// Otherwise the earliest segment id and offset. Returns false when every proven
    /// copy is that dead location; the caller appends a new record.
    /// </summary>
    private bool TryChooseProvenLocation(
        JournalAcceptRecord accept,
        IReadOnlyList<StoredArticleLocation> proven,
        out StoredArticleLocation chosen)
    {
        chosen = default;
        var skipDead = false;
        StoredArticleLocation deadLocation = default;
        if (_index.TryGet(accept.ArtId, out var existing))
        {
            if (existing.State == ArticleStorageState.Present)
            {
                foreach (var candidate in proven)
                {
                    if (LocationsEqual(candidate, existing.Location))
                    {
                        chosen = candidate;
                        return true;
                    }
                }
            }
            else if (existing.State is ArticleStorageState.Evicted or ArticleStorageState.Invalid)
            {
                skipDead = true;
                deadLocation = existing.Location;
            }
        }

        foreach (var candidate in proven)
        {
            if (skipDead && LocationsEqual(candidate, deadLocation))
            {
                continue;
            }

            chosen = candidate;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Counts proven copies the index does not publish as dead. Does not change the index.
    /// </summary>
    private void MarkUnpublishedProvenCopiesDead(
        ArticleId artId,
        IReadOnlyList<StoredArticleLocation> copies)
    {
        StoredArticleLocation? published = null;
        if (_index.TryGet(artId, out var metadata))
        {
            published = metadata.Location;
        }

        foreach (var copy in copies)
        {
            if (published is { } location && LocationsEqual(copy, location))
            {
                continue;
            }

            try
            {
                // This copy was counted live at append. Move it to dead.
                // A later index rebuild clears the delta. A closed-segment scan replaces
                // DeadBytes from one complete proof instead of adding the copy again.
                Catalogue.ApplyLiveDeadDelta(
                    copy.SegmentId,
                    liveDelta: -copy.Length,
                    deadDelta: copy.Length);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    /// <summary>
    /// Appends Accept bytes and registers the location until PhysicalWritten is durable.
    /// A location sealed for rename is not frozen; Option 1 appends again on the active segment.
    /// </summary>
    private async Task<StoredArticleLocation> AppendAcceptLocationForPhysicalWrittenAsync(
        JournalAcceptRecord accept,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxPrePhysicalWrittenAppendAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var addedReservation = ReserveSegmentCopyBeforeAppend(accept);
            StoredArticleLocation location;
            var appended = false;
            try
            {
                location = await AppendPhysicalAsync(accept.ArtData, cancellationToken).ConfigureAwait(false);
                appended = true;

                // The record is in the segment. A later PhysicalWritten failure must rediscover it.
                RemoveAcceptWithoutPhysicalBytes(accept.Sequence);
                NoteSegmentCopyWritten(accept.Sequence);
            }
            catch (Exception ex)
            {
                var createdPending = ex is UnreconciledDurableTailException { CreatedByThisCall: true };
                if (createdPending)
                {
                    // This call left a pending record or an unreconciled tail. Bytes may exist.
                    RemoveAcceptWithoutPhysicalBytes(accept.Sequence);
                }

                if (addedReservation && !appended && !createdPending)
                {
                    ReleaseUnwrittenSegmentCopy(accept.Sequence);
                }

                throw;
            }

            ThrowIfTestFault(PersistFaultPoint.AfterSataAppend, accept.Sequence);

            var rewrite = TestRewritePhysicalLocationAfterAppend;
            TestRewritePhysicalLocationAfterAppend = null;
            if (rewrite is not null)
            {
                location = rewrite(location);
            }

            var sealRewrite = TestRewriteSealedPhysicalLocation;
            if (sealRewrite is not null)
            {
                var rewritten = sealRewrite(location);
                if (rewritten is { } next)
                {
                    location = next;
                }
                else
                {
                    TestRewriteSealedPhysicalLocation = null;
                }
            }

            if (!TryRegisterPrePhysicalWritten(accept.Sequence, location))
            {
                continue;
            }

            try
            {
                var afterSataHook = TestHookAfterSataBeforePhysicalWritten;
                TestHookAfterSataBeforePhysicalWritten = null;
                afterSataHook?.Invoke(accept.Sequence, location);
                return location;
            }
            catch
            {
                ClearPrePhysicalWritten(accept.Sequence);
                throw;
            }
        }

        throw new IOException(
            $"Accept sequence {accept.Sequence} could not register a pre-PhysicalWritten location.");
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

        if (ShouldFinishPhysicalWrittenWithoutPublishing(accept, written.Location))
        {
            await AppendIndexCommittedAsync(accept, cancellationToken).ConfigureAwait(false);
            return;
        }

        var segmentId = written.Location.SegmentId.Value;
        if (!TryEnterIndexPublication(segmentId))
        {
            throw new IOException(
                $"Index publication for sequence {accept.Sequence} lost the retirement fence.");
        }

        try
        {
            var publicationHook = TestHookAfterPublicationEnteredBeforeIndexCommit;
            TestHookAfterPublicationEnteredBeforeIndexCommit = null;
            publicationHook?.Invoke(written.Location.SegmentId);

            if (ShouldFinishPhysicalWrittenWithoutPublishing(accept, written.Location))
            {
                // Death or a move landed before the index write. Do not publish over it.
            }
            else
            {
                var metadata = new StoredArticleMetadata(
                    accept.ArtId,
                    accept.ArtHash,
                    accept.ArtSize,
                    written.Location,
                    ArticleStorageState.Present,
                    _timeProvider.GetUtcNow(),
                    accept.Sequence);
                if (!PublishPresentFrame(accept.Sequence, in metadata)
                    && !ShouldFinishPhysicalWrittenWithoutPublishing(accept, written.Location))
                {
                    throw new InvalidOperationException(
                        $"Index commit failed for sequence {accept.Sequence} during recovery.");
                }
            }
        }
        finally
        {
            ExitIndexPublication(segmentId);
        }

        ThrowIfTestFault(PersistFaultPoint.AfterIndexCommit, accept.Sequence);
        await AppendIndexCommittedAsync(accept, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// True when <c>IndexCommitted</c> must close the sequence without <c>TryCommitPresent</c>.
    /// </summary>
    /// <remarks>
    /// <c>IndexCommitted</c> completes the journal obligation. It does not entitle the
    /// <c>PhysicalWritten</c> location to become the current logical state.
    /// The index row <see cref="StoredArticleMetadata.Sequence"/> is that logical transaction.
    /// An equal sequence already established the row, including after relocation or death, so
    /// recovery must not replace it. A greater index sequence is a newer transaction; this older
    /// <c>PhysicalWritten</c> must not move the index backwards. A smaller index sequence is an
    /// older transaction, and this Accept may publish under the existing identity rules.
    /// </remarks>
    private bool ShouldFinishPhysicalWrittenWithoutPublishing(
        JournalAcceptRecord accept,
        in StoredArticleLocation location)
    {
        if (!_index.TryGet(accept.ArtId, out var existing))
        {
            return false;
        }

        if (existing.Sequence >= accept.Sequence)
        {
            return true;
        }

        if (existing.ArtHash != accept.ArtHash || existing.ArtSize != accept.ArtSize)
        {
            return false;
        }

        return existing.State == ArticleStorageState.Present;
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
        if (!_index.TryGet(accept.ArtId, out var indexed)
            || indexed.State != ArticleStorageState.Present
            || indexed.ArtHash != accept.ArtHash
            || indexed.ArtSize != accept.ArtSize)
        {
            return;
        }

        var metadata = new StoredArticleMetadata(
            indexed.ArtId,
            indexed.ArtHash,
            indexed.ArtSize,
            indexed.Location,
            ArticleStorageState.Present,
            _timeProvider.GetUtcNow(),
            indexed.Sequence);
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

    /// <summary>
    /// Invalidates <paramref name="expected"/> only if the index still names that location and
    /// identity in the same critical section as the transition. Accounting uses the matched entry.
    /// </summary>
    private bool TryInvalidatePresentAt(in StoredArticleMetadata expected)
    {
        var hook = TestHookBeforeExpectedInvalidation;
        TestHookBeforeExpectedInvalidation = null;
        hook?.Invoke(expected.ArtId, expected.Location);
        var matched = expected;

        var frameBytes = (long)ArticleIndexRecordCodec.RecordLength;
        var reserved = false;
        long lengthBefore = 0;
        try
        {
            while (true)
            {
                var invalidated = false;
                long frameOffset = -1;
                var reserveBeforeAppend = false;
                var expectedArtId = expected.ArtId;
                var expectedLocation = expected.Location;
                var expectedArtHash = expected.ArtHash;
                var expectedArtSize = expected.ArtSize;
                Catalogue.ExecuteLocked(() =>
                {
                    if (_capacityAdmissionEnabled && !reserved)
                    {
                        reserveBeforeAppend = IndexInvalidateWouldAppend(matched);
                        return;
                    }

                    if (!_index.TryInvalidatePresentAtReporting(
                            expectedArtId,
                            expectedLocation,
                            expectedArtHash,
                            expectedArtSize,
                            _timeProvider.GetUtcNow(),
                            out var transitioned,
                            out frameOffset))
                    {
                        return;
                    }

                    try
                    {
                        Catalogue.ApplyLiveDeadDelta(
                            transitioned.Location.SegmentId,
                            liveDelta: -transitioned.Location.Length,
                            deadDelta: transitioned.Location.Length);
                    }
                    catch (InvalidOperationException)
                    {
                        // Catalogue entry may be absent in edge tests; logical index transition still stands.
                    }

                    invalidated = true;
                });

                if (reserveBeforeAppend)
                {
                    if (!TryReserveDirectIndexFrame(
                            expected.ArtId,
                            frameBytes,
                            _capacityMaximumUtilization))
                    {
                        return false;
                    }

                    reserved = true;
                    lengthBefore = _index.DurableLength;
                    continue;
                }

                if (reserved)
                {
                    if (frameOffset >= 0)
                    {
                        BindDirectIndexFrame(expected.ArtId, frameOffset, frameBytes);
                    }
                    else
                    {
                        RollbackDirectIndexFrame(frameBytes);
                    }

                    reserved = false;
                }

                if (invalidated)
                {
                    BestEffortCacheRemove(expected.ArtId);
                }

                return invalidated;
            }
        }
        catch
        {
            if (reserved)
            {
                FinishDirectIndexFrameAfterThrow(expected.ArtId, lengthBefore, frameBytes);
            }

            throw;
        }
    }

    private bool IndexInvalidateWouldAppend(in StoredArticleMetadata expected) =>
        _index.TryGet(expected.ArtId, out var existing)
        && existing.State == ArticleStorageState.Present
        && existing.ArtHash == expected.ArtHash
        && existing.ArtSize == expected.ArtSize
        && LocationsEqual(existing.Location, expected.Location);

    private bool TransitionLogicalDeath(ArticleId artId, ArticleStorageState state)
    {
        var frameBytes = (long)ArticleIndexRecordCodec.RecordLength;
        var reserved = false;
        long lengthBefore = 0;
        try
        {
            while (true)
            {
                var changed = false;
                long frameOffset = -1;
                var reserveBeforeAppend = false;
                Catalogue.ExecuteLocked(() =>
                {
                    if (_capacityAdmissionEnabled && !reserved && IndexDeathWouldAppend(artId))
                    {
                        reserveBeforeAppend = true;
                        return;
                    }

                    changed = TransitionLogicalDeathUnlocked(artId, state, out frameOffset);
                });

                if (reserveBeforeAppend)
                {
                    if (!TryReserveDirectIndexFrame(artId, frameBytes, _capacityMaximumUtilization))
                    {
                        if (_index.TryGet(artId, out var current) && current.State == state)
                        {
                            BestEffortCacheRemove(artId);
                            return true;
                        }

                        return false;
                    }

                    reserved = true;
                    lengthBefore = _index.DurableLength;
                    continue;
                }

                if (reserved)
                {
                    if (frameOffset >= 0)
                    {
                        BindDirectIndexFrame(artId, frameOffset, frameBytes);
                    }
                    else
                    {
                        RollbackDirectIndexFrame(frameBytes);
                    }

                    reserved = false;
                }

                return changed;
            }
        }
        catch
        {
            if (reserved)
            {
                FinishDirectIndexFrameAfterThrow(artId, lengthBefore, frameBytes);
            }

            throw;
        }
    }

    private bool IndexDeathWouldAppend(ArticleId artId) =>
        _index.TryGet(artId, out var existing) && existing.State == ArticleStorageState.Present;

    private bool TransitionLogicalDeathUnlocked(ArticleId artId, ArticleStorageState state, out long frameOffset)
    {
        frameOffset = -1;
        if (!_index.TryGet(artId, out var snapshot))
        {
            return false;
        }

        if (snapshot.State == state)
        {
            BestEffortCacheRemove(artId);
            return true;
        }

        if (snapshot.State != ArticleStorageState.Present)
        {
            return false;
        }

        if (TestFailNextLogicalDeath)
        {
            TestFailNextLogicalDeath = false;
            return false;
        }

        // Only the caller that actually changes Present applies the live→dead delta.
        // A second evict observes the terminal state and must not count the bytes again.
        if (!_index.TryTransitionPresentOnce(
                artId,
                state,
                _timeProvider.GetUtcNow(),
                out var transitioned,
                out frameOffset))
        {
            frameOffset = -1;
            if (_index.TryGet(artId, out snapshot) && snapshot.State == state)
            {
                BestEffortCacheRemove(artId);
                return true;
            }

            return false;
        }

        try
        {
            Catalogue.ApplyLiveDeadDelta(
                transitioned.Location.SegmentId,
                liveDelta: -transitioned.Location.Length,
                deadDelta: transitioned.Location.Length);
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
                            // Keep the incomplete Accept. Release only copies that were not written.
                            ReleaseUnwrittenSegmentCopies(sequence);
                            ReleaseUnboundIndexReservation(sequence);
                            FileArticleStorageEngineLogMessages.PersistNonRetryableFailure(
                                _logger,
                                sequence,
                                ex.GetType().Name,
                                ex.Message);
                            ScheduleBlockedPersistRetry(sequence);
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
