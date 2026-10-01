using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;

namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

public sealed partial class FileArticleStorageEngine
{
    /// <summary>Maximum fresh appends when a location was sealed before PhysicalWritten.</summary>
    internal const int MaxPrePhysicalWrittenAppendAttempts = 8;

    /// <summary>
    /// Invoked after this sequence has entered index publication and before
    /// <c>TryCommitPresent</c>. Tests only. Not called under <see cref="_publicationFence"/>.
    /// Cleared before invoke.
    /// </summary>
    internal Action<SegmentId>? TestHookAfterPublicationEnteredBeforeIndexCommit { get; set; }

    /// <summary>
    /// Leaf lock for publication depth, pre-PhysicalWritten markers, and rename seals.
    /// Never held across segment IO, journal flush, catalogue rename, or capacity reads.
    /// Never acquired while the engine gate, index lock, journal lock, segment write gate,
    /// or catalogue lock is held.
    /// </summary>
    private readonly object _publicationFence = new();

    /// <summary>Accept append locations that do not yet have a durable PhysicalWritten.</summary>
    private readonly Dictionary<ulong, StoredArticleLocation> _prePhysicalWritten = new();

    /// <summary>Sequences inside <c>TryCommitPresent</c> for a segment, keyed by segment id.</summary>
    private readonly Dictionary<ulong, int> _indexPublicationDepth = new();

    /// <summary>Segments whose Closed→Retired rename has passed the final fence.</summary>
    private readonly HashSet<ulong> _retirementSealed = new();

    /// <summary>
    /// Records an accept append that has not reached PhysicalWritten.
    /// Returns false when <paramref name="location"/> is already sealed for rename.
    /// </summary>
    private bool TryRegisterPrePhysicalWritten(ulong sequence, in StoredArticleLocation location)
    {
        lock (_publicationFence)
        {
            if (_retirementSealed.Contains(location.SegmentId.Value))
            {
                return false;
            }

            _prePhysicalWritten[sequence] = location;
            return true;
        }
    }

    /// <summary>Marks <paramref name="segmentId"/> sealed so a later pre-PhysicalWritten register fails. Tests only.</summary>
    internal void TestSealSegmentForPrePhysicalWritten(ulong segmentId)
    {
        lock (_publicationFence)
        {
            _ = _retirementSealed.Add(segmentId);
        }
    }

    /// <summary>Drops the pre-PhysicalWritten marker for <paramref name="sequence"/>.</summary>
    private void ClearPrePhysicalWritten(ulong sequence)
    {
        lock (_publicationFence)
        {
            _ = _prePhysicalWritten.Remove(sequence);
        }
    }

    /// <summary>
    /// True when completion of an incomplete PhysicalWritten can still publish Present
    /// on <paramref name="segmentId"/>. Accept-only sequences are not a fence. Does not read segments.
    /// </summary>
    private bool HasPublishableIncompletePhysicalWritten(SegmentId segmentId)
    {
        foreach (var incomplete in _journal.EnumerateIncomplete())
        {
            if (incomplete.PhysicalWritten is not { } written)
            {
                continue;
            }

            if (written.Location.SegmentId.Value != segmentId.Value)
            {
                continue;
            }

            if (CanPhysicalWrittenStillPublish(incomplete.Accept, written.Location))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Non-sealing snapshot used before CompactionCommitted and before appending CompactionRetired.
    /// </summary>
    /// <returns>False when the source must not be treated as exhausted.</returns>
    private bool TryReadSourcePublicationFence(SegmentId segmentId, out string? blockReason)
    {
        lock (_publicationFence)
        {
            if (IsPublicationBlockedUnlocked(segmentId.Value))
            {
                blockReason = PublicationBlockReasonUnlocked(segmentId.Value);
                return false;
            }
        }

        if (CountPresentOnSource(segmentId) > 0)
        {
            blockReason = "present-remain-on-source";
            return false;
        }

        if (HasPublishableIncompletePhysicalWritten(segmentId))
        {
            blockReason = "pending-physical-written";
            return false;
        }

        blockReason = null;
        return true;
    }

    /// <summary>
    /// Passes the final rename fence and seals <paramref name="segmentId"/> so a concurrent
    /// <c>TryCommitPresent</c> cannot publish onto it. Caller renames outside this lock.
    /// </summary>
    private bool TrySealSourceForRename(SegmentId segmentId)
    {
        lock (_publicationFence)
        {
            if (IsPublicationBlockedUnlocked(segmentId.Value))
            {
                return false;
            }

            _ = _retirementSealed.Add(segmentId.Value);
        }

        if (CountPresentOnSource(segmentId) > 0
            || HasPublishableIncompletePhysicalWritten(segmentId))
        {
            UnsealSource(segmentId);
            return false;
        }

        lock (_publicationFence)
        {
            if (IsPublicationBlockedUnlocked(segmentId.Value))
            {
                _ = _retirementSealed.Remove(segmentId.Value);
                return false;
            }
        }

        return true;
    }

    /// <summary>Drops a rename seal after <c>TryRetire</c> fails. Successful renames stay sealed.</summary>
    private void UnsealSource(SegmentId segmentId)
    {
        lock (_publicationFence)
        {
            _ = _retirementSealed.Remove(segmentId.Value);
        }
    }

    /// <summary>
    /// Enters the publication critical section. False when rename has already sealed the segment.
    /// </summary>
    private bool TryEnterIndexPublication(ulong segmentId)
    {
        lock (_publicationFence)
        {
            if (_retirementSealed.Contains(segmentId))
            {
                return false;
            }

            _ = _indexPublicationDepth.TryGetValue(segmentId, out var depth);
            _indexPublicationDepth[segmentId] = depth + 1;
            return true;
        }
    }

    /// <summary>Leaves the publication critical section started by <see cref="TryEnterIndexPublication"/>.</summary>
    private void ExitIndexPublication(ulong segmentId)
    {
        lock (_publicationFence)
        {
            if (!_indexPublicationDepth.TryGetValue(segmentId, out var depth))
            {
                return;
            }

            if (depth <= 1)
            {
                _ = _indexPublicationDepth.Remove(segmentId);
            }
            else
            {
                _indexPublicationDepth[segmentId] = depth - 1;
            }
        }
    }

    /// <summary>Caller holds <see cref="_publicationFence"/>.</summary>
    private bool IsPublicationBlockedUnlocked(ulong segmentId)
    {
        if (_indexPublicationDepth.TryGetValue(segmentId, out var depth) && depth > 0)
        {
            return true;
        }

        foreach (var location in _prePhysicalWritten.Values)
        {
            if (location.SegmentId.Value == segmentId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Caller holds <see cref="_publicationFence"/>.</summary>
    private string PublicationBlockReasonUnlocked(ulong segmentId)
    {
        if (_indexPublicationDepth.TryGetValue(segmentId, out var depth) && depth > 0)
        {
            return "index-publication-in-flight";
        }

        return "pending-inflight-append";
    }

    /// <summary>
    /// True when a later completion can still create Present at <paramref name="location"/>.
    /// </summary>
    /// <remarks>
    /// The index row sequence is the logical transaction. An equal or newer row already superseded
    /// this <c>PhysicalWritten</c>, so it must not publish. A newer Accept than the row may still
    /// publish over a dead row. <see cref="ArticleStorageState.Present"/> is not overwritten.
    /// </remarks>
    private bool CanPhysicalWrittenStillPublish(
        JournalAcceptRecord accept,
        in StoredArticleLocation location)
    {
        if (!_index.TryGet(accept.ArtId, out var existing))
        {
            return true;
        }

        if (existing.Sequence >= accept.Sequence)
        {
            return false;
        }

        return existing.State != ArticleStorageState.Present;
    }
}
