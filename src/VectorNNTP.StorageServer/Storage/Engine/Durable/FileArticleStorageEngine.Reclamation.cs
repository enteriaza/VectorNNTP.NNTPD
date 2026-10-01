using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

/// <summary>Phase 4C.2: deterministic physical reclamation of one Retired segment.</summary>
public sealed partial class FileArticleStorageEngine
{
    /// <summary>Test fault injection during physical reclamation (engine-owned only).</summary>
    internal enum ReclamationFaultPoint
    {
        None = 0,
        BeforeDelete = 1,
        AfterDeleteBeforeCatalogueRemove = 2,
    }

    /// <summary>Optional one-shot reclamation fault (cleared when consumed). Tests only.</summary>
    internal ReclamationFaultPoint TestReclamationFaultPoint { get; set; }

    /// <summary>
    /// Physically deletes one Retired segment's <c>.retired</c> file, then removes its catalogue
    /// entry. Does not scan SATA for articles; refuses when any Present index entry references
    /// the segment.
    /// </summary>
    /// <remarks>
    /// Ordering is delete-then-catalogue-remove. Catalogue reconstruction is file-authoritative,
    /// so a crash after delete still yields a permanently absent segment on restart. No new
    /// journal frame is required.
    /// </remarks>
    public Task<ArticleSegmentReclamationResult> ReclaimRetiredSegmentAsync(
        SegmentId segmentId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryReadSourcePublicationFence(segmentId, out var fenceReason))
        {
            return Task.FromResult(new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.RejectedPresentRemain,
                segmentId,
                PhysicalFileDeleted: false,
                CatalogueEntryRemoved: false,
                Reason: fenceReason ?? "present-remain-on-source"));
        }

        var retiredPath = Path.Combine(
            _segments.RootPath,
            SegmentFileNames.Format(segmentId, SegmentFileKind.Retired));
        var activePath = Path.Combine(
            _segments.RootPath,
            SegmentFileNames.Format(segmentId, SegmentFileKind.Active));
        var closedPath = Path.Combine(
            _segments.RootPath,
            SegmentFileNames.Format(segmentId, SegmentFileKind.Closed));

        if (!Catalogue.TryGet(segmentId, out var info))
        {
            if (!File.Exists(retiredPath) && !File.Exists(activePath) && !File.Exists(closedPath))
            {
                return Task.FromResult(new ArticleSegmentReclamationResult(
                    ArticleSegmentReclamationOutcome.IdempotentAlreadyReclaimed,
                    segmentId,
                    PhysicalFileDeleted: false,
                    CatalogueEntryRemoved: false,
                    Reason: "already-reclaimed"));
            }

            return Task.FromResult(new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.RejectedMissing,
                segmentId,
                PhysicalFileDeleted: false,
                CatalogueEntryRemoved: false,
                Reason: "catalogue-missing"));
        }

        if (info.State == SegmentState.Active)
        {
            return Task.FromResult(new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.RejectedActive,
                segmentId,
                PhysicalFileDeleted: false,
                CatalogueEntryRemoved: false,
                Reason: "segment-active"));
        }

        if (info.State == SegmentState.Closed)
        {
            return Task.FromResult(new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.RejectedClosed,
                segmentId,
                PhysicalFileDeleted: false,
                CatalogueEntryRemoved: false,
                Reason: "segment-closed"));
        }

        if (info.State != SegmentState.Retired)
        {
            return Task.FromResult(new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.Failed,
                segmentId,
                PhysicalFileDeleted: false,
                CatalogueEntryRemoved: false,
                Reason: "unexpected-state-" + info.State));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfReclamationFault(ReclamationFaultPoint.BeforeDelete);

        var existedBefore = File.Exists(retiredPath);
        Action? afterDeleteHook = null;
        if (TestReclamationFaultPoint == ReclamationFaultPoint.AfterDeleteBeforeCatalogueRemove)
        {
            TestReclamationFaultPoint = ReclamationFaultPoint.None;
            afterDeleteHook = static () =>
                throw new IOException("Injected reclamation fault at AfterDeleteBeforeCatalogueRemove.");
        }

        try
        {
            if (!_segments.TryReclaimRetired(segmentId, out var failureReason, afterDeleteHook))
            {
                return Task.FromResult(MapReclaimFailure(segmentId, failureReason));
            }

            if (string.Equals(failureReason, "already-reclaimed", StringComparison.Ordinal)
                || !existedBefore)
            {
                return Task.FromResult(new ArticleSegmentReclamationResult(
                    ArticleSegmentReclamationOutcome.IdempotentAlreadyReclaimed,
                    segmentId,
                    PhysicalFileDeleted: false,
                    CatalogueEntryRemoved: !Catalogue.TryGet(segmentId, out _),
                    Reason: "already-reclaimed"));
            }

            if (_capacityAdmissionEnabled)
            {
                _ = RequireSegmentVolume().WithLedger(
                    ledger => ledger.ReleaseCompactionDestinationsOnSegment(segmentId));
            }

            return Task.FromResult(new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.Reclaimed,
                segmentId,
                PhysicalFileDeleted: true,
                CatalogueEntryRemoved: !Catalogue.TryGet(segmentId, out _),
                Reason: null));
        }
        catch (IOException) when (afterDeleteHook is not null)
        {
            // Crash window: file deleted, catalogue still Retired — reopen remediates.
            throw;
        }
    }

    private static ArticleSegmentReclamationResult MapReclaimFailure(
        SegmentId segmentId,
        string? failureReason)
    {
        return failureReason switch
        {
            "already-reclaimed" => new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.IdempotentAlreadyReclaimed,
                segmentId,
                PhysicalFileDeleted: false,
                CatalogueEntryRemoved: false,
                Reason: failureReason),
            "segment-active" => new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.RejectedActive,
                segmentId,
                PhysicalFileDeleted: false,
                CatalogueEntryRemoved: false,
                Reason: failureReason),
            "segment-closed" => new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.RejectedClosed,
                segmentId,
                PhysicalFileDeleted: false,
                CatalogueEntryRemoved: false,
                Reason: failureReason),
            "unexpected-active-or-closed-file"
                or "runtime-path-not-retired"
                or "catalogue-missing-with-retired-file" => new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.RejectedUnexpectedPhysical,
                segmentId,
                PhysicalFileDeleted: false,
                CatalogueEntryRemoved: false,
                Reason: failureReason),
            _ => new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.Failed,
                segmentId,
                PhysicalFileDeleted: false,
                CatalogueEntryRemoved: false,
                Reason: failureReason ?? "reclaim-failed"),
        };
    }

    private void ThrowIfReclamationFault(ReclamationFaultPoint point)
    {
        if (TestReclamationFaultPoint != point)
        {
            return;
        }

        TestReclamationFaultPoint = ReclamationFaultPoint.None;
        throw new IOException($"Injected reclamation fault at {point}.");
    }
}
