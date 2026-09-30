using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;

namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

/// <summary>Phase 4C.1: durable CompactionRetired + catalogue Closed→Retired fence.</summary>
public sealed partial class FileArticleStorageEngine
{
    /// <summary>Test fault injection during retirement (engine-owned only).</summary>
    internal enum RetirementFaultPoint
    {
        None = 0,
        BeforeCompactionRetired = 1,
        AfterCompactionRetiredBeforeCatalogue = 2,
    }

    /// <summary>Optional one-shot retirement fault (cleared when consumed). Tests only.</summary>
    internal RetirementFaultPoint TestRetirementFaultPoint { get; set; }

    /// <summary>
    /// Durably retires a CompactionCommitted source segment: appends <c>CompactionRetired</c>,
    /// then publishes catalogue/file Closed→Retired via the existing segment-store rename.
    /// </summary>
    /// <remarks>
    /// Does not delete or truncate the physical file. Does not touch Live/Dead accounting or
    /// the article cache. Uses durable <c>CompactionBegin.SourceGeneration</c> in the journal
    /// record; catalogue <see cref="ISegmentCatalogue.TryRetire"/> uses the process-local
    /// catalogue generation after the journal record is durable.
    /// </remarks>
    public async Task<ArticleSegmentRetirementResult> RetireCompactedSegmentAsync(
        ulong compactionId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (compactionId == 0 || !_journal.TryGetCompaction(compactionId, out var snap))
        {
            return new ArticleSegmentRetirementResult(
                ArticleSegmentRetirementOutcome.RejectedUnknownCompaction,
                compactionId,
                default,
                SourceGeneration: 0,
                CompactionRetiredAppended: false,
                Reason: "unknown-compaction");
        }

        var sourceId = snap.Begin.SourceSegmentId;
        var beginGeneration = snap.Begin.SourceGeneration;

        if (snap.Retired is not null)
        {
            return CompleteCatalogueRetirement(
                compactionId,
                sourceId,
                beginGeneration,
                compactionRetiredAppended: false,
                alreadyJournalRetired: true);
        }

        if (!snap.Committed)
        {
            return new ArticleSegmentRetirementResult(
                ArticleSegmentRetirementOutcome.RejectedNotCommitted,
                compactionId,
                sourceId,
                beginGeneration,
                CompactionRetiredAppended: false,
                Reason: "not-committed");
        }

        if (!TryReadSourcePublicationFence(sourceId, out var fenceReason))
        {
            return new ArticleSegmentRetirementResult(
                ArticleSegmentRetirementOutcome.RejectedPresentRemain,
                compactionId,
                sourceId,
                beginGeneration,
                CompactionRetiredAppended: false,
                Reason: fenceReason);
        }

        if (!Catalogue.TryGet(sourceId, out var info))
        {
            return new ArticleSegmentRetirementResult(
                ArticleSegmentRetirementOutcome.RejectedSourceMissing,
                compactionId,
                sourceId,
                beginGeneration,
                CompactionRetiredAppended: false,
                Reason: "source-missing");
        }

        if (info.State == SegmentState.Active)
        {
            return new ArticleSegmentRetirementResult(
                ArticleSegmentRetirementOutcome.RejectedSourceNotClosed,
                compactionId,
                sourceId,
                beginGeneration,
                CompactionRetiredAppended: false,
                Reason: "source-active");
        }

        if (info.State == SegmentState.Retired)
        {
            // Catalogue already Retired without journal Retired is inconsistent; still append
            // durable CompactionRetired so recovery stays authoritative.
        }
        else if (info.State != SegmentState.Closed)
        {
            return new ArticleSegmentRetirementResult(
                ArticleSegmentRetirementOutcome.RejectedSourceNotClosed,
                compactionId,
                sourceId,
                beginGeneration,
                CompactionRetiredAppended: false,
                Reason: "source-not-closed");
        }

        ThrowIfRetirementFault(RetirementFaultPoint.BeforeCompactionRetired);

        var retiredRecord = new JournalCompactionRetiredRecord(
            1,
            compactionId,
            sourceId,
            beginGeneration);
        var appendOutcome = await _journal
            .AppendCompactionRetiredAsync(retiredRecord, cancellationToken)
            .ConfigureAwait(false);

        if (appendOutcome == JournalAppendOutcome.Conflict)
        {
            return new ArticleSegmentRetirementResult(
                ArticleSegmentRetirementOutcome.Failed,
                compactionId,
                sourceId,
                beginGeneration,
                CompactionRetiredAppended: false,
                Reason: "compaction-retired-conflict");
        }

        if (appendOutcome == JournalAppendOutcome.Rejected)
        {
            return new ArticleSegmentRetirementResult(
                ArticleSegmentRetirementOutcome.Failed,
                compactionId,
                sourceId,
                beginGeneration,
                CompactionRetiredAppended: false,
                Reason: "compaction-retired-rejected");
        }

        ThrowIfRetirementFault(RetirementFaultPoint.AfterCompactionRetiredBeforeCatalogue);

        return CompleteCatalogueRetirement(
            compactionId,
            sourceId,
            beginGeneration,
            compactionRetiredAppended: appendOutcome == JournalAppendOutcome.Applied,
            alreadyJournalRetired: false);
    }

    /// <summary>
    /// Applies durable CompactionRetired fences to the catalogue/file lifecycle after journal
    /// replay (crash between CompactionRetired and catalogue publication).
    /// </summary>
    internal void ApplyRetiredCompactionsFromJournal()
    {
        foreach (var compaction in _journal.EnumerateCompactions())
        {
            if (compaction.Retired is null)
            {
                continue;
            }

            PublishCatalogueRetired(compaction.Begin.SourceSegmentId);
        }
    }

    private ArticleSegmentRetirementResult CompleteCatalogueRetirement(
        ulong compactionId,
        SegmentId sourceId,
        ulong beginGeneration,
        bool compactionRetiredAppended,
        bool alreadyJournalRetired)
    {
        if (!Catalogue.TryGet(sourceId, out var info))
        {
            return new ArticleSegmentRetirementResult(
                ArticleSegmentRetirementOutcome.Failed,
                compactionId,
                sourceId,
                beginGeneration,
                compactionRetiredAppended,
                Reason: "source-missing-after-retired-journal");
        }

        if (info.State == SegmentState.Retired)
        {
            return new ArticleSegmentRetirementResult(
                alreadyJournalRetired
                    ? ArticleSegmentRetirementOutcome.IdempotentNoOp
                    : ArticleSegmentRetirementOutcome.Retired,
                compactionId,
                sourceId,
                beginGeneration,
                compactionRetiredAppended,
                Reason: alreadyJournalRetired ? "already-retired" : null);
        }

        if (info.State == SegmentState.Active)
        {
            return new ArticleSegmentRetirementResult(
                ArticleSegmentRetirementOutcome.RejectedSourceNotClosed,
                compactionId,
                sourceId,
                beginGeneration,
                compactionRetiredAppended,
                Reason: "source-active-after-retired-journal");
        }

        if (info.State != SegmentState.Closed)
        {
            return new ArticleSegmentRetirementResult(
                ArticleSegmentRetirementOutcome.Failed,
                compactionId,
                sourceId,
                beginGeneration,
                compactionRetiredAppended,
                Reason: "unexpected-source-state-" + info.State);
        }

        // Catalogue Generation is process-local; after restart it may differ from
        // CompactionBegin.SourceGeneration. Durable fencing already lives in CompactionRetired.
        if (!PublishCatalogueRetired(sourceId))
        {
            return new ArticleSegmentRetirementResult(
                ArticleSegmentRetirementOutcome.Failed,
                compactionId,
                sourceId,
                beginGeneration,
                compactionRetiredAppended,
                Reason: "catalogue-try-retire-failed");
        }

        return new ArticleSegmentRetirementResult(
            alreadyJournalRetired
                ? ArticleSegmentRetirementOutcome.Retired
                : ArticleSegmentRetirementOutcome.Retired,
            compactionId,
            sourceId,
            beginGeneration,
            compactionRetiredAppended);
    }

    private bool PublishCatalogueRetired(SegmentId sourceId)
    {
        if (!Catalogue.TryGet(sourceId, out var info))
        {
            return false;
        }

        if (info.State == SegmentState.Retired)
        {
            return true;
        }

        if (info.State != SegmentState.Closed)
        {
            return false;
        }

        // Seal only around the decision. Catalogue rename runs after this method returns
        // from the seal section; TryRetire performs the file move outside _publicationFence.
        if (!TrySealSourceForRename(sourceId))
        {
            return false;
        }

        if (!Catalogue.TryRetire(sourceId, info.Generation, _timeProvider.GetUtcNow()))
        {
            UnsealSource(sourceId);
            return false;
        }

        return true;
    }

    private void ThrowIfRetirementFault(RetirementFaultPoint point)
    {
        if (TestRetirementFaultPoint != point)
        {
            return;
        }

        TestRetirementFaultPoint = RetirementFaultPoint.None;
        throw new IOException($"Injected retirement fault at {point}.");
    }
}
