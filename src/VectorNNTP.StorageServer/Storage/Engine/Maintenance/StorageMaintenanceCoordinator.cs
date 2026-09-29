using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;
using VectorNNTP.StorageServer.Storage.Engine.Policy;

namespace VectorNNTP.StorageServer.Storage.Engine.Maintenance;

/// <summary>
/// Explicitly-invoked storage maintenance coordinator (Phase 5B). Composes
/// <see cref="ArticleSegmentPolicy"/> with frozen compaction/retirement/reclamation primitives.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="RunOnceAsync"/> performs at most one useful maintenance cycle:
/// reclaim one Retired segment, or continue/finish one compaction lifecycle
/// (compact → retire → reclaim). No timers, workers, or hosted-service wiring.
/// </para>
/// <para>
/// Ordering: existing Retired physical reclaim first; then finish CompactionCommitted
/// pending retirement; then continue an open uncommitted compaction; then select a new
/// Closed victim via policy. Under article admission pressure (Phase 5F.2), Closed-victim
/// selection prefers physical recovery potential and compaction-headroom feasibility;
/// pressure is recomputed each invocation and is not a persistent mode.
/// </para>
/// <para>
/// Phase 5F.3: when continuing an open compaction returns
/// <see cref="StorageMaintenanceSkipReasons.CapacityOpenCompactionZeroProgress"/>, that open
/// compaction is not abandoned — it remains durable and resumable — but the same
/// <see cref="RunOnceAsync"/> falls through to Closed-victim selection so a capacity-blocked
/// open compaction cannot starve other Closed work. Multiple open uncommitted compactions are
/// still enumerated by lowest CompactionId; only the selected open is attempted before
/// fall-through (no same-run retry of another open).
/// </para>
/// <para>
/// Policy selection is a hint — every destructive step is revalidated against current
/// catalogue/index state.
/// </para>
/// </remarks>
public sealed class StorageMaintenanceCoordinator
{
    private readonly FileArticleStorageEngine _engine;
    private readonly ArticleSegmentPolicy _policy;

    /// <summary>Creates a coordinator over an open durable engine and a read-only policy.</summary>
    /// <param name="engine">Durable article storage engine.</param>
    /// <param name="policy">Compaction/reclamation victim policy.</param>
    public StorageMaintenanceCoordinator(FileArticleStorageEngine engine, ArticleSegmentPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(policy);
        _engine = engine;
        _policy = policy;
    }

    /// <summary>Policy used by this coordinator (read-only).</summary>
    public ArticleSegmentPolicy Policy => _policy;

    /// <summary>
    /// Invoked after a Closed compaction victim is selected and before revalidation/execution.
    /// Tests only.
    /// </summary>
    internal Action<SegmentId>? TestHookAfterCompactionVictimSelected { get; set; }

    /// <summary>
    /// Invoked after a Retired reclamation victim is selected and before revalidation/execution.
    /// Tests only.
    /// </summary>
    internal Action<SegmentId>? TestHookAfterReclamationVictimSelected { get; set; }

    /// <summary>
    /// Evaluates current durable state and performs at most one maintenance cycle.
    /// </summary>
    public async Task<StorageMaintenanceResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pressure = _engine.ObserveCapacityAdmissionPressure();

        // 1) Physical reclaim of existing Retired garbage (no new compaction).
        if (_policy.TrySelectReclamationVictim(_engine.Catalogue, out var retiredVictim))
        {
            TestHookAfterReclamationVictimSelected?.Invoke(retiredVictim.SegmentId);
            var reclaimed = await TryReclaimRetiredAsync(retiredVictim, cancellationToken)
                .ConfigureAwait(false);
            // Re-observe after reclaim so recovery target reflects reduced UsedBytes when the
            // capacity reader tracks deletions (tests / DriveInfo).
            return AttachPressure(reclaimed, _engine.ObserveCapacityAdmissionPressure());
        }

        // 2) Finish CompactionCommitted → Retire → Reclaim (partial progress).
        if (TryFindCommittedPendingRetirement(out var committed))
        {
            return AttachPressure(
                await FinishCommittedCompactionAsync(committed, cancellationToken)
                    .ConfigureAwait(false),
                pressure);
        }

        // 3) Continue open uncommitted compaction. Capacity zero-progress does not monopolize
        // the remainder of this run (Phase 5F.3 fall-through to Closed selection).
        StorageMaintenanceResult? deferredOpenCapacitySkip = null;
        if (TryFindOpenUncommitted(out var open))
        {
            var openResult = await CompactThenFinishAsync(
                    open.Begin.SourceSegmentId,
                    requirePolicyEligibility: false,
                    sourceAccountingHint: null,
                    continuingOpenCompaction: true,
                    cancellationToken)
                .ConfigureAwait(false);

            if (IsCapacityOpenCompactionZeroProgressSkip(in openResult))
            {
                deferredOpenCapacitySkip = openResult;
            }
            else
            {
                return AttachPressure(openResult, pressure);
            }
        }

        // 4) Select a new Closed compaction victim (pressure-aware when under admission pressure).
        var closedResult = await SelectAndRunClosedVictimAsync(pressure, cancellationToken)
            .ConfigureAwait(false);

        if (deferredOpenCapacitySkip is { } deferred)
        {
            // No Closed work available — preserve the open capacity Skip (Case A).
            if (IsNoClosedWorkOutcome(in closedResult))
            {
                return AttachPressure(deferred, pressure);
            }

            // Closed path produced the primary outcome; record that an open was deferred.
            return AttachPressure(closedResult.WithDeferredOpenCompaction(in deferred), pressure);
        }

        return AttachPressure(closedResult, pressure);
    }

    private async Task<StorageMaintenanceResult> SelectAndRunClosedVictimAsync(
        CapacityAdmissionPressureSnapshot pressure,
        CancellationToken cancellationToken)
    {
        // Closed selection must not re-pick a source that already has an open uncommitted
        // compaction (Phase 5F.3 fall-through would otherwise reselect the capacity-blocked open).
        var closedSnapshot = CatalogueSnapshotExcludingOpenUncommittedSources();

        if (pressure.IsUnderAdmissionPressure)
        {
            // Revalidate pressure immediately before victim selection / execution.
            pressure = _engine.ObserveCapacityAdmissionPressure();
            if (!pressure.IsUnderAdmissionPressure)
            {
                // Pressure cleared between observation and selection — fall through to normal.
            }
            else if (!_policy.TrySelectPressureReliefCompactionVictim(
                         closedSnapshot,
                         in pressure,
                         out var pressureVictim))
            {
                return Skipped(
                    default,
                    compactionId: 0,
                    StorageMaintenanceSkipReasons.CapacityPressureNoFeasibleCandidate);
            }
            else
            {
                // Stale feasibility: re-observe and re-check before SATA work.
                pressure = _engine.ObserveCapacityAdmissionPressure();
                if (!pressure.IsUnderAdmissionPressure)
                {
                    // Fall through to normal selection below.
                }
                else if (!ArticleSegmentPolicy.IsCompactionFeasibleUnderHeadroom(
                             in pressureVictim,
                             in pressure))
                {
                    return Skipped(
                        pressureVictim.SegmentId,
                        compactionId: 0,
                        StorageMaintenanceSkipReasons.CapacityInsufficientHeadroom);
                }
                else
                {
                    TestHookAfterCompactionVictimSelected?.Invoke(pressureVictim.SegmentId);

                    if (!TryRevalidateCompactionCandidate(pressureVictim.SegmentId, out var pressureSkip))
                    {
                        return Skipped(pressureVictim.SegmentId, compactionId: 0, pressureSkip);
                    }

                    return await CompactThenFinishAsync(
                            pressureVictim.SegmentId,
                            requirePolicyEligibility: true,
                            sourceAccountingHint: pressureVictim,
                            continuingOpenCompaction: false,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        if (!_policy.TrySelectCompactionVictim(closedSnapshot, out var closedVictim))
        {
            return NoWork();
        }

        TestHookAfterCompactionVictimSelected?.Invoke(closedVictim.SegmentId);

        if (!TryRevalidateCompactionCandidate(closedVictim.SegmentId, out var skipReason))
        {
            return Skipped(closedVictim.SegmentId, compactionId: 0, skipReason);
        }

        return await CompactThenFinishAsync(
                closedVictim.SegmentId,
                requirePolicyEligibility: true,
                sourceAccountingHint: closedVictim,
                continuingOpenCompaction: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Catalogue snapshot without Closed sources that already have an open uncommitted compaction.
    /// </summary>
    private IReadOnlyList<SegmentInfo> CatalogueSnapshotExcludingOpenUncommittedSources()
    {
        HashSet<ulong>? openSources = null;
        foreach (var entry in _engine.Journal.EnumerateOpenCompactions())
        {
            if (entry.Committed || entry.Retired is not null)
            {
                continue;
            }

            openSources ??= new HashSet<ulong>();
            _ = openSources.Add(entry.Begin.SourceSegmentId.Value);
        }

        var snapshot = _engine.Catalogue.Snapshot();
        if (openSources is null || openSources.Count == 0)
        {
            return snapshot;
        }

        var filtered = new List<SegmentInfo>(snapshot.Count);
        foreach (var entry in snapshot)
        {
            if (!openSources.Contains(entry.SegmentId.Value))
            {
                filtered.Add(entry);
            }
        }

        return filtered;
    }

    private static bool IsCapacityOpenCompactionZeroProgressSkip(in StorageMaintenanceResult result) =>
        result.Outcome == StorageMaintenanceOutcome.Skipped
        && result.SkipReason == StorageMaintenanceSkipReasons.CapacityOpenCompactionZeroProgress;

    /// <summary>
    /// True when Closed selection found nothing useful to attempt (preserve deferred open Skip).
    /// </summary>
    private static bool IsNoClosedWorkOutcome(in StorageMaintenanceResult result) =>
        result.Outcome == StorageMaintenanceOutcome.NoWork
        || (result.Outcome == StorageMaintenanceOutcome.Skipped
            && !result.CompactionAttempted
            && result.SkipReason is StorageMaintenanceSkipReasons.CapacityPressureNoFeasibleCandidate
                or StorageMaintenanceSkipReasons.CapacityInsufficientHeadroom);

    private async Task<StorageMaintenanceResult> TryReclaimRetiredAsync(
        SegmentInfo segment,
        CancellationToken cancellationToken)
    {
        var segmentId = segment.SegmentId;
        if (!TryRevalidateReclamationCandidate(segmentId, out var skipReason))
        {
            return Skipped(segmentId, compactionId: 0, skipReason);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var reclaim = await _engine
            .ReclaimRetiredSegmentAsync(segmentId, cancellationToken)
            .ConfigureAwait(false);

        return MapReclamationOnly(reclaim, segment.SizeBytes);
    }

    private async Task<StorageMaintenanceResult> FinishCommittedCompactionAsync(
        CompactionJournalSnapshot committed,
        CancellationToken cancellationToken)
    {
        var compactionId = committed.Begin.CompactionId;
        var sourceId = committed.Begin.SourceSegmentId;

        if (!TryRevalidateRetirementCandidate(compactionId, sourceId, out var skipReason))
        {
            return Skipped(sourceId, compactionId, skipReason);
        }

        return await RetireThenMaybeReclaimAsync(
                compactionId,
                sourceId,
                compactionAttempted: false,
                compactionCommitted: true,
                compactionExecution: null,
                sourceAccountingHint: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<StorageMaintenanceResult> CompactThenFinishAsync(
        SegmentId sourceSegmentId,
        bool requirePolicyEligibility,
        SegmentInfo? sourceAccountingHint,
        bool continuingOpenCompaction,
        CancellationToken cancellationToken)
    {
        if (requirePolicyEligibility
            && !TryRevalidateCompactionCandidate(sourceSegmentId, out var skipReason))
        {
            return Skipped(sourceSegmentId, compactionId: 0, skipReason);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var compact = await _engine
            .CompactClosedSegmentAsync(sourceSegmentId, cancellationToken)
            .ConfigureAwait(false);

        switch (compact.Outcome)
        {
            case ArticleCompactionOutcome.Committed:
                return await RetireThenMaybeReclaimAsync(
                        compact.CompactionId,
                        compact.SourceSegmentId,
                        compactionAttempted: true,
                        compactionCommitted: true,
                        compact,
                        sourceAccountingHint,
                        cancellationToken)
                    .ConfigureAwait(false);

            case ArticleCompactionOutcome.Incomplete:
                // Capacity policy denial before any successful relocation → Skipped (not Failed).
                // Distinguish open soft-spin from new-compaction headroom denial.
                if (compact.RelocatedCount == 0
                    && compact.AbandonedCount == 0
                    && compact.Reason is not null
                    && compact.Reason.StartsWith("capacity", StringComparison.Ordinal))
                {
                    var capacitySkip = continuingOpenCompaction
                        ? StorageMaintenanceSkipReasons.CapacityOpenCompactionZeroProgress
                        : StorageMaintenanceSkipReasons.CapacityInsufficientHeadroom;
                    return EnrichCompactionResult(
                        Skipped(
                            compact.SourceSegmentId,
                            compact.CompactionId,
                            capacitySkip),
                        compact,
                        sourceAccountingHint);
                }

                return IncompleteFromCompaction(compact, sourceAccountingHint);

            case ArticleCompactionOutcome.RejectedSourceMissing:
            case ArticleCompactionOutcome.RejectedSourceNotClosed:
                return Skipped(
                    compact.SourceSegmentId,
                    compact.CompactionId,
                    compact.Reason ?? compact.Outcome.ToString());

            case ArticleCompactionOutcome.CompetingOpenCompaction:
            case ArticleCompactionOutcome.Failed:
                return FailedFromCompaction(compact, sourceAccountingHint);

            default:
                return FailedFromCompaction(
                    compact with { Reason = "unexpected-compaction-" + compact.Outcome },
                    sourceAccountingHint);
        }
    }

    private async Task<StorageMaintenanceResult> RetireThenMaybeReclaimAsync(
        ulong compactionId,
        SegmentId sourceSegmentId,
        bool compactionAttempted,
        bool compactionCommitted,
        ArticleCompactionResult? compactionExecution,
        SegmentInfo? sourceAccountingHint,
        CancellationToken cancellationToken)
    {
        if (!TryRevalidateRetirementCandidate(compactionId, sourceSegmentId, out var skipReason))
        {
            // Compaction may already be durable; skip retirement without claiming failure of commit.
            return EnrichCompactionResult(
                new StorageMaintenanceResult(
                    compactionCommitted
                        ? StorageMaintenanceOutcome.Compacted
                        : StorageMaintenanceOutcome.Skipped,
                    sourceSegmentId,
                    compactionId,
                    compactionAttempted,
                    compactionCommitted,
                    RetirementAttempted: false,
                    Retired: false,
                    ReclamationAttempted: false,
                    Reclaimed: false,
                    SkipReason: skipReason),
                compactionExecution,
                sourceAccountingHint);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var retire = await _engine
            .RetireCompactedSegmentAsync(compactionId, cancellationToken)
            .ConfigureAwait(false);

        switch (retire.Outcome)
        {
            case ArticleSegmentRetirementOutcome.Retired:
            case ArticleSegmentRetirementOutcome.IdempotentNoOp:
                break;

            case ArticleSegmentRetirementOutcome.RejectedPresentRemain:
            case ArticleSegmentRetirementOutcome.RejectedSourceNotClosed:
            case ArticleSegmentRetirementOutcome.RejectedSourceMissing:
            case ArticleSegmentRetirementOutcome.RejectedNotCommitted:
            case ArticleSegmentRetirementOutcome.RejectedUnknownCompaction:
                return EnrichCompactionResult(
                    new StorageMaintenanceResult(
                        StorageMaintenanceOutcome.Compacted,
                        sourceSegmentId,
                        compactionId,
                        compactionAttempted,
                        compactionCommitted,
                        RetirementAttempted: true,
                        Retired: false,
                        ReclamationAttempted: false,
                        Reclaimed: false,
                        SkipReason: retire.Reason ?? retire.Outcome.ToString()),
                    compactionExecution,
                    sourceAccountingHint);

            case ArticleSegmentRetirementOutcome.Failed:
                return EnrichCompactionResult(
                    new StorageMaintenanceResult(
                        StorageMaintenanceOutcome.Failed,
                        sourceSegmentId,
                        compactionId,
                        compactionAttempted,
                        compactionCommitted,
                        RetirementAttempted: true,
                        Retired: false,
                        ReclamationAttempted: false,
                        Reclaimed: false,
                        SkipReason: retire.Reason),
                    compactionExecution,
                    sourceAccountingHint);

            default:
                return EnrichCompactionResult(
                    new StorageMaintenanceResult(
                        StorageMaintenanceOutcome.Failed,
                        sourceSegmentId,
                        compactionId,
                        compactionAttempted,
                        compactionCommitted,
                        RetirementAttempted: true,
                        Retired: false,
                        ReclamationAttempted: false,
                        Reclaimed: false,
                        SkipReason: "unexpected-retirement-" + retire.Outcome),
                    compactionExecution,
                    sourceAccountingHint);
        }

        long? reclaimedSize = sourceAccountingHint is { } hint
            && hint.SegmentId == sourceSegmentId
            ? hint.SizeBytes
            : null;

        if (!TryRevalidateReclamationCandidate(sourceSegmentId, out var reclaimSkip))
        {
            return EnrichCompactionResult(
                new StorageMaintenanceResult(
                    StorageMaintenanceOutcome.Retired,
                    sourceSegmentId,
                    compactionId,
                    compactionAttempted,
                    CompactionCommitted: true,
                    RetirementAttempted: true,
                    Retired: true,
                    ReclamationAttempted: false,
                    Reclaimed: false,
                    SkipReason: reclaimSkip),
                compactionExecution,
                sourceAccountingHint);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var reclaim = await _engine
            .ReclaimRetiredSegmentAsync(sourceSegmentId, cancellationToken)
            .ConfigureAwait(false);

        if (reclaim.Outcome is ArticleSegmentReclamationOutcome.Reclaimed
            or ArticleSegmentReclamationOutcome.IdempotentAlreadyReclaimed)
        {
            return EnrichCompactionResult(
                new StorageMaintenanceResult(
                    StorageMaintenanceOutcome.CompactedAndReclaimed,
                    sourceSegmentId,
                    compactionId,
                    compactionAttempted,
                    CompactionCommitted: true,
                    RetirementAttempted: true,
                    Retired: true,
                    ReclamationAttempted: true,
                    Reclaimed: true,
                    SkipReason: reclaim.Reason,
                    ReclaimedSegmentSizeBytes: reclaimedSize),
                compactionExecution,
                sourceAccountingHint);
        }

        if (reclaim.Outcome is ArticleSegmentReclamationOutcome.RejectedPresentRemain
            or ArticleSegmentReclamationOutcome.RejectedUnexpectedPhysical
            or ArticleSegmentReclamationOutcome.RejectedActive
            or ArticleSegmentReclamationOutcome.RejectedClosed
            or ArticleSegmentReclamationOutcome.RejectedMissing)
        {
            return EnrichCompactionResult(
                new StorageMaintenanceResult(
                    StorageMaintenanceOutcome.Retired,
                    sourceSegmentId,
                    compactionId,
                    compactionAttempted,
                    CompactionCommitted: true,
                    RetirementAttempted: true,
                    Retired: true,
                    ReclamationAttempted: true,
                    Reclaimed: false,
                    SkipReason: reclaim.Reason ?? reclaim.Outcome.ToString()),
                compactionExecution,
                sourceAccountingHint);
        }

        return EnrichCompactionResult(
            new StorageMaintenanceResult(
                StorageMaintenanceOutcome.Failed,
                sourceSegmentId,
                compactionId,
                compactionAttempted,
                CompactionCommitted: true,
                RetirementAttempted: true,
                Retired: true,
                ReclamationAttempted: true,
                Reclaimed: false,
                SkipReason: reclaim.Reason ?? reclaim.Outcome.ToString()),
            compactionExecution,
            sourceAccountingHint);
    }

    private bool TryFindCommittedPendingRetirement(out CompactionJournalSnapshot snapshot)
    {
        snapshot = default;
        CompactionJournalSnapshot? best = null;
        foreach (var entry in _engine.Journal.EnumerateOpenCompactions())
        {
            if (!entry.Committed || entry.Retired is not null)
            {
                continue;
            }

            if (best is null || entry.Begin.CompactionId < best.Value.Begin.CompactionId)
            {
                best = entry;
            }
        }

        if (best is null)
        {
            return false;
        }

        snapshot = best.Value;
        return true;
    }

    private bool TryFindOpenUncommitted(out CompactionJournalSnapshot snapshot)
    {
        snapshot = default;
        CompactionJournalSnapshot? best = null;
        foreach (var entry in _engine.Journal.EnumerateOpenCompactions())
        {
            if (entry.Committed || entry.Retired is not null)
            {
                continue;
            }

            if (best is null || entry.Begin.CompactionId < best.Value.Begin.CompactionId)
            {
                best = entry;
            }
        }

        if (best is null)
        {
            return false;
        }

        snapshot = best.Value;
        return true;
    }

    private bool TryRevalidateCompactionCandidate(SegmentId segmentId, out string? skipReason)
    {
        skipReason = null;
        if (!_engine.Catalogue.TryGet(segmentId, out var info))
        {
            skipReason = "catalogue-missing";
            return false;
        }

        if (info.State == SegmentState.Active)
        {
            skipReason = "segment-active";
            return false;
        }

        if (info.State == SegmentState.Retired)
        {
            skipReason = "segment-retired";
            return false;
        }

        if (info.State != SegmentState.Closed)
        {
            skipReason = "segment-not-closed";
            return false;
        }

        var eligibility = _policy.EvaluateCompaction(in info);
        if (!eligibility.IsEligible)
        {
            skipReason = "policy-" + eligibility.Reason;
            return false;
        }

        return true;
    }

    private bool TryRevalidateRetirementCandidate(
        ulong compactionId,
        SegmentId sourceSegmentId,
        out string? skipReason)
    {
        skipReason = null;
        if (!_engine.Journal.TryGetCompaction(compactionId, out var snap))
        {
            skipReason = "unknown-compaction";
            return false;
        }

        if (!snap.Committed)
        {
            skipReason = "not-committed";
            return false;
        }

        if (snap.Begin.SourceSegmentId.Value != sourceSegmentId.Value)
        {
            skipReason = "source-mismatch";
            return false;
        }

        if (CountPresent(sourceSegmentId) > 0)
        {
            skipReason = "present-remain-on-source";
            return false;
        }

        if (!_engine.Catalogue.TryGet(sourceSegmentId, out var info))
        {
            // Already retired+reclaimed catalogue absence with journal Retired is reclaim path.
            if (snap.Retired is not null)
            {
                skipReason = "source-already-absent";
                return false;
            }

            skipReason = "source-missing";
            return false;
        }

        if (info.State == SegmentState.Retired && snap.Retired is not null)
        {
            // Ready for reclaim, not retirement — caller should not be here for reclaim-only.
            // Allow RetireCompactedSegmentAsync IdempotentNoOp path.
            return true;
        }

        if (info.State != SegmentState.Closed && info.State != SegmentState.Retired)
        {
            skipReason = "source-not-closed";
            return false;
        }

        return true;
    }

    private bool TryRevalidateReclamationCandidate(SegmentId segmentId, out string? skipReason)
    {
        skipReason = null;
        if (!_engine.Catalogue.TryGet(segmentId, out var info))
        {
            skipReason = "catalogue-missing";
            return false;
        }

        if (info.State != SegmentState.Retired)
        {
            skipReason = info.State switch
            {
                SegmentState.Active => "segment-active",
                SegmentState.Closed => "segment-closed",
                _ => "segment-not-retired",
            };
            return false;
        }

        if (CountPresent(segmentId) > 0)
        {
            skipReason = "present-remain-on-source";
            return false;
        }

        var retiredPath = Path.Combine(
            _engine.Segments.RootPath,
            SegmentFileNames.Format(segmentId, SegmentFileKind.Retired));
        var activePath = Path.Combine(
            _engine.Segments.RootPath,
            SegmentFileNames.Format(segmentId, SegmentFileKind.Active));
        var closedPath = Path.Combine(
            _engine.Segments.RootPath,
            SegmentFileNames.Format(segmentId, SegmentFileKind.Closed));

        if (File.Exists(activePath) || File.Exists(closedPath))
        {
            skipReason = "unexpected-active-or-closed-file";
            return false;
        }

        if (!File.Exists(retiredPath))
        {
            // Catalogue Retired but file gone — let reclaim primitive finish catalogue cleanup.
            return true;
        }

        return true;
    }

    private int CountPresent(SegmentId segmentId) =>
        _engine.Index.Snapshot()
            .Count(m => m.State == ArticleStorageState.Present
                        && m.Location.SegmentId.Value == segmentId.Value);

    private static StorageMaintenanceResult MapReclamationOnly(
        ArticleSegmentReclamationResult reclaim,
        long reclaimedSegmentSizeBytes)
    {
        return reclaim.Outcome switch
        {
            ArticleSegmentReclamationOutcome.Reclaimed
                or ArticleSegmentReclamationOutcome.IdempotentAlreadyReclaimed =>
                new StorageMaintenanceResult(
                    StorageMaintenanceOutcome.Reclaimed,
                    reclaim.SegmentId,
                    CompactionId: 0,
                    CompactionAttempted: false,
                    CompactionCommitted: false,
                    RetirementAttempted: false,
                    Retired: false,
                    ReclamationAttempted: true,
                    Reclaimed: true,
                    SkipReason: reclaim.Reason,
                    ReclaimedSegmentSizeBytes: reclaimedSegmentSizeBytes),

            ArticleSegmentReclamationOutcome.RejectedPresentRemain
                or ArticleSegmentReclamationOutcome.RejectedUnexpectedPhysical
                or ArticleSegmentReclamationOutcome.RejectedActive
                or ArticleSegmentReclamationOutcome.RejectedClosed
                or ArticleSegmentReclamationOutcome.RejectedMissing =>
                Skipped(reclaim.SegmentId, compactionId: 0, reclaim.Reason ?? reclaim.Outcome.ToString()),

            _ => new StorageMaintenanceResult(
                StorageMaintenanceOutcome.Failed,
                reclaim.SegmentId,
                CompactionId: 0,
                CompactionAttempted: false,
                CompactionCommitted: false,
                RetirementAttempted: false,
                Retired: false,
                ReclamationAttempted: true,
                Reclaimed: false,
                SkipReason: reclaim.Reason ?? reclaim.Outcome.ToString()),
        };
    }

    private static StorageMaintenanceResult AttachPressure(
        StorageMaintenanceResult result,
        in CapacityAdmissionPressureSnapshot pressure) =>
        result.WithCapacityPressure(in pressure);

    private static StorageMaintenanceResult NoWork() =>
        new(
            StorageMaintenanceOutcome.NoWork,
            default,
            CompactionId: 0,
            CompactionAttempted: false,
            CompactionCommitted: false,
            RetirementAttempted: false,
            Retired: false,
            ReclamationAttempted: false,
            Reclaimed: false);

    private static StorageMaintenanceResult Skipped(SegmentId segmentId, ulong compactionId, string? reason) =>
        new(
            StorageMaintenanceOutcome.Skipped,
            segmentId,
            compactionId,
            CompactionAttempted: false,
            CompactionCommitted: false,
            RetirementAttempted: false,
            Retired: false,
            ReclamationAttempted: false,
            Reclaimed: false,
            SkipReason: reason);

    private static StorageMaintenanceResult IncompleteFromCompaction(
        ArticleCompactionResult compact,
        SegmentInfo? sourceAccountingHint) =>
        EnrichCompactionResult(
            new StorageMaintenanceResult(
                StorageMaintenanceOutcome.Incomplete,
                compact.SourceSegmentId,
                compact.CompactionId,
                CompactionAttempted: true,
                CompactionCommitted: false,
                RetirementAttempted: false,
                Retired: false,
                ReclamationAttempted: false,
                Reclaimed: false,
                SkipReason: compact.Reason),
            compact,
            sourceAccountingHint);

    private static StorageMaintenanceResult FailedFromCompaction(
        ArticleCompactionResult compact,
        SegmentInfo? sourceAccountingHint) =>
        EnrichCompactionResult(
            new StorageMaintenanceResult(
                StorageMaintenanceOutcome.Failed,
                compact.SourceSegmentId,
                compact.CompactionId,
                CompactionAttempted: true,
                CompactionCommitted: false,
                RetirementAttempted: false,
                Retired: false,
                ReclamationAttempted: false,
                Reclaimed: false,
                SkipReason: compact.Reason),
            compact,
            sourceAccountingHint);

    private static StorageMaintenanceResult EnrichCompactionResult(
        StorageMaintenanceResult result,
        ArticleCompactionResult? compactionExecution,
        SegmentInfo? sourceAccountingHint)
    {
        if (sourceAccountingHint is { } hint)
        {
            result = result.WithSourceAccounting(in hint);
        }

        if (compactionExecution is { } compact)
        {
            result = result.WithCompactionExecution(in compact);
        }

        return result;
    }
}
