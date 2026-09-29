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
/// TryGet returns a record referencing that owned buffer via <see cref="ArticleRecord.ArtData"/>
/// (<see cref="ReadOnlyMemory{T}"/>); callers must not mutate underlying storage.
/// </para>
/// </remarks>
public sealed class ArticleMemoryCache : IArticleMemoryCache
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
                return false;
            }

            MoveToHeadUnlocked(node);
            record = node.Record;
            return true;
        }
    }

    /// <inheritdoc />
    public ArticleMemoryCachePutOutcome Put(in ArticleRecord record)
    {
        if (_maxBytes == 0)
        {
            return ArticleMemoryCachePutOutcome.RejectedDisabled;
        }

        if (record.ParseStatus != ArticleParseStatus.CanonicalV1
            || record.ArtSize is < 1 or > ArticleResourceLimits.MaxArticleBytes
            || record.ArtSize != record.ArtData.Length
            || !ArticleStorageIntegrity.TryProve(
                record.ArtData.Span,
                record.ArtId,
                record.ArtHash,
                record.ArtSize))
        {
            return ArticleMemoryCachePutOutcome.RejectedInvalid;
        }

        // Oversized relative to capacity: never evict to make room.
        if (record.ArtSize > _maxBytes)
        {
            return ArticleMemoryCachePutOutcome.RejectedOversized;
        }

        lock (_gate)
        {
            if (_map.TryGetValue(record.ArtId, out var existing))
            {
                if (existing.Record.ArtHash == record.ArtHash
                    && existing.Record.ArtSize == record.ArtSize
                    && existing.Record.ArtData.Span.SequenceEqual(record.ArtData.Span))
                {
                    MoveToHeadUnlocked(existing);
                    return ArticleMemoryCachePutOutcome.IdempotentNoOp;
                }

                return ArticleMemoryCachePutOutcome.RejectedConflict;
            }

            while (_currentBytes + record.ArtSize > _maxBytes && _tail is not null)
            {
                EvictTailUnlocked();
            }

            // Capacity check after eviction (should always succeed given ArtSize <= MaxBytes).
            if (_currentBytes + record.ArtSize > _maxBytes)
            {
                return ArticleMemoryCachePutOutcome.RejectedOversized;
            }

            var owned = CloneOwned(record);
            var node = new Node(owned);
            _map[record.ArtId] = node;
            _currentBytes += record.ArtSize;
            InsertAtHeadUnlocked(node);
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
