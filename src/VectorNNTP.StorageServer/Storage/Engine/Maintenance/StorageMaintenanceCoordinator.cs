using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage;
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
/// When <c>MaxRetentionAge</c> is positive, <see cref="RunOnceAsync"/> first expires one bounded
/// batch of age-eligible Present articles. When bulk pressure is at Pressure or higher, a second
/// bounded window may logically evict older Present articles. Both passes read index metadata only.
/// Zero <c>MaxRetentionAge</c> skips only the age pass. Neither pass deletes segment bytes.
/// One <see cref="RunOnceAsync"/> performs at most one useful maintenance cycle:
/// reclaim one Retired segment, reclaim one fully-dead Closed segment, or continue/finish
/// one compaction lifecycle (compact → retire → reclaim). No timers, workers, or hosted-service wiring.
/// </para>
/// <para>
/// Ordering: optional physical-journal checkpoint, optional physical-index checkpoint, then existing Retired physical reclaim; then finish CompactionCommitted
/// pending retirement; then continue an open uncommitted compaction; then delete one Closed
/// segment whose live bytes are already zero; then select a new Closed victim via policy.
/// A selected victim still has Present bytes and meets both
/// <c>MinimumDeadBytes</c> and <c>MinimumDeadRatio</c>. That selection is the low-density
/// rewrite: Present rows are relocated onto the active segment, the index location is
/// published, and the source is retired and reclaimed only after no Present row remains
/// on it. A Closed segment whose dead ratio is below <c>MinimumDeadRatio</c> stays where
/// it is. Live density is the complement of that configured dead ratio; this coordinator
/// does not add a second threshold. Under article admission pressure (Phase 5F.2), Closed-victim
/// selection prefers physical recovery potential and compaction-headroom feasibility;
/// pressure is recomputed each invocation and is not a persistent mode.
/// </para>
/// <para>
/// Phase 5F.3 / 5F.4: when an open compaction returns
/// <see cref="StorageMaintenanceSkipReasons.CapacityOpenCompactionZeroProgress"/>, that open
/// remains durable and resumable but yields for the remainder of this
/// <see cref="RunOnceAsync"/>. The same yield applies when the only block is a publishable
/// PhysicalWritten or pre-PhysicalWritten append. The coordinator then tries the next
/// uncommitted open by ascending CompactionId (each yielded open attempted at most once this
/// run). After all eligible opens have yielded, Closed-victim selection runs (Phase 5F.3).
/// CompetingOpenCompaction and other non-yield outcomes do not rotate. A committed compaction
/// that still has Present entries is relocated on that same id before it can yield.
/// </para>
/// <para>
/// Policy selection is a hint — every destructive step is revalidated against current
/// catalogue/index state.
/// </para>
/// <para>
/// When the physical journal threshold is positive and <c>JournalPhysicalBytes</c> has reached
/// it, the cycle first calls <see cref="FileArticleStorageEngine.CheckpointTruncateCommitted"/>.
/// When the physical index threshold is positive and <c>IndexPhysicalBytes</c> has reached it,
/// the cycle then calls <see cref="FileArticleStorageEngine.CheckpointIndex"/>. The two
/// checkpoints run one after the other on this cycle. They do not reorder reclaim, retirement,
/// or compaction. A pending ambiguous journal append, or a compaction plan that changed while
/// the checkpoint image was built, is logged and the rest of the cycle continues. Any other
/// journal checkpoint exception leaves this method. An index checkpoint exception is logged
/// and leaves this method the same way. A capacity denial is not an exception: the index call
/// returns no retired bytes and the cycle continues.
/// </para>
/// <para>
/// Each cycle also classifies cache-volume used space
/// (<see cref="BulkStoragePressurePolicy"/>). That class is not
/// <see cref="StorageWritePressure"/> and it is not the
/// <c>MaximumUsageCapacity</c> admission latch. Normal and Warning leave the existing age,
/// expiration, and rewrite economics unchanged. From Pressure upward, a fully-dead closed
/// segment is still deleted before a new rewrite. High and Critical withhold a new rewrite
/// that would consume the configured reserves or, at Critical, that would not free more bytes
/// than it copies. Emergency does not enter usage-pressure recovery, so that recovery cannot
/// logically evict Present articles or start a new rewrite while the volume is in Emergency.
/// Age expiration still runs first. A second bounded window may then mark Present articles
/// Evicted when bulk pressure is at Pressure or higher, or while a started pass is still
/// above the recovery target. That window does not delete segment bytes.
/// In-progress compaction still finishes. Classification does not delete an acknowledged
/// ingress article.
/// </para>
/// </remarks>
public sealed class StorageMaintenanceCoordinator
{
    private readonly FileArticleStorageEngine _engine;
    private readonly ArticleSegmentPolicy _policy;
    private readonly long _journalCheckpointThresholdBytes;
    private readonly long _indexCheckpointThresholdBytes;
    private readonly TimeSpan _maxRetentionAge;
    private readonly ILogger _logger;
    private readonly BulkStoragePressurePolicy _bulkPolicy;
    private readonly object _bulkObservationLock = new();
    private readonly SemaphoreSlim _usagePressureGate = new(1, 1);
    private BulkStoragePressureEvaluation _observedBulk;

    /// <summary>
    /// True after a pressure-expiration pass has started in this process, until used percent
    /// falls below the recovery target. Not persisted.
    /// </summary>
    private bool _pressureExpirationLatched;

    /// <summary>
    /// Cycles that stayed at Warning or above without a better free-byte or class reading.
    /// Reset when pressure recovers or the volume is unmeasured. Not a history log.
    /// </summary>
    private int _consecutiveUnimprovedPressureCycles;

    /// <summary>Timestamp of the coordinator invocation currently in progress. Zero when idle.</summary>
    private long _recoveryStartedTimestamp;

    /// <summary>True after this invocation has stored the pre-expiration classification.</summary>
    private bool _recoveryBeforeReady;

    /// <summary>Capacity sample taken before logical expiration. Default when the cycle has not started.</summary>
    private CapacityAdmissionPressureSnapshot _recoveryBeforePressure;

    /// <summary>Bulk class of <see cref="_recoveryBeforePressure"/>.</summary>
    private BulkStoragePressureEvaluation _recoveryBeforeEvaluation;

    /// <summary>Age batch for the invocation in progress.</summary>
    private RetentionExpirationResult _recoveryAge;

    /// <summary>Pressure window for the invocation in progress. Zero when that pass did not run.</summary>
    private PressureExpirationResult _recoveryPressure;

    private bool _hasObservedBulk;

    /// <summary>Creates a coordinator over an open durable engine and a read-only policy.</summary>
    /// <param name="engine">Durable article storage engine.</param>
    /// <param name="policy">Compaction/reclamation victim policy.</param>
    /// <param name="journalCheckpointThresholdBytes">
    /// Physical journal length at which this cycle checkpoints. <c>0</c> does not checkpoint.
    /// </param>
    /// <param name="logger">Maintenance checkpoint logger. Null uses a no-op logger.</param>
    /// <param name="indexCheckpointThresholdBytes">
    /// Physical index frame history at which this cycle checkpoints. <c>0</c> does not checkpoint.
    /// </param>
    /// <param name="maxRetentionAge">
    /// Age at which a bulk-committed article may be logically evicted.
    /// <see cref="TimeSpan.Zero"/> disables expiration and skips the index scan.
    /// </param>
    /// <param name="bulkPressure">
    /// Cache-volume watermarks. Null uses the documented defaults. Invalid ladders throw.
    /// </param>
    public StorageMaintenanceCoordinator(
        FileArticleStorageEngine engine,
        ArticleSegmentPolicy policy,
        long journalCheckpointThresholdBytes = 0,
        ILogger? logger = null,
        long indexCheckpointThresholdBytes = 0,
        TimeSpan maxRetentionAge = default,
        BulkStoragePressureOptions? bulkPressure = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfNegative(journalCheckpointThresholdBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(indexCheckpointThresholdBytes);
        if (maxRetentionAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetentionAge));
        }
        _engine = engine;
        _policy = policy;
        _journalCheckpointThresholdBytes = journalCheckpointThresholdBytes;
        _indexCheckpointThresholdBytes = indexCheckpointThresholdBytes;
        _maxRetentionAge = maxRetentionAge;
        _logger = logger ?? NullLogger.Instance;
        _bulkPolicy = new BulkStoragePressurePolicy(bulkPressure);
        _observedBulk = BulkStoragePressurePolicy.Unmeasured();
        _pressureExpirationLatched = false;
        _engine.UsagePressureRecovery = RunAdmissionRecoveryAsync;
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
    /// Invoked after a fully-dead closed segment is revalidated and before its file is deleted.
    /// Tests only. A capacity reader that tracks deletions can drop its used bytes here.
    /// </summary>
    internal Action<SegmentId>? TestHookBeforeFullyDeadReclaim { get; set; }

    /// <summary>
    /// Invoked after an open uncommitted compaction attempt completes (before yield/return decisions).
    /// Tests only. Arguments: source segment, compaction id, attempt result.
    /// </summary>
    internal Action<SegmentId, ulong, StorageMaintenanceResult>? TestHookAfterOpenUncommittedAttempted
    {
        get;
        set;
    }

    /// <summary>
    /// Evaluates current durable state and performs at most one maintenance cycle.
    /// <see cref="StorageMaintenanceResult.Recovery"/> separates logical expiration from files deleted.
    /// </summary>
    /// <param name="cancellationToken">Cancels the cycle.</param>
    /// <param name="maintenanceRunId">
    /// Worker run id when the caller has one. Direct invocations leave this at zero.
    /// </param>
    public async Task<StorageMaintenanceResult> RunOnceAsync(
        CancellationToken cancellationToken,
        ulong maintenanceRunId = 0)
    {
        _recoveryStartedTimestamp = Stopwatch.GetTimestamp();
        _recoveryBeforeReady = false;
        _recoveryAge = default;
        _recoveryPressure = default;
        var result = await RunOnceCoreAsync(cancellationToken, maintenanceRunId).ConfigureAwait(false);
        return FinishRecoveryAccounting(result);
    }

    /// <summary>
    /// Performs one cycle. <see cref="RunOnceAsync"/> attaches the recovery account after this returns.
    /// </summary>
    private async Task<StorageMaintenanceResult> RunOnceCoreAsync(
        CancellationToken cancellationToken,
        ulong maintenanceRunId)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Metadata-only. Age expiration stays first. Pressure expiration then marks older Present
        // rows Evicted. Neither pass reads or deletes segment bytes.
        var expirationPressure = _engine.ObserveCapacityAdmissionPressure();
        ObserveBulk(in expirationPressure);
        _recoveryBeforePressure = expirationPressure;
        _recoveryBeforeEvaluation = CurrentBulk();
        _recoveryBeforeReady = true;
        _recoveryAge = _engine.ExpireRetentionBatch(_maxRetentionAge, cancellationToken);
        ApplyPressureExpiration(cancellationToken);
        TryCheckpointJournal(maintenanceRunId);
        TryCheckpointIndex(maintenanceRunId);

        // Startup leaves unreferenced-extent accounting incomplete. Victim selection must
        // not treat that under-count as a finished dead-byte inventory.
        _engine.CompleteUnreferencedExtentAccounting();

        var pressure = _engine.ObserveCapacityAdmissionPressure();
        ObserveBulk(in pressure);
        // Emergency stays on this cycle so open compaction can finish and fully-dead
        // segments can be deleted. Usage-pressure recovery would otherwise evict Present
        // articles and start a new rewrite. Journal outstanding bytes are not discarded.
        if (pressure.IsUnderUsagePressure
            && pressure.UsedBytes > pressure.UsageRecoveryTargetBytes
            && CurrentBulk().State != BulkStoragePressureState.Emergency)
        {
            return await RunUsagePressureRecoveryAsync(cancellationToken, maintenanceRunId)
                .ConfigureAwait(false);
        }

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

        // 2) Finish CompactionCommitted → relocate any late Present → Retire → Reclaim.
        // A source blocked only by a publishable PhysicalWritten yields for this run.
        StorageMaintenanceResult? firstDeferredOpenCapacitySkip = null;
        var deferredOpenCount = 0;
        HashSet<ulong>? yieldedCommittedIds = null;
        while (TryFindCommittedPendingRetirement(yieldedCommittedIds, out var committed))
        {
            var finished = await FinishCommittedCompactionAsync(committed, cancellationToken)
                .ConfigureAwait(false);
            if (!IsSameRunYieldSkip(in finished))
            {
                var primary = AttachPressure(finished, pressure);
                if (firstDeferredOpenCapacitySkip is { } priorCommitted)
                {
                    return primary.WithDeferredOpenCompaction(in priorCommitted, deferredOpenCount);
                }

                return primary;
            }

            yieldedCommittedIds ??= new HashSet<ulong>();
            _ = yieldedCommittedIds.Add(committed.Begin.CompactionId);
            deferredOpenCount++;
            firstDeferredOpenCapacitySkip ??= finished;
        }

        // 3) Continue open uncommitted compaction(s). Capacity zero-progress and pending
        // PhysicalWritten yield to the next open (Phase 5F.4), then to Closed selection
        // (Phase 5F.3). Each yielded open is not retried in this RunOnceAsync.
        HashSet<ulong>? yieldedOpenCompactionIds = null;

        while (TryFindOpenUncommitted(yieldedOpenCompactionIds, out var open))
        {
            var openResult = await CompactThenFinishAsync(
                    open.Begin.SourceSegmentId,
                    requirePolicyEligibility: false,
                    sourceAccountingHint: null,
                    continuingOpenCompaction: true,
                    cancellationToken)
                .ConfigureAwait(false);

            TestHookAfterOpenUncommittedAttempted?.Invoke(
                open.Begin.SourceSegmentId,
                open.Begin.CompactionId,
                openResult);

            if (!IsSameRunYieldSkip(in openResult))
            {
                // Progress, Failed, CompetingOpen, stale Skip, etc. — return immediately.
                var primary = AttachPressure(openResult, pressure);
                if (firstDeferredOpenCapacitySkip is { } priorDeferred)
                {
                    return primary.WithDeferredOpenCompaction(in priorDeferred, deferredOpenCount);
                }

                return primary;
            }

            yieldedOpenCompactionIds ??= new HashSet<ulong>();
            _ = yieldedOpenCompactionIds.Add(open.Begin.CompactionId);
            deferredOpenCount++;
            firstDeferredOpenCapacitySkip ??= openResult;
        }

        // Journal CompactionRetired that could not rename still has Present entries.
        // Relocation intents are rejected on that id, so continue with a new compaction id.
        if (TryFindJournalRetiredStillClosedWithPresent(out var retiredSource))
        {
            var reconciled = await CompactThenFinishAsync(
                    retiredSource,
                    requirePolicyEligibility: false,
                    sourceAccountingHint: null,
                    continuingOpenCompaction: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!IsSameRunYieldSkip(in reconciled))
            {
                var primary = AttachPressure(reconciled, pressure);
                if (firstDeferredOpenCapacitySkip is { } priorRetired)
                {
                    return primary.WithDeferredOpenCompaction(in priorRetired, deferredOpenCount);
                }

                return primary;
            }

            deferredOpenCount++;
            firstDeferredOpenCapacitySkip ??= reconciled;
        }

        // A closed segment with no live bytes is deleted whole. Open compaction sources and
        // segments that still owe a physical retirement stay on the compaction path above.
        if (TrySelectFullyDeadClosed(out var deadClosed))
        {
            var reclaimed = await TryReclaimFullyDeadClosedAsync(deadClosed, cancellationToken)
                .ConfigureAwait(false);
            if (firstDeferredOpenCapacitySkip is { } priorDead)
            {
                return AttachPressure(
                    reclaimed.WithDeferredOpenCompaction(in priorDead, deferredOpenCount),
                    _engine.ObserveCapacityAdmissionPressure());
            }

            return AttachPressure(reclaimed, _engine.ObserveCapacityAdmissionPressure());
        }

        // 4) Select a new Closed compaction victim (pressure-aware when under admission pressure).
        var closedResult = await SelectAndRunClosedVictimAsync(pressure, cancellationToken)
            .ConfigureAwait(false);

        if (firstDeferredOpenCapacitySkip is { } deferred)
        {
            // No Closed work available — preserve a capacity-zero-progress Skip (first deferred).
            if (IsNoClosedWorkOutcome(in closedResult))
            {
                // Keep primary Skip identity as the open result; only enrich when multiple opens yielded.
                if (deferredOpenCount > 1)
                {
                    return AttachPressure(
                        deferred.WithDeferredOpenCompaction(in deferred, deferredOpenCount),
                        pressure);
                }

                return AttachPressure(deferred, pressure);
            }

            // Closed path produced the primary outcome; record deferred open(s).
            return AttachPressure(
                closedResult.WithDeferredOpenCompaction(in deferred, deferredOpenCount),
                pressure);
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
                    if (WithholdNewRewrite(in pressureVictim) is { } pressureWithheld)
                    {
                        return pressureWithheld;
                    }

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
            var skips = CountRewriteSkips(closedSnapshot);
            if (skips.Density > 0 || skips.DeadBytes > 0)
            {
                StorageMaintenanceLogMessages.RewriteNotSelected(
                    _logger,
                    skips.Closed,
                    skips.Density,
                    skips.DeadBytes,
                    _policy.MinimumDeadRatio,
                    _policy.MinimumDeadBytes);
            }

            return NoWork() with { RewriteDensitySkipCount = skips.Density };
        }

        if (WithholdNewRewrite(in closedVictim) is { } withheld)
        {
            return withheld;
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
    /// Same-run yield: capacity made no progress, or the source is waiting on a publishable
    /// PhysicalWritten / pre-PhysicalWritten append. Present relocation is not a yield.
    /// </summary>
    private static bool IsSameRunYieldSkip(in StorageMaintenanceResult result) =>
        IsCapacityOpenCompactionZeroProgressSkip(in result)
        || (result.Outcome == StorageMaintenanceOutcome.Skipped
            && IsPublicationFenceReason(result.SkipReason));

    private static bool IsPublicationFenceReason(string? reason) =>
        reason is StorageMaintenanceSkipReasons.PendingPhysicalWritten
            or "pending-inflight-append"
            or "index-publication-in-flight";

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

        return MapReclamationOnly(reclaim, segment.SizeBytes, fullyDeadFile: false);
    }

    /// <summary>
    /// Selects the lowest-id Closed segment whose live bytes are zero and whose size is fully
    /// classified as dead. Active and Retired entries are ignored.
    /// </summary>
    private bool TrySelectFullyDeadClosed(out SegmentInfo victim)
    {
        victim = default;
        var reserved = CompactionReservedSegmentIds();
        SegmentInfo? best = null;
        foreach (var entry in _engine.Catalogue.Snapshot())
        {
            if (!SegmentLifecycle.IsReclaimable(entry) || reserved.Contains(entry.SegmentId.Value))
            {
                continue;
            }

            if (best is null || entry.SegmentId.Value < best.Value.SegmentId.Value)
            {
                best = entry;
            }
        }

        if (best is null)
        {
            return false;
        }

        victim = best.Value;
        return true;
    }

    /// <summary>
    /// Segment ids named by a compaction that is still open, or whose journal retirement has
    /// not yet renamed the closed file. Direct deletion must not remove those files.
    /// </summary>
    private HashSet<ulong> CompactionReservedSegmentIds()
    {
        var reserved = new HashSet<ulong>();
        foreach (var entry in _engine.Journal.EnumerateCompactions())
        {
            var sourceId = entry.Begin.SourceSegmentId;
            if (entry.Retired is null)
            {
                _ = reserved.Add(sourceId.Value);
                continue;
            }

            if (_engine.Catalogue.TryGet(sourceId, out var info) && info.State == SegmentState.Closed)
            {
                _ = reserved.Add(sourceId.Value);
            }
        }

        return reserved;
    }

    /// <summary>
    /// Revalidates the selected Closed segment, then deletes it only when it is still the same
    /// fully-dead generation.
    /// </summary>
    private async Task<StorageMaintenanceResult> TryReclaimFullyDeadClosedAsync(
        SegmentInfo segment,
        CancellationToken cancellationToken)
    {
        if (!_engine.Catalogue.TryGet(segment.SegmentId, out var current)
            || current.Generation != segment.Generation
            || current.State != SegmentState.Closed
            || !SegmentLifecycle.IsReclaimable(current))
        {
            return Skipped(segment.SegmentId, compactionId: 0, "stale-fully-dead-candidate");
        }

        cancellationToken.ThrowIfCancellationRequested();
        TestHookBeforeFullyDeadReclaim?.Invoke(segment.SegmentId);
        var reclaim = await _engine
            .ReclaimFullyDeadClosedSegmentAsync(segment.SegmentId, segment.Generation, cancellationToken)
            .ConfigureAwait(false);
        return MapReclamationOnly(reclaim, segment.SizeBytes, fullyDeadFile: true);
    }

    private async Task<StorageMaintenanceResult> FinishCommittedCompactionAsync(
        CompactionJournalSnapshot committed,
        CancellationToken cancellationToken)
    {
        // Same compaction id. Present that landed after CompactionCommitted is relocated
        // before retirement. A publishable PhysicalWritten is not relocated.
        return await CompactThenFinishAsync(
                committed.Begin.SourceSegmentId,
                requirePolicyEligibility: false,
                sourceAccountingHint: null,
                continuingOpenCompaction: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Checkpoints when the physical journal has reached the configured threshold.
    /// Does not take the engine gate. <see cref="UnreconciledDurableTailException"/> and
    /// <see cref="CheckpointCompactionChangedException"/> are deferred.
    /// </summary>
    private void TryCheckpointJournal(ulong maintenanceRunId)
    {
        if (_journalCheckpointThresholdBytes <= 0)
        {
            return;
        }

        var physicalBytes = _engine.Journal.JournalPhysicalBytes;
        if (physicalBytes < _journalCheckpointThresholdBytes)
        {
            return;
        }

        StorageMaintenanceLogMessages.JournalCheckpointAttempted(
            _logger,
            maintenanceRunId,
            physicalBytes,
            _journalCheckpointThresholdBytes);
        try
        {
            var started = Stopwatch.GetTimestamp();
            var released = _engine.CheckpointTruncateCommitted();
            var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (released > 0)
            {
                StorageMaintenanceLogMessages.JournalCheckpointOmitted(
                    _logger,
                    maintenanceRunId,
                    released,
                    durationMs);
            }
            else
            {
                StorageMaintenanceLogMessages.JournalCheckpointNothingToOmit(_logger, maintenanceRunId, durationMs);
            }
        }
        catch (UnreconciledDurableTailException ex)
        {
            StorageMaintenanceLogMessages.JournalCheckpointDeferred(_logger, maintenanceRunId, ex);
        }
        catch (CheckpointCompactionChangedException ex)
        {
            StorageMaintenanceLogMessages.JournalCheckpointDeferredCompactionChanged(
                _logger,
                maintenanceRunId,
                ex);
        }
    }

    /// <summary>
    /// Checkpoints when durable index frame history has reached the configured threshold.
    /// Does not take the engine gate. A failure is logged and propagated. A denial or an
    /// empty retirement does not fail the cycle.
    /// </summary>
    private void TryCheckpointIndex(ulong maintenanceRunId)
    {
        if (_indexCheckpointThresholdBytes <= 0)
        {
            return;
        }

        var physicalBytes = _engine.Index.IndexPhysicalBytes;
        if (physicalBytes < _indexCheckpointThresholdBytes)
        {
            return;
        }

        StorageMaintenanceLogMessages.IndexCheckpointAttempted(
            _logger,
            maintenanceRunId,
            physicalBytes,
            _indexCheckpointThresholdBytes);
        try
        {
            var started = Stopwatch.GetTimestamp();
            var retired = _engine.CheckpointIndex();
            var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (retired > 0)
            {
                StorageMaintenanceLogMessages.IndexCheckpointRetired(
                    _logger,
                    maintenanceRunId,
                    physicalBytes,
                    _indexCheckpointThresholdBytes,
                    retired,
                    durationMs);
            }
            else
            {
                StorageMaintenanceLogMessages.IndexCheckpointNothingToRetire(
                    _logger,
                    maintenanceRunId,
                    physicalBytes,
                    _indexCheckpointThresholdBytes,
                    durationMs);
            }
        }
        catch (Exception ex)
        {
            StorageMaintenanceLogMessages.IndexCheckpointFailed(
                _logger,
                maintenanceRunId,
                physicalBytes,
                _indexCheckpointThresholdBytes,
                ex);
            throw;
        }
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
                if (IsPublicationFenceReason(compact.Reason))
                {
                    return EnrichCompactionResult(
                        Skipped(
                            compact.SourceSegmentId,
                            compact.CompactionId,
                            compact.Reason),
                        compact,
                        sourceAccountingHint);
                }

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
            case ArticleSegmentRetirementOutcome.RejectedAccountingIncomplete:
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
                    ReclaimedSegmentSizeBytes: reclaimedSize,
                    PhysicalFileDeleted: reclaim.PhysicalFileDeleted),
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

    private bool TryFindCommittedPendingRetirement(
        HashSet<ulong>? excludeCompactionIds,
        out CompactionJournalSnapshot snapshot)
    {
        snapshot = default;
        CompactionJournalSnapshot? best = null;
        foreach (var entry in _engine.Journal.EnumerateOpenCompactions())
        {
            if (!entry.Committed || entry.Retired is not null)
            {
                continue;
            }

            if (excludeCompactionIds is not null
                && excludeCompactionIds.Contains(entry.Begin.CompactionId))
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

    private bool TryFindJournalRetiredStillClosedWithPresent(out SegmentId segmentId)
    {
        segmentId = default;
        ulong? best = null;
        foreach (var entry in _engine.Journal.EnumerateCompactions())
        {
            if (entry.Retired is null)
            {
                continue;
            }

            var source = entry.Begin.SourceSegmentId;
            if (!_engine.Catalogue.TryGet(source, out var info) || info.State != SegmentState.Closed)
            {
                continue;
            }

            if (CountPresent(source) == 0)
            {
                continue;
            }

            if (best is null || source.Value < best.Value)
            {
                best = source.Value;
                segmentId = source;
            }
        }

        return best is not null;
    }

    private bool TryFindOpenUncommitted(out CompactionJournalSnapshot snapshot) =>
        TryFindOpenUncommitted(excludeCompactionIds: null, out snapshot);

    /// <summary>
    /// Selects the lowest-CompactionId open uncommitted compaction not in
    /// <paramref name="excludeCompactionIds"/> (Phase 5F.4 same-run yield set).
    /// </summary>
    private bool TryFindOpenUncommitted(
        HashSet<ulong>? excludeCompactionIds,
        out CompactionJournalSnapshot snapshot)
    {
        snapshot = default;
        CompactionJournalSnapshot? best = null;
        foreach (var entry in _engine.Journal.EnumerateOpenCompactions())
        {
            if (entry.Committed || entry.Retired is not null)
            {
                continue;
            }

            if (excludeCompactionIds is not null
                && excludeCompactionIds.Contains(entry.Begin.CompactionId))
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
        long reclaimedSegmentSizeBytes,
        bool fullyDeadFile)
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
                    ReclaimedSegmentSizeBytes: reclaimedSegmentSizeBytes,
                    PhysicalFileDeleted: reclaim.PhysicalFileDeleted,
                    FullyDeadFileReclaim: fullyDeadFile && reclaim.PhysicalFileDeleted),

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

    private StorageMaintenanceResult AttachPressure(
        StorageMaintenanceResult result,
        in CapacityAdmissionPressureSnapshot pressure)
    {
        ObserveBulk(in pressure);
        return StampBulk(result.WithCapacityPressure(in pressure));
    }

    /// <summary>
    /// Expires one bounded window when bulk pressure is active or a previous pass is still above
    /// the recovery target. Does not scan segment payloads and does not delete files.
    /// </summary>
    private void ApplyPressureExpiration(CancellationToken cancellationToken)
    {
        var bulk = CurrentBulk();
        var mode = ArticlePressureRetentionPolicy.Mode(in bulk, _pressureExpirationLatched, _bulkPolicy.Options);
        var latched = mode != PressureExpirationMode.None;
        if (latched != _pressureExpirationLatched)
        {
            _pressureExpirationLatched = latched;
            var target = ArticlePressureRetentionPolicy.RecoveryTargetPercent(_bulkPolicy.Options);
            if (latched)
            {
                StorageMaintenanceLogMessages.PressureExpirationStarted(
                    _logger,
                    bulk.State.ToString(),
                    bulk.UsedPercent,
                    target,
                    _bulkPolicy.Options.MinimumRetentionAge);
            }
            else
            {
                StorageMaintenanceLogMessages.PressureExpirationStopped(
                    _logger,
                    bulk.State.ToString(),
                    bulk.UsedPercent,
                    target);
            }
        }

        if (!latched)
        {
            _recoveryPressure = default;
            return;
        }

        _recoveryPressure = _engine.ExpirePressureRetentionBatch(
            _bulkPolicy.Options.MinimumRetentionAge,
            ArticlePressureRetentionPolicy.ExpirationLimit(bulk.State, mode),
            bulk.State,
            cancellationToken);
    }

    /// <summary>
    /// Samples capacity once more and attaches logical-versus-physical recovery. Does not expire
    /// articles or delete files. Logical expired bytes are not added to free space.
    /// </summary>
    private StorageMaintenanceResult FinishRecoveryAccounting(StorageMaintenanceResult result)
    {
        var afterPressure = _engine.ObserveCapacityAdmissionPressure();
        ObserveBulk(in afterPressure);
        result = StampBulk(result);
        var before = _recoveryBeforeReady
            ? _recoveryBeforeEvaluation
            : BulkStoragePressurePolicy.Unmeasured();
        var after = CurrentBulk();
        var age = _recoveryAge;
        var pressureExpiration = _recoveryPressure;
        var accounting = RetentionRecoveryAccounting.Compose(
            in before,
            in after,
            in age,
            in pressureExpiration,
            in result,
            ArticlePressureRetentionPolicy.RecoveryTargetPercent(_bulkPolicy.Options),
            _consecutiveUnimprovedPressureCycles,
            Stopwatch.GetElapsedTime(_recoveryStartedTimestamp).TotalMilliseconds);
        _consecutiveUnimprovedPressureCycles = accounting.ConsecutiveUnimprovedCycles;
        LogRecovery(in accounting);
        return result with { Recovery = accounting };
    }

    /// <summary>
    /// One recovery summary per cycle. Warning and above, or any real expiration or release,
    /// is Information. A stuck High or worse class with no physical release is Warning.
    /// </summary>
    private void LogRecovery(in RetentionRecoveryAccounting accounting)
    {
        var released = accounting.FullyDeadBytesReclaimed
            + accounting.RetiredBytesReclaimed
            + accounting.CompactionSourceBytesReclaimed;
        if (accounting.ConsecutiveUnimprovedCycles >= 2
            && accounting.NetPhysicalRecoveryBytes <= 0
            && accounting.PressureStateAfter is nameof(BulkStoragePressureState.High)
                or nameof(BulkStoragePressureState.Critical)
                or nameof(BulkStoragePressureState.Emergency))
        {
            StorageMaintenanceLogMessages.RetentionRecoveryStalled(
                _logger,
                accounting.PressureStateBefore,
                accounting.PressureStateAfter,
                accounting.FreeBytesBefore,
                accounting.FreeBytesAfter,
                accounting.LogicalExpiredBytes,
                released,
                accounting.NetPhysicalRecoveryBytes,
                accounting.ConsecutiveUnimprovedCycles);
            return;
        }

        if (!accounting.Measured
            || accounting.PressureStateAfter is nameof(BulkStoragePressureState.Normal)
            && accounting.LogicalExpiredBytes == 0
            && released == 0
            && !accounting.PressureImproved)
        {
            StorageMaintenanceLogMessages.RetentionRecoverySummary(
                _logger,
                accounting.PressureStateBefore,
                accounting.PressureStateAfter,
                accounting.FreeBytesBefore,
                accounting.FreeBytesAfter,
                accounting.LogicalExpiredBytes,
                released,
                accounting.NetPhysicalRecoveryBytes,
                accounting.PressureImproved,
                accounting.ConsecutiveUnimprovedCycles);
            return;
        }

        StorageMaintenanceLogMessages.RetentionRecoveryAttention(
            _logger,
            accounting.PressureStateBefore,
            accounting.PressureStateAfter,
            accounting.FreeBytesBefore,
            accounting.FreeBytesAfter,
            accounting.AgeExpiredBytes,
            accounting.PressureExpiredBytes,
            accounting.LogicalExpiredBytes,
            accounting.FullyDeadBytesReclaimed,
            accounting.CompactionDestinationBytesWritten,
            accounting.CompactionSourceBytesReclaimed,
            accounting.NetPhysicalRecoveryBytes,
            accounting.PressureImproved,
            accounting.RecoveryTargetReached,
            accounting.ConsecutiveUnimprovedCycles);
    }

    private void ObserveBulk(in CapacityAdmissionPressureSnapshot pressure)
    {
        var evaluation = _bulkPolicy.Evaluate(pressure.TotalBytes, pressure.UsedBytes, pressure.AvailableBytes);
        BulkStoragePressureState? previous;
        lock (_bulkObservationLock)
        {
            previous = _hasObservedBulk ? _observedBulk.State : null;
            _observedBulk = evaluation;
            _hasObservedBulk = true;
        }

        if (previous == evaluation.State)
        {
            return;
        }

        var previousName = previous?.ToString() ?? "Unobserved";
        StorageMaintenanceLogMessages.BulkPressureMeasured(
            _logger,
            evaluation.State.ToString(),
            evaluation.TotalBytes,
            evaluation.FreeBytes,
            evaluation.UsedBytes,
            evaluation.UsedPercent,
            evaluation.AvailableReserveBytes,
            evaluation.MaintenanceMode);
        StorageMaintenanceLogMessages.BulkPressureStateChanged(
            _logger,
            previousName,
            evaluation.State.ToString(),
            evaluation.UsedPercent,
            evaluation.MaintenanceMode);
        if (evaluation.State is BulkStoragePressureState.Warning
            or BulkStoragePressureState.Pressure
            or BulkStoragePressureState.High)
        {
            StorageMaintenanceLogMessages.BulkPressureAttention(
                _logger,
                evaluation.State.ToString(),
                evaluation.UsedPercent,
                evaluation.FreeBytes,
                evaluation.AvailableReserveBytes,
                evaluation.MaintenanceMode);
        }
        else if (evaluation.State is BulkStoragePressureState.Critical
            or BulkStoragePressureState.Emergency)
        {
            StorageMaintenanceLogMessages.BulkPressureCritical(
                _logger,
                evaluation.State.ToString(),
                evaluation.UsedPercent,
                evaluation.FreeBytes,
                evaluation.AvailableReserveBytes,
                evaluation.EmergencyAdmissionProtectionRequired,
                evaluation.MaintenanceMode);
        }
    }

    /// <summary>Copies the latest bulk classification onto <paramref name="result"/>.</summary>
    private StorageMaintenanceResult StampBulk(StorageMaintenanceResult result)
    {
        var bulk = CurrentBulk();
        return result with
        {
            BulkPressureState = bulk.State.ToString(),
            BulkUsedPercent = bulk.Measured ? bulk.UsedPercent : null,
            BulkFreeBytes = bulk.Measured ? bulk.FreeBytes : null,
            BulkAvailableReserveBytes = bulk.Measured ? bulk.AvailableReserveBytes : null,
            BulkMaintenanceMode = bulk.MaintenanceMode,
            BulkEmergencyAdmissionProtectionRequired = bulk.EmergencyAdmissionProtectionRequired,
        };
    }

    /// <summary>Latest cache-volume classification. Unmeasured until the first observation.</summary>
    private BulkStoragePressureEvaluation CurrentBulk()
    {
        lock (_bulkObservationLock)
        {
            return _observedBulk;
        }
    }

    /// <summary>
    /// Returns a no-work result when bulk pressure forbids starting a new rewrite of
    /// <paramref name="victim"/>. Null when the rewrite may start. Does not affect an
    /// in-progress compaction.
    /// </summary>
    private StorageMaintenanceResult? WithholdNewRewrite(in SegmentInfo victim)
    {
        var bulk = CurrentBulk();
        if (_bulkPolicy.AllowsNewRewrite(in bulk, victim.LiveBytes, victim.DeadBytes))
        {
            return null;
        }

        StorageMaintenanceLogMessages.BulkRewriteWithheld(
            _logger,
            bulk.State.ToString(),
            victim.SegmentId.Value,
            victim.LiveBytes,
            victim.DeadBytes,
            bulk.AvailableReserveBytes);
        return NoWork() with
        {
            SegmentId = victim.SegmentId,
            BulkRewriteSuppressed = true,
            SkipReason = StorageMaintenanceSkipReasons.BulkRewriteWithheld,
        };
    }

    /// <summary>
    /// Accept-path recovery. Reclaims one already-retired segment and one already fully-dead
    /// closed segment when the cache volume is High, Critical, or Emergency, then runs
    /// usage-pressure recovery only while that latch is on and the volume is not Emergency.
    /// Does not scan segment payloads, start a low-density rewrite, or evict Present articles
    /// for Emergency. Extent accounting stays on the maintenance cycle.
    /// </summary>
    private async Task RunAdmissionRecoveryAsync(CancellationToken cancellationToken)
    {
        var pressure = _engine.ObserveCapacityAdmissionPressure();
        ObserveBulk(in pressure);
        var bulk = CurrentBulk();
        if (bulk.State is BulkStoragePressureState.High
            or BulkStoragePressureState.Critical
            or BulkStoragePressureState.Emergency)
        {
            if (_policy.TrySelectReclamationVictim(_engine.Catalogue, out var retired))
            {
                _ = await TryReclaimRetiredAsync(retired, cancellationToken).ConfigureAwait(false);
            }

            if (TrySelectFullyDeadClosed(out var dead))
            {
                _ = await TryReclaimFullyDeadClosedAsync(dead, cancellationToken).ConfigureAwait(false);
            }
        }

        pressure = _engine.ObserveCapacityAdmissionPressure();
        ObserveBulk(in pressure);
        if (CurrentBulk().State == BulkStoragePressureState.Emergency)
        {
            return;
        }

        if (pressure.IsUnderUsagePressure && pressure.UsedBytes > pressure.UsageRecoveryTargetBytes)
        {
            _ = await RunUsagePressureRecoveryAsync(cancellationToken, maintenanceRunId: 0)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Evicts least-frequently-used articles and compacts/reclaims until filesystem usage is at
    /// or below the physical recovery target, or until a pass makes no physical progress.
    /// Logical eviction alone does not finish the pass.
    /// </summary>
    internal async Task<StorageMaintenanceResult> RunUsagePressureRecoveryAsync(
        CancellationToken cancellationToken,
        ulong maintenanceRunId = 0)
    {
        _ = maintenanceRunId;
        await _usagePressureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunUsagePressureRecoveryCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _usagePressureGate.Release();
        }
    }

    private async Task<StorageMaintenanceResult> RunUsagePressureRecoveryCoreAsync(
        CancellationToken cancellationToken)
    {
        StorageMaintenanceResult last = NoWork();
        for (var step = 0; step < 64; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pressure = _engine.ObserveCapacityAdmissionPressure();
            if (!pressure.IsUnderUsagePressure || pressure.UsedBytes <= pressure.UsageRecoveryTargetBytes)
            {
                return AttachPressure(last, pressure);
            }

            if (_policy.TrySelectReclamationVictim(_engine.Catalogue, out var retiredVictim))
            {
                TestHookAfterReclamationVictimSelected?.Invoke(retiredVictim.SegmentId);
                var reclaimed = await TryReclaimRetiredAsync(retiredVictim, cancellationToken)
                    .ConfigureAwait(false);
                var afterReclaim = _engine.ObserveCapacityAdmissionPressure();
                last = AttachPressure(reclaimed, afterReclaim);
                if (afterReclaim.UsedBytes < pressure.UsedBytes)
                {
                    continue;
                }

                if (reclaimed.Reclaimed)
                {
                    return last;
                }
            }

            ObserveBulk(in pressure);
            if (CurrentBulk().State == BulkStoragePressureState.Emergency)
            {
                // Direct recovery (including the Accept callback) must not evict Present
                // articles or start a rewrite while the cache volume is in Emergency.
                // RunOnceAsync does not enter this method in that state.
                if (TrySelectFullyDeadClosed(out var emergencyDead))
                {
                    var deadResult = await TryReclaimFullyDeadClosedAsync(emergencyDead, cancellationToken)
                        .ConfigureAwait(false);
                    return AttachPressure(deadResult, _engine.ObserveCapacityAdmissionPressure());
                }

                return AttachPressure(
                    NoWork() with
                    {
                        BulkRewriteSuppressed = true,
                        SkipReason = StorageMaintenanceSkipReasons.BulkRewriteWithheld,
                    },
                    pressure);
            }

            await _engine.Segments.CloseActiveAsync(cancellationToken).ConfigureAwait(false);
            _engine.CompleteUnreferencedExtentAccounting();
            pressure = _engine.ObserveCapacityAdmissionPressure();
            ObserveBulk(in pressure);
            if (CurrentBulk().AccelerateReclamation
                && TrySelectFullyDeadClosed(out var acceleratedDead))
            {
                var deadResult = await TryReclaimFullyDeadClosedAsync(acceleratedDead, cancellationToken)
                    .ConfigureAwait(false);
                if (deadResult.Reclaimed || deadResult.Outcome == StorageMaintenanceOutcome.Failed)
                {
                    return AttachPressure(deadResult, _engine.ObserveCapacityAdmissionPressure());
                }
            }

            var closed = CatalogueSnapshotExcludingOpenUncommittedSources();
            if (!_policy.TrySelectUsagePressureCompactionVictim(closed, in pressure, out var victim))
            {
                var deficit = pressure.UsedBytes - pressure.UsageRecoveryTargetBytes;
                var evicted = _engine.EvictLeastFrequentlyUsed(deficit);
                if (evicted == 0)
                {
                    evicted = _engine.EvictLeastFrequentlyUsed(long.MaxValue);
                }

                if (evicted == 0)
                {
                    return AttachPressure(
                        Skipped(default, compactionId: 0, StorageMaintenanceSkipReasons.CapacityPressureUnrecoverable),
                        pressure);
                }

                await _engine.Segments.CloseActiveAsync(cancellationToken).ConfigureAwait(false);
                _engine.CompleteUnreferencedExtentAccounting();
                pressure = _engine.ObserveCapacityAdmissionPressure();
                closed = CatalogueSnapshotExcludingOpenUncommittedSources();
                if (!_policy.TrySelectUsagePressureCompactionVictim(closed, in pressure, out victim))
                {
                    return AttachPressure(
                        Skipped(default, compactionId: 0, StorageMaintenanceSkipReasons.CapacityInsufficientHeadroom),
                        pressure);
                }
            }

            if (WithholdNewRewrite(in victim) is { } withheldRewrite)
            {
                return AttachPressure(withheldRewrite, pressure);
            }

            TestHookAfterCompactionVictimSelected?.Invoke(victim.SegmentId);
            last = await CompactThenFinishAsync(
                    victim.SegmentId,
                    requirePolicyEligibility: false,
                    sourceAccountingHint: victim,
                    continuingOpenCompaction: false,
                    cancellationToken)
                .ConfigureAwait(false);
            var afterCompact = _engine.ObserveCapacityAdmissionPressure();
            if (afterCompact.UsedBytes < pressure.UsedBytes)
            {
                continue;
            }

            if (last.Reclaimed || last.CompactionCommitted || last.Retired)
            {
                return AttachPressure(last, afterCompact);
            }

            if (!last.Reclaimed)
            {
                var reason = last.SkipReason
                    ?? StorageMaintenanceSkipReasons.CapacityPressureUnrecoverable;
                return AttachPressure(
                    last.Outcome == StorageMaintenanceOutcome.Skipped
                        ? last
                        : Skipped(victim.SegmentId, last.CompactionId, reason),
                    afterCompact);
            }
        }

        return AttachPressure(
            Skipped(default, compactionId: 0, StorageMaintenanceSkipReasons.CapacityPressureUnrecoverable),
            _engine.ObserveCapacityAdmissionPressure());
    }

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

    private StorageMaintenanceResult IncompleteFromCompaction(
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

    private StorageMaintenanceResult FailedFromCompaction(
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

    private StorageMaintenanceResult EnrichCompactionResult(
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

        return result with { DestinationSegmentId = DestinationSegmentOf(result.CompactionId) };
    }

    /// <summary>
    /// Counts Closed segments the ordinary rewrite gate left alone on this snapshot.
    /// </summary>
    private (int Closed, int Density, int DeadBytes) CountRewriteSkips(IReadOnlyList<SegmentInfo> snapshot)
    {
        var closed = 0;
        var density = 0;
        var deadBytes = 0;
        foreach (var entry in snapshot)
        {
            if (entry.State != SegmentState.Closed)
            {
                continue;
            }

            closed++;
            switch (_policy.EvaluateCompaction(in entry).Reason)
            {
                case CompactionEligibilityReason.InsufficientDeadRatio:
                    density++;
                    break;
                case CompactionEligibilityReason.InsufficientDeadBytes:
                    deadBytes++;
                    break;
            }
        }

        return (closed, density, deadBytes);
    }

    /// <summary>
    /// Lowest segment id that a durable RelocationWritten names for <paramref name="compactionId"/>.
    /// </summary>
    private ulong? DestinationSegmentOf(ulong compactionId)
    {
        if (compactionId == 0 || !_engine.Journal.TryGetCompaction(compactionId, out var snapshot))
        {
            return null;
        }

        ulong? destination = null;
        foreach (var relocation in snapshot.Relocations)
        {
            if (relocation.Written is not { } written)
            {
                continue;
            }

            var segmentId = written.DestinationLocation.SegmentId.Value;
            if (destination is null || segmentId < destination.Value)
            {
                destination = segmentId;
            }
        }

        return destination;
    }
}
