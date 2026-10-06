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
    /// journal frame is required. After the catalogue entry is gone, Evicted and Invalid index
    /// rows that still name this segment are dropped from memory. Present rows are not dropped.
    /// A crash before the next index checkpoint replays those frames, and open drops them again
    /// because the segment file is absent.
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
                _ = _index.ForgetReclaimedSegment(segmentId);
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
                _ = _index.ForgetReclaimedSegment(segmentId);
                return Task.FromResult(new ArticleSegmentReclamationResult(
                    ArticleSegmentReclamationOutcome.IdempotentAlreadyReclaimed,
                    segmentId,
                    PhysicalFileDeleted: false,
                    CatalogueEntryRemoved: !Catalogue.TryGet(segmentId, out _),
                    Reason: "already-reclaimed"));
            }

            if (_capacityAdmissionEnabled)
            {
                _ = RequireSegmentVolume().WithLedger(ledger =>
                {
                    _ = ledger.ReleaseCompactionDestinationsOnSegment(segmentId);
                    _ = ledger.ReleaseWrittenArticleCopiesOnSegment(segmentId);
                    return true;
                });
            }

            _ = _index.ForgetReclaimedSegment(segmentId);
            return Task.FromResult(new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.Reclaimed,
                segmentId,
                PhysicalFileDeleted: true,
                CatalogueEntryRemoved: !Catalogue.TryGet(segmentId, out _),
                Reason: null));
        }
        catch (IOException) when (afterDeleteHook is not null)
        {
            // The file is already gone. Drop written article holds for it exactly once.
            // The catalogue is still Retired; a later call takes the already-reclaimed path.
            if (_capacityAdmissionEnabled)
            {
                _ = RequireSegmentVolume().WithLedger(
                    ledger => ledger.ReleaseWrittenArticleCopiesOnSegment(segmentId));
            }

            throw;
        }
    }

    /// <summary>
    /// Deletes one Closed segment whose catalogue accounting shows zero live bytes and no
    /// unclassified gap, then forgets Evicted and Invalid index rows for that segment.
    /// </summary>
    /// <remarks>
    /// Refuses Active segments, Retired segments, and any Closed segment that still has a
    /// Present row, pending publication, or a catalogue generation other than
    /// <paramref name="expectedGeneration"/>. The physical file is deleted before the catalogue
    /// entry and before index rows are forgotten. A failed delete leaves the file, catalogue,
    /// and index rows in place.
    /// </remarks>
    /// <param name="segmentId">Closed segment selected as fully dead.</param>
    /// <param name="expectedGeneration">Catalogue generation observed when the segment was selected.</param>
    /// <param name="cancellationToken">Cancels before the physical delete.</param>
    /// <returns>The reclamation outcome. <see cref="ArticleSegmentReclamationOutcome.Reclaimed"/> means the file was deleted.</returns>
    public Task<ArticleSegmentReclamationResult> ReclaimFullyDeadClosedSegmentAsync(
        SegmentId segmentId,
        ulong expectedGeneration,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        var closedPath = Path.Combine(
            _segments.RootPath,
            SegmentFileNames.Format(segmentId, SegmentFileKind.Closed));
        var activePath = Path.Combine(
            _segments.RootPath,
            SegmentFileNames.Format(segmentId, SegmentFileKind.Active));
        var retiredPath = Path.Combine(
            _segments.RootPath,
            SegmentFileNames.Format(segmentId, SegmentFileKind.Retired));

        if (!Catalogue.TryGet(segmentId, out var info))
        {
            if (!File.Exists(closedPath) && !File.Exists(activePath) && !File.Exists(retiredPath))
            {
                _ = _index.ForgetReclaimedSegment(segmentId);
                return Task.FromResult(new ArticleSegmentReclamationResult(
                    ArticleSegmentReclamationOutcome.IdempotentAlreadyReclaimed,
                    segmentId,
                    PhysicalFileDeleted: false,
                    CatalogueEntryRemoved: true,
                    Reason: "already-reclaimed"));
            }

            return Task.FromResult(new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.RejectedMissing,
                segmentId,
                PhysicalFileDeleted: false,
                CatalogueEntryRemoved: false,
                Reason: "catalogue-missing"));
        }

        if (info.State == SegmentState.Active || info.Generation != expectedGeneration)
        {
            var reason = info.State == SegmentState.Active ? "segment-active" : "generation-changed";
            return Task.FromResult(new ArticleSegmentReclamationResult(
                info.State == SegmentState.Active
                    ? ArticleSegmentReclamationOutcome.RejectedActive
                    : ArticleSegmentReclamationOutcome.RejectedUnexpectedPhysical,
                segmentId,
                PhysicalFileDeleted: false,
                CatalogueEntryRemoved: false,
                Reason: reason));
        }

        if (info.State != SegmentState.Closed)
        {
            return Task.FromResult(new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.Failed,
                segmentId,
                PhysicalFileDeleted: false,
                CatalogueEntryRemoved: false,
                Reason: "segment-not-closed"));
        }

        if (!SegmentLifecycle.IsReclaimable(info))
        {
            var reason = info.LiveBytes > 0 ? "live-bytes-remain" : "extent-accounting-incomplete";
            return Task.FromResult(new ArticleSegmentReclamationResult(
                info.LiveBytes > 0
                    ? ArticleSegmentReclamationOutcome.RejectedPresentRemain
                    : ArticleSegmentReclamationOutcome.RejectedClosed,
                segmentId,
                PhysicalFileDeleted: false,
                CatalogueEntryRemoved: false,
                Reason: reason));
        }

        if (!TryReadSourcePublicationFence(segmentId, out var fenceReason))
        {
            return Task.FromResult(new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.RejectedPresentRemain,
                segmentId,
                PhysicalFileDeleted: false,
                CatalogueEntryRemoved: false,
                Reason: fenceReason ?? "present-remain-on-source"));
        }

        if (!Catalogue.TryGet(segmentId, out info)
            || info.State != SegmentState.Closed
            || info.Generation != expectedGeneration
            || !SegmentLifecycle.IsReclaimable(info))
        {
            return Task.FromResult(new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.RejectedUnexpectedPhysical,
                segmentId,
                PhysicalFileDeleted: false,
                CatalogueEntryRemoved: false,
                Reason: "stale-fully-dead-candidate"));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfReclamationFault(ReclamationFaultPoint.BeforeDelete);

        var existedBefore = File.Exists(closedPath);
        var reclaimedBytes = info.SizeBytes;
        Action? afterDeleteHook = null;
        if (TestReclamationFaultPoint == ReclamationFaultPoint.AfterDeleteBeforeCatalogueRemove)
        {
            TestReclamationFaultPoint = ReclamationFaultPoint.None;
            afterDeleteHook = static () =>
                throw new IOException("Injected reclamation fault at AfterDeleteBeforeCatalogueRemove.");
        }

        try
        {
            if (!_segments.TryReclaimFullyDeadClosed(
                    segmentId,
                    expectedGeneration,
                    out var failureReason,
                    afterDeleteHook))
            {
                FileArticleStorageEngineLogMessages.FullyDeadSegmentReclamationFailed(
                    _logger,
                    segmentId.Value,
                    failureReason ?? "reclaim-failed");
                return Task.FromResult(MapClosedReclaimFailure(segmentId, failureReason));
            }

            if (string.Equals(failureReason, "already-reclaimed", StringComparison.Ordinal)
                || !existedBefore)
            {
                _ = _index.ForgetReclaimedSegment(segmentId);
                return Task.FromResult(new ArticleSegmentReclamationResult(
                    ArticleSegmentReclamationOutcome.IdempotentAlreadyReclaimed,
                    segmentId,
                    PhysicalFileDeleted: false,
                    CatalogueEntryRemoved: !Catalogue.TryGet(segmentId, out _),
                    Reason: "already-reclaimed"));
            }

            if (_capacityAdmissionEnabled)
            {
                _ = RequireSegmentVolume().WithLedger(ledger =>
                {
                    _ = ledger.ReleaseCompactionDestinationsOnSegment(segmentId);
                    _ = ledger.ReleaseWrittenArticleCopiesOnSegment(segmentId);
                    return true;
                });
            }

            _ = _index.ForgetReclaimedSegment(segmentId);
            FileArticleStorageEngineLogMessages.FullyDeadSegmentReclaimed(
                _logger,
                segmentId.Value,
                reclaimedBytes,
                "fully-dead");
            return Task.FromResult(new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.Reclaimed,
                segmentId,
                PhysicalFileDeleted: true,
                CatalogueEntryRemoved: !Catalogue.TryGet(segmentId, out _),
                Reason: "fully-dead"));
        }
        catch (IOException) when (afterDeleteHook is not null)
        {
            if (_capacityAdmissionEnabled)
            {
                _ = RequireSegmentVolume().WithLedger(
                    ledger => ledger.ReleaseWrittenArticleCopiesOnSegment(segmentId));
            }

            throw;
        }
    }

    private static ArticleSegmentReclamationResult MapClosedReclaimFailure(
        SegmentId segmentId,
        string? failureReason)
    {
        if (failureReason is not null && failureReason.StartsWith("delete-failed:", StringComparison.Ordinal))
        {
            return new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.Failed,
                segmentId,
                PhysicalFileDeleted: false,
                CatalogueEntryRemoved: false,
                Reason: failureReason);
        }

        return failureReason switch
        {
            "segment-active" => new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.RejectedActive,
                segmentId,
                PhysicalFileDeleted: false,
                CatalogueEntryRemoved: false,
                Reason: failureReason),
            "live-bytes-remain" => new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.RejectedPresentRemain,
                segmentId,
                PhysicalFileDeleted: false,
                CatalogueEntryRemoved: false,
                Reason: failureReason),
            "extent-accounting-incomplete" or "segment-not-closed" => new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.RejectedClosed,
                segmentId,
                PhysicalFileDeleted: false,
                CatalogueEntryRemoved: false,
                Reason: failureReason),
            "generation-changed" or "stale-fully-dead-candidate"
                or "unexpected-active-or-retired-file"
                or "runtime-not-closed"
                or "runtime-path-not-closed" => new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.RejectedUnexpectedPhysical,
                segmentId,
                PhysicalFileDeleted: false,
                CatalogueEntryRemoved: false,
                Reason: failureReason),
            "catalogue-missing-with-closed-file" => new ArticleSegmentReclamationResult(
                ArticleSegmentReclamationOutcome.RejectedMissing,
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
