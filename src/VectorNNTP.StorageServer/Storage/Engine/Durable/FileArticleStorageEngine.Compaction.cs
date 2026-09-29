using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;

namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

/// <summary>Phase 4B.4: deterministic single-Closed-segment compaction orchestration.</summary>
public sealed partial class FileArticleStorageEngine
{
    /// <summary>
    /// Invoked immediately before each <see cref="RelocateArticleAsync"/> call during compaction.
    /// Tests only (eviction/invalidation races). Remains installed for the whole compaction call.
    /// </summary>
    internal Action<ArticleId>? TestHookBeforeRelocateArticle { get; set; }

    /// <summary>
    /// Compacts one Closed source segment by relocating Present index entries through
    /// <see cref="RelocateArticleAsync"/>, then appends <c>CompactionCommitted</c> only when a
    /// fresh index snapshot shows zero Present references to the source.
    /// </summary>
    /// <remarks>
    /// Does not scan SATA. Does not retire/rename/delete the source. Does not append
    /// CompactionRetired. Does not touch the article cache. Continues an existing uncommitted
    /// open compaction for the same source when present (using durable Begin generation).
    /// </remarks>
    public async Task<ArticleCompactionResult> CompactClosedSegmentAsync(
        SegmentId sourceSegmentId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Catalogue.TryGet(sourceSegmentId, out var sourceInfo))
        {
            return new ArticleCompactionResult(
                ArticleCompactionOutcome.RejectedSourceMissing,
                CompactionId: 0,
                sourceSegmentId,
                SourceGeneration: 0,
                InitialCandidateCount: 0,
                RelocatedCount: 0,
                AbandonedCount: 0,
                RemainingPresentOnSource: CountPresentOnSource(sourceSegmentId),
                CompactionCommittedAppended: false,
                Reason: "source-missing");
        }

        if (sourceInfo.State == SegmentState.Retired)
        {
            return new ArticleCompactionResult(
                ArticleCompactionOutcome.RejectedSourceNotClosed,
                CompactionId: 0,
                sourceSegmentId,
                SourceGeneration: 0,
                InitialCandidateCount: 0,
                RelocatedCount: 0,
                AbandonedCount: 0,
                RemainingPresentOnSource: CountPresentOnSource(sourceSegmentId),
                CompactionCommittedAppended: false,
                Reason: "source-retired");
        }

        if (sourceInfo.State != SegmentState.Closed)
        {
            return new ArticleCompactionResult(
                ArticleCompactionOutcome.RejectedSourceNotClosed,
                CompactionId: 0,
                sourceSegmentId,
                SourceGeneration: 0,
                InitialCandidateCount: 0,
                RelocatedCount: 0,
                AbandonedCount: 0,
                RemainingPresentOnSource: CountPresentOnSource(sourceSegmentId),
                CompactionCommittedAppended: false,
                Reason: "source-not-closed");
        }

        if (!TryResolveCompactionTransaction(
                sourceSegmentId,
                sourceInfo.Generation,
                out var compactionId,
                out var sourceGeneration,
                out var alreadyCommitted,
                out var resolveReject))
        {
            return resolveReject;
        }

        if (alreadyCommitted)
        {
            var remainingCommitted = CountPresentOnSource(sourceSegmentId);
            return new ArticleCompactionResult(
                remainingCommitted == 0
                    ? ArticleCompactionOutcome.Committed
                    : ArticleCompactionOutcome.Incomplete,
                compactionId,
                sourceSegmentId,
                sourceGeneration,
                InitialCandidateCount: 0,
                RelocatedCount: 0,
                AbandonedCount: 0,
                RemainingPresentOnSource: remainingCommitted,
                CompactionCommittedAppended: false,
                Reason: remainingCommitted == 0 ? "already-committed" : "committed-but-present-remain");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Durable CompactionBegin when starting a new transaction (continuation skips this).
        if (!_journal.TryGetCompaction(compactionId, out _))
        {
            var beginOutcome = await _journal
                .AppendCompactionBeginAsync(
                    new JournalCompactionBeginRecord(1, compactionId, sourceSegmentId, sourceGeneration),
                    cancellationToken)
                .ConfigureAwait(false);
            if (beginOutcome is JournalAppendOutcome.Rejected or JournalAppendOutcome.Conflict)
            {
                return new ArticleCompactionResult(
                    ArticleCompactionOutcome.Failed,
                    compactionId,
                    sourceSegmentId,
                    sourceGeneration,
                    InitialCandidateCount: 0,
                    RelocatedCount: 0,
                    AbandonedCount: 0,
                    RemainingPresentOnSource: CountPresentOnSource(sourceSegmentId),
                    CompactionCommittedAppended: false,
                    Reason: "compaction-begin-" + beginOutcome);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var worklist = BuildRelocationWorklist(sourceSegmentId, compactionId);
        var relocated = 0;
        var abandoned = 0;

        foreach (var (relocationId, artId) in worklist)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TestHookBeforeRelocateArticle?.Invoke(artId);

            var relocate = await RelocateArticleAsync(
                    compactionId,
                    relocationId,
                    sourceSegmentId,
                    sourceGeneration,
                    artId,
                    cancellationToken)
                .ConfigureAwait(false);

            switch (relocate.Outcome)
            {
                case ArticleRelocationOutcome.Relocated:
                case ArticleRelocationOutcome.IdempotentNoOp:
                    relocated++;
                    break;

                case ArticleRelocationOutcome.Abandoned:
                case ArticleRelocationOutcome.RejectedNotPresent:
                    // Concurrent eviction/invalidation/move — never resurrect.
                    abandoned++;
                    break;

                case ArticleRelocationOutcome.RejectedCapacity:
                    return new ArticleCompactionResult(
                        ArticleCompactionOutcome.Incomplete,
                        compactionId,
                        sourceSegmentId,
                        sourceGeneration,
                        worklist.Count,
                        relocated,
                        abandoned,
                        CountPresentOnSource(sourceSegmentId),
                        CompactionCommittedAppended: false,
                        Reason: "capacity"
                                + (relocate.Reason is null ? string.Empty : ":" + relocate.Reason));

                case ArticleRelocationOutcome.RejectedSourceCorrupt:
                case ArticleRelocationOutcome.Conflict:
                case ArticleRelocationOutcome.RejectedCompaction:
                case ArticleRelocationOutcome.RejectedGenerationMismatch:
                case ArticleRelocationOutcome.RejectedSourceMissing:
                case ArticleRelocationOutcome.RejectedSourceNotClosed:
                    return new ArticleCompactionResult(
                        ArticleCompactionOutcome.Failed,
                        compactionId,
                        sourceSegmentId,
                        sourceGeneration,
                        worklist.Count,
                        relocated,
                        abandoned,
                        CountPresentOnSource(sourceSegmentId),
                        CompactionCommittedAppended: false,
                        Reason: "relocate-" + relocate.Outcome
                                + (relocate.Reason is null ? string.Empty : ":" + relocate.Reason));

                default:
                    return new ArticleCompactionResult(
                        ArticleCompactionOutcome.Failed,
                        compactionId,
                        sourceSegmentId,
                        sourceGeneration,
                        worklist.Count,
                        relocated,
                        abandoned,
                        CountPresentOnSource(sourceSegmentId),
                        CompactionCommittedAppended: false,
                        Reason: "relocate-unknown-" + relocate.Outcome);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var remaining = CountPresentOnSource(sourceSegmentId);
        if (remaining > 0)
        {
            return new ArticleCompactionResult(
                ArticleCompactionOutcome.Incomplete,
                compactionId,
                sourceSegmentId,
                sourceGeneration,
                worklist.Count,
                relocated,
                abandoned,
                remaining,
                CompactionCommittedAppended: false,
                Reason: "present-remain-on-source");
        }

        var commitOutcome = await _journal
            .AppendCompactionCommittedAsync(
                new JournalCompactionCommittedRecord(1, compactionId),
                cancellationToken)
            .ConfigureAwait(false);

        if (commitOutcome is JournalAppendOutcome.Rejected or JournalAppendOutcome.Conflict)
        {
            return new ArticleCompactionResult(
                ArticleCompactionOutcome.Failed,
                compactionId,
                sourceSegmentId,
                sourceGeneration,
                worklist.Count,
                relocated,
                abandoned,
                RemainingPresentOnSource: 0,
                CompactionCommittedAppended: false,
                Reason: "compaction-committed-" + commitOutcome);
        }

        return new ArticleCompactionResult(
            ArticleCompactionOutcome.Committed,
            compactionId,
            sourceSegmentId,
            sourceGeneration,
            worklist.Count,
            relocated,
            abandoned,
            RemainingPresentOnSource: 0,
            CompactionCommittedAppended: commitOutcome == JournalAppendOutcome.Applied,
            Reason: commitOutcome == JournalAppendOutcome.IdempotentNoOp ? "commit-idempotent" : null);
    }

    private bool TryResolveCompactionTransaction(
        SegmentId sourceSegmentId,
        ulong catalogueGeneration,
        out ulong compactionId,
        out ulong sourceGeneration,
        out bool alreadyCommitted,
        out ArticleCompactionResult rejected)
    {
        compactionId = 0;
        sourceGeneration = 0;
        alreadyCommitted = false;
        rejected = default;

        var forSource = _journal.EnumerateOpenCompactions()
            .Where(c => c.Begin.SourceSegmentId.Value == sourceSegmentId.Value && c.Retired is null)
            .OrderBy(c => c.Begin.CompactionId)
            .ToArray();

        var uncommitted = forSource.Where(c => !c.Committed).ToArray();
        if (uncommitted.Length > 1)
        {
            rejected = new ArticleCompactionResult(
                ArticleCompactionOutcome.CompetingOpenCompaction,
                CompactionId: 0,
                sourceSegmentId,
                SourceGeneration: 0,
                InitialCandidateCount: 0,
                RelocatedCount: 0,
                AbandonedCount: 0,
                RemainingPresentOnSource: CountPresentOnSource(sourceSegmentId),
                CompactionCommittedAppended: false,
                Reason: "multiple-uncommitted-compactions");
            return false;
        }

        if (uncommitted.Length == 1)
        {
            compactionId = uncommitted[0].Begin.CompactionId;
            // Carry durable Begin generation — never substitute rediscovered catalogue generation.
            sourceGeneration = uncommitted[0].Begin.SourceGeneration;
            alreadyCommitted = false;
            return true;
        }

        var committed = forSource.Where(c => c.Committed).ToArray();
        if (committed.Length >= 1)
        {
            var latest = committed[^1];
            compactionId = latest.Begin.CompactionId;
            sourceGeneration = latest.Begin.SourceGeneration;
            alreadyCommitted = true;
            return true;
        }

        compactionId = _journal.AllocateCompactionId();
        sourceGeneration = catalogueGeneration;
        alreadyCommitted = false;
        return true;
    }

    private List<(ulong RelocationId, ArticleId ArtId)> BuildRelocationWorklist(
        SegmentId sourceSegmentId,
        ulong compactionId)
    {
        var present = _index.Snapshot()
            .Where(m => m.State == ArticleStorageState.Present
                        && m.Location.SegmentId.Value == sourceSegmentId.Value)
            .OrderBy(static m => m.ArtId, ArticleIdComparer.Instance)
            .ToArray();

        Dictionary<ArticleId, ulong>? existingByArtId = null;
        ulong nextRelocationId = 1;
        if (_journal.TryGetCompaction(compactionId, out var snap))
        {
            existingByArtId = new Dictionary<ArticleId, ulong>();
            foreach (var relocation in snap.Relocations)
            {
                existingByArtId[relocation.Intent.ArtId] = relocation.Intent.RelocationId;
                if (relocation.Intent.RelocationId >= nextRelocationId)
                {
                    nextRelocationId = relocation.Intent.RelocationId + 1;
                }
            }
        }

        var worklist = new List<(ulong, ArticleId)>(present.Length);
        foreach (var meta in present)
        {
            ulong relocationId;
            if (existingByArtId is not null
                && existingByArtId.TryGetValue(meta.ArtId, out var existingId))
            {
                relocationId = existingId;
            }
            else
            {
                relocationId = nextRelocationId++;
            }

            worklist.Add((relocationId, meta.ArtId));
        }

        return worklist;
    }

    private int CountPresentOnSource(SegmentId sourceSegmentId) =>
        _index.Snapshot()
            .Count(m => m.State == ArticleStorageState.Present
                        && m.Location.SegmentId.Value == sourceSegmentId.Value);

    /// <summary>Deterministic ArticleId ordering for compaction worklists (digest byte order).</summary>
    private sealed class ArticleIdComparer : IComparer<ArticleId>
    {
        public static ArticleIdComparer Instance { get; } = new();

        public int Compare(ArticleId x, ArticleId y)
        {
            Span<byte> left = stackalloc byte[ArticleId.Length];
            Span<byte> right = stackalloc byte[ArticleId.Length];
            x.CopyTo(left);
            y.CopyTo(right);
            return left.SequenceCompareTo(right);
        }
    }
}
