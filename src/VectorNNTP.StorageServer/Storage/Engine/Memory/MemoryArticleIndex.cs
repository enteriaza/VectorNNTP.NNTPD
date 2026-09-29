using VectorNNTP.Common.Articles;

namespace VectorNNTP.StorageServer.Storage.Engine.Memory;

/// <summary>In-memory logical article index with relocate and soft LastAccess hints.</summary>
public sealed class MemoryArticleIndex : IArticleIndex
{
    private readonly object _gate = new();
    private readonly Dictionary<ArticleId, StoredArticleMetadata> _entries = new();
    private long _durableWriteCount;
    private long _touchHintCount;

    /// <summary>Number of durable-style mutations (commit/relocate/set-state). Tests.</summary>
    public long DurableWriteCount
    {
        get
        {
            lock (_gate)
            {
                return _durableWriteCount;
            }
        }
    }

    /// <summary>Number of soft TouchHint calls. Tests.</summary>
    public long TouchHintCount
    {
        get
        {
            lock (_gate)
            {
                return _touchHintCount;
            }
        }
    }

    /// <inheritdoc />
    public bool TryGet(ArticleId artId, out StoredArticleMetadata metadata)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(artId, out metadata);
        }
    }

    /// <inheritdoc />
    public bool TryCommitPresent(in StoredArticleMetadata metadata)
    {
        if (metadata.State != ArticleStorageState.Present)
        {
            return false;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(metadata.ArtId, out var existing)
                && existing.State == ArticleStorageState.Present)
            {
                if (existing.ArtHash == metadata.ArtHash
                    && existing.ArtSize == metadata.ArtSize
                    && LocationsEqual(existing.Location, metadata.Location))
                {
                    return true;
                }

                if (existing.ArtHash == metadata.ArtHash && existing.ArtSize == metadata.ArtSize)
                {
                    // Same identity, different location — require TryRelocate.
                    return false;
                }

                return false;
            }

            _entries[metadata.ArtId] = metadata;
            _durableWriteCount++;
            return true;
        }
    }

    /// <inheritdoc />
    public ArticleRelocateOutcome TryRelocate(
        ArticleId artId,
        in StoredArticleLocation expectedLocation,
        in StoredArticleLocation newLocation,
        ulong artHash,
        int artSize)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(artId, out var existing)
                || existing.State != ArticleStorageState.Present)
            {
                return ArticleRelocateOutcome.NotPresent;
            }

            if (existing.ArtHash != artHash || existing.ArtSize != artSize)
            {
                return ArticleRelocateOutcome.IdentityMismatch;
            }

            if (LocationsEqual(existing.Location, newLocation))
            {
                return ArticleRelocateOutcome.IdempotentNoOp;
            }

            if (!LocationsEqual(existing.Location, expectedLocation))
            {
                return ArticleRelocateOutcome.ExpectedLocationMismatch;
            }

            _entries[artId] = existing with { Location = newLocation };
            _durableWriteCount++;
            return ArticleRelocateOutcome.Relocated;
        }
    }

    /// <inheritdoc />
    public bool TrySetState(ArticleId artId, ArticleStorageState state, DateTimeOffset utcNow)
    {
        if (state is not (ArticleStorageState.Evicted or ArticleStorageState.Invalid))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(artId, out var existing))
            {
                return false;
            }

            if (existing.State == state)
            {
                return true;
            }

            if (existing.State != ArticleStorageState.Present)
            {
                return false;
            }

            _entries[artId] = existing with { State = state, LastAccessUtc = utcNow };
            _durableWriteCount++;
            return true;
        }
    }

    /// <inheritdoc />
    public void TouchHint(ArticleId artId, DateTimeOffset utcNow)
    {
        lock (_gate)
        {
            _touchHintCount++;
            if (!_entries.TryGetValue(artId, out var existing)
                || existing.State != ArticleStorageState.Present)
            {
                return;
            }

            // Soft in-memory hint only — does not increment DurableWriteCount.
            _entries[artId] = existing with { LastAccessUtc = utcNow };
        }
    }

    private static bool LocationsEqual(in StoredArticleLocation left, in StoredArticleLocation right) =>
        left.SegmentId.Value == right.SegmentId.Value
        && left.Offset == right.Offset
        && left.Length == right.Length;
}
