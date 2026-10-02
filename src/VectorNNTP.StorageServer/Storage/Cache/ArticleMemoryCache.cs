using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;

namespace VectorNNTP.StorageServer.Storage.Cache;

/// <summary>
/// Size-bounded in-process LRU cache of complete CanonicalV1 <see cref="ArticleRecord"/> values.
/// </summary>
/// <remarks>
/// <para>
/// Dictionary + doubly-linked recency list. Head = most recently used; tail = least recently used.
/// Byte accounting uses ArtData length only. Eviction is synchronous on Put.
/// </para>
/// <para>
/// On Put, ArtData is copied into a cache-owned buffer so callers cannot mutate cached bytes.
/// <see cref="AdoptOwned"/> stores a caller-transferred payload without copying it.
/// TryGet returns a record referencing that owned buffer via <see cref="ArticleRecord.ArtData"/>
/// (<see cref="ReadOnlyMemory{T}"/>); callers must not mutate underlying storage.
/// </para>
/// </remarks>
public sealed class ArticleMemoryCache : IArticleMemoryCache, IArticleMemoryCacheAdoption
{
    private readonly object _gate = new();
    private readonly Dictionary<ArticleId, Node> _map;
    private readonly long _maxBytes;
    private Node? _head;
    private Node? _tail;
    private long _currentBytes;

    /// <summary>Creates a cache with <paramref name="maxBytes"/> ArtData capacity (0 = disabled).</summary>
    public ArticleMemoryCache(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        _maxBytes = maxBytes;
        _map = new Dictionary<ArticleId, Node>();
    }

    /// <summary>Creates a cache from bindable options.</summary>
    public ArticleMemoryCache(ArticleMemoryCacheOptions options)
        : this(options?.MaxBytes ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    /// <inheritdoc />
    public long MaxBytes => _maxBytes;

    /// <inheritdoc />
    public long CurrentBytes
    {
        get
        {
            lock (_gate)
            {
                return _currentBytes;
            }
        }
    }

    /// <inheritdoc />
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    /// <inheritdoc />
    public bool TryGet(ArticleId artId, out ArticleRecord record)
    {
        lock (_gate)
        {
            if (_maxBytes == 0 || !_map.TryGetValue(artId, out var node))
            {
                record = default;
                IndexCommittedProbe.NoteCacheLookup(false);
                return false;
            }

            MoveToHeadUnlocked(node);
            record = node.Record;
            IndexCommittedProbe.NoteCacheLookup(true);
            return true;
        }
    }

    /// <inheritdoc />
    public ArticleMemoryCachePutOutcome Put(in ArticleRecord record) =>
        Insert(in record, adoptOwned: false);

    /// <summary>
    /// Stores <paramref name="record"/> without copying its payload.
    /// </summary>
    /// <remarks>
    /// The caller transfers ownership of the underlying payload to the cache and must not
    /// subsequently mutate, reuse, or retain ownership of that payload.
    /// After <see cref="ArticleMemoryCachePutOutcome.Inserted"/>, the cache owns the buffer.
    /// A disabled, invalid, oversized, idempotent, or conflicting result does not retain the
    /// candidate. An exception before the entry is published does not retain it either.
    /// <see cref="Put"/> still copies caller-owned records.
    /// </remarks>
    internal ArticleMemoryCachePutOutcome AdoptOwned(in ArticleRecord record) =>
        Insert(in record, adoptOwned: true);

    /// <inheritdoc />
    ArticleMemoryCachePutOutcome IArticleMemoryCacheAdoption.AdoptOwned(in ArticleRecord record) =>
        AdoptOwned(in record);

    private ArticleMemoryCachePutOutcome Insert(in ArticleRecord record, bool adoptOwned)
    {
        IndexCommittedProbe.NoteCapacity(_maxBytes);
        IndexCommittedProbe.NoteArticleBytes(record.ArtSize, _maxBytes);
        if (_maxBytes == 0)
        {
            IndexCommittedProbe.NoteOutcome(ArticleMemoryCachePutOutcome.RejectedDisabled, record.ArtSize, 0);
            return ArticleMemoryCachePutOutcome.RejectedDisabled;
        }

        var proveStart = IndexCommittedProbe.MarkPut();
        IndexCommittedProbe.EnterPutProof();
        bool invalid;
        try
        {
            invalid = record.ParseStatus != ArticleParseStatus.CanonicalV1
                || record.ArtSize is < 1 or > ArticleResourceLimits.MaxArticleBytes
                || record.ArtSize != record.ArtData.Length
                || !ArticleStorageIntegrity.TryProve(
                    record.ArtData.Span,
                    record.ArtId,
                    record.ArtHash,
                    record.ArtSize);
        }
        finally
        {
            IndexCommittedProbe.ExitProof();
        }

        IndexCommittedProbe.AddPutProve(proveStart);
        if (invalid)
        {
            IndexCommittedProbe.NoteOutcome(ArticleMemoryCachePutOutcome.RejectedInvalid, record.ArtSize, 0);
            return ArticleMemoryCachePutOutcome.RejectedInvalid;
        }

        // Oversized relative to capacity: never evict to make room.
        if (record.ArtSize > _maxBytes)
        {
            IndexCommittedProbe.NoteOutcome(ArticleMemoryCachePutOutcome.RejectedOversized, record.ArtSize, 0);
            return ArticleMemoryCachePutOutcome.RejectedOversized;
        }

        var lockStart = IndexCommittedProbe.MarkPut();
        lock (_gate)
        {
            IndexCommittedProbe.AddLock(lockStart);
            var lookupStart = IndexCommittedProbe.MarkPut();
            var found = _map.TryGetValue(record.ArtId, out var existing);
            IndexCommittedProbe.AddLookup(lookupStart);
            if (found)
            {
                if (existing!.Record.ArtHash == record.ArtHash
                    && existing.Record.ArtSize == record.ArtSize
                    && existing.Record.ArtData.Span.SequenceEqual(record.ArtData.Span))
                {
                    MoveToHeadUnlocked(existing);
                    IndexCommittedProbe.NoteOutcome(ArticleMemoryCachePutOutcome.IdempotentNoOp, record.ArtSize, _currentBytes);
                    return ArticleMemoryCachePutOutcome.IdempotentNoOp;
                }

                IndexCommittedProbe.NoteOutcome(ArticleMemoryCachePutOutcome.RejectedConflict, record.ArtSize, _currentBytes);
                return ArticleMemoryCachePutOutcome.RejectedConflict;
            }

            while (_currentBytes + record.ArtSize > _maxBytes && _tail is not null)
            {
                if (IndexCommittedProbe.IsEnabled)
                {
                    var victim = _tail;
                    if (victim is null)
                    {
                        break;
                    }

                    var evictId = victim.Record.ArtId;
                    var evictSize = victim.Record.ArtSize;
                    var evictStart = IndexCommittedProbe.MarkPut();
                    EvictTailUnlocked();
                    IndexCommittedProbe.AddEvict(evictStart);
                    IndexCommittedProbe.Forget(evictId, evictSize);
                }
                else
                {
                    EvictTailUnlocked();
                }
            }

            // Capacity check after eviction (should always succeed given ArtSize <= MaxBytes).
            if (_currentBytes + record.ArtSize > _maxBytes)
            {
                IndexCommittedProbe.NoteOutcome(ArticleMemoryCachePutOutcome.RejectedOversized, record.ArtSize, _currentBytes);
                return ArticleMemoryCachePutOutcome.RejectedOversized;
            }

            ArticleRecord owned;
            if (adoptOwned)
            {
                owned = record;
            }
            else
            {
                var cloneStart = IndexCommittedProbe.MarkPut();
                owned = CloneOwned(record);
                IndexCommittedProbe.AddClone(cloneStart, owned.ArtSize);
            }
            var dictStart = IndexCommittedProbe.MarkPut();
            var node = new Node(owned);
            _map[record.ArtId] = node;
            IndexCommittedProbe.AddDict(dictStart);
            var bytesStart = IndexCommittedProbe.MarkPut();
            _currentBytes += record.ArtSize;
            IndexCommittedProbe.AddBytesAccounting(bytesStart);
            var linkStart = IndexCommittedProbe.MarkPut();
            InsertAtHeadUnlocked(node);
            IndexCommittedProbe.AddLink(linkStart);
            IndexCommittedProbe.Remember(record.ArtId);
            IndexCommittedProbe.NoteOutcome(ArticleMemoryCachePutOutcome.Inserted, record.ArtSize, _currentBytes);
            return ArticleMemoryCachePutOutcome.Inserted;
        }
    }

    /// <inheritdoc />
    public bool Remove(ArticleId artId)
    {
        lock (_gate)
        {
            if (!_map.Remove(artId, out var node))
            {
                return false;
            }

            UnlinkUnlocked(node);
            _currentBytes -= node.Record.ArtSize;
            if (_currentBytes < 0)
            {
                _currentBytes = 0;
            }

            return true;
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _head = null;
            _tail = null;
            _currentBytes = 0;
        }
    }

    private static ArticleRecord CloneOwned(in ArticleRecord source)
    {
        var copy = source.ArtData.ToArray();
        return new ArticleRecord(
            source.ArtId,
            source.ArtHash,
            source.ArtType,
            source.ArtLines,
            source.CanonicalUtc,
            source.ParseStatus,
            copy,
            source.Fields);
    }

    private void EvictTailUnlocked()
    {
        var victim = _tail;
        if (victim is null)
        {
            return;
        }

        _ = _map.Remove(victim.Record.ArtId);
        UnlinkUnlocked(victim);
        _currentBytes -= victim.Record.ArtSize;
        if (_currentBytes < 0)
        {
            _currentBytes = 0;
        }
    }

    private void MoveToHeadUnlocked(Node node)
    {
        if (ReferenceEquals(_head, node))
        {
            return;
        }

        UnlinkUnlocked(node);
        InsertAtHeadUnlocked(node);
    }

    private void InsertAtHeadUnlocked(Node node)
    {
        node.Prev = null;
        node.Next = _head;
        if (_head is not null)
        {
            _head.Prev = node;
        }

        _head = node;
        _tail ??= node;
    }

    private void UnlinkUnlocked(Node node)
    {
        if (node.Prev is not null)
        {
            node.Prev.Next = node.Next;
        }
        else if (ReferenceEquals(_head, node))
        {
            _head = node.Next;
        }

        if (node.Next is not null)
        {
            node.Next.Prev = node.Prev;
        }
        else if (ReferenceEquals(_tail, node))
        {
            _tail = node.Prev;
        }

        node.Prev = null;
        node.Next = null;
    }

    private sealed class Node(ArticleRecord record)
    {
        public ArticleRecord Record { get; } = record;

        public Node? Prev { get; set; }

        public Node? Next { get; set; }
    }
}
