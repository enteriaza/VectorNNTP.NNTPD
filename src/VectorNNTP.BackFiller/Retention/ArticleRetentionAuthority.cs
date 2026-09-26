using VectorNNTP.BackFiller.Configuration;

namespace VectorNNTP.BackFiller.Retention;

/// <summary>
/// In-memory retention authority. One process-wide owner of retained article lifetime.
/// </summary>
public sealed class ArticleRetentionAuthority : IArticleRetentionAuthority, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RetainedEntry> _byMessageId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _messageIdByMd5 = new(StringComparer.Ordinal);
    private readonly LinkedList<RetainedEntry> _insertionOrder = [];
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly long _maximumBytes;
    private readonly TimeSpan _ttl;
    private readonly string _fqdn;
    private readonly int _bindPort;
    private long _retainedBytes;
    private int _physicalCount;
    private long _nextGeneration;
    private bool _admissionClosed;
    private bool _disposed;

    /// <summary>Creates the authority from validated runtime options.</summary>
    public ArticleRetentionAuthority(
        BackFillerRuntimeOptions runtime,
        TimeProvider time,
        ILogger<ArticleRetentionAuthority> logger)
        : this(
            (runtime ?? throw new ArgumentNullException(nameof(runtime))).ArticleRetention,
            runtime.Fqdn,
            runtime.BindPort,
            time,
            logger)
    {
    }

    /// <summary>Creates the authority with an explicit retention policy (tests and host).</summary>
    public ArticleRetentionAuthority(
        BackFillerArticleRetentionRuntimeOptions retention,
        string fqdn,
        int bindPort,
        TimeProvider time,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(retention);
        ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        if (retention.MaximumRetainedPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), "MaximumRetainedPayloadBytes must be greater than zero.");
        }

        if (retention.RetentionTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), "RetentionTtl must be greater than zero.");
        }

        if (retention.SweepInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), "SweepInterval must be greater than zero.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(bindPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bindPort, 65535);

        _maximumBytes = retention.MaximumRetainedPayloadBytes;
        _ttl = retention.RetentionTtl;
        SweepInterval = retention.SweepInterval;
        _fqdn = fqdn;
        _bindPort = bindPort;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public TimeSpan SweepInterval { get; }

    /// <inheritdoc />
    public long RetainedPayloadBytes
    {
        get
        {
            lock (_gate)
            {
                return _retainedBytes;
            }
        }
    }

    /// <inheritdoc />
    public int RetainedCount
    {
        get
        {
            lock (_gate)
            {
                return _physicalCount;
            }
        }
    }

    /// <inheritdoc />
    public ArticleRetentionResult Retain(string messageId, byte[] payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.Length <= 0)
        {
            ArticleRetentionLogMessages.Rejected(_logger, ArticleRetentionKind.InvalidPayload, null, 0, RetainedPayloadBytes);
            return new ArticleRetentionResult(ArticleRetentionKind.InvalidPayload, null, null, RetainedPayloadBytes, 0);
        }

        if (payload.Length > _maximumBytes)
        {
            ArticleRetentionLogMessages.Rejected(
                _logger,
                ArticleRetentionKind.PayloadExceedsCapacity,
                null,
                payload.Length,
                RetainedPayloadBytes);
            return new ArticleRetentionResult(
                ArticleRetentionKind.PayloadExceedsCapacity,
                null,
                null,
                RetainedPayloadBytes,
                0);
        }

        var identity = ArticleIdentity.FromExactMessageId(messageId);
        var now = _time.GetUtcNow();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_admissionClosed)
            {
                return RejectLocked(ArticleRetentionKind.ShuttingDown, identity, payload.Length, 0);
            }

            if (_byMessageId.TryGetValue(messageId, out var existing))
            {
                if (IsExpiredLocked(existing, now))
                {
                    RemoveExpiredLocked(existing);
                }
                else
                {
                    return new ArticleRetentionResult(
                        ArticleRetentionKind.AlreadyPresent,
                        existing.Identity,
                        existing.CacheUri,
                        _retainedBytes,
                        0);
                }
            }

            if (_messageIdByMd5.TryGetValue(identity.Md5Hex, out var colliding)
                && !string.Equals(colliding, messageId, StringComparison.Ordinal))
            {
                if (_byMessageId.TryGetValue(colliding, out var collidingEntry) && IsExpiredLocked(collidingEntry, now))
                {
                    RemoveExpiredLocked(collidingEntry);
                }
                else
                {
                    return RejectLocked(ArticleRetentionKind.Md5Collision, identity, payload.Length, 0);
                }
            }

            var released = ReclaimForAdmissionLocked(payload.Length, now);
            if (_retainedBytes + payload.Length > _maximumBytes)
            {
                return RejectLocked(ArticleRetentionKind.CapacityUnavailable, identity, payload.Length, released);
            }

            var entry = new RetainedEntry(
                identity,
                CacheArticleUri.Create(_fqdn, _bindPort, identity),
                payload,
                now,
                now + _ttl,
                Interlocked.Increment(ref _nextGeneration));
            entry.Node = _insertionOrder.AddLast(entry);
            _byMessageId[messageId] = entry;
            _messageIdByMd5[identity.Md5Hex] = messageId;
            AddBytesLocked(payload.Length);
            _physicalCount++;
            ArticleRetentionLogMessages.Retained(_logger, identity.Md5Hex, payload.Length, _retainedBytes);
            return new ArticleRetentionResult(
                ArticleRetentionKind.Retained,
                identity,
                entry.CacheUri,
                _retainedBytes,
                released);
        }
    }

    /// <inheritdoc />
    public ArticleLookupResult TryGetByMessageId(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        lock (_gate)
        {
            return _byMessageId.TryGetValue(messageId, out var entry)
                ? TryLeaseLocked(entry)
                : ArticleLookupResult.Missing();
        }
    }

    /// <inheritdoc />
    public ArticleLookupResult TryGetByMd5(string md5Hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(md5Hex);
        lock (_gate)
        {
            return _messageIdByMd5.TryGetValue(md5Hex, out var messageId)
                && _byMessageId.TryGetValue(messageId, out var entry)
                ? TryLeaseLocked(entry)
                : ArticleLookupResult.Missing();
        }
    }

    /// <inheritdoc />
    public long SweepExpired()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return 0;
            }

            var released = ExpireEligibleLocked(_time.GetUtcNow());
            if (released > 0)
            {
                ArticleRetentionLogMessages.Swept(_logger, released, _retainedBytes);
            }

            return released;
        }
    }

    /// <inheritdoc />
    public void BeginShutdown()
    {
        lock (_gate)
        {
            _admissionClosed = true;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            _admissionClosed = true;
            foreach (var entry in _byMessageId.Values.ToArray())
            {
                UnindexLocked(entry);
                DisposePhysicallyLocked(entry);
            }

            _insertionOrder.Clear();
        }

        return ValueTask.CompletedTask;
    }

    private ArticleLookupResult TryLeaseLocked(RetainedEntry entry)
    {
        if (IsExpiredLocked(entry, _time.GetUtcNow()))
        {
            RemoveExpiredLocked(entry);
            return ArticleLookupResult.Expired();
        }

        if (!entry.TryAcquire())
        {
            return ArticleLookupResult.Missing();
        }

        return ArticleLookupResult.Found(new ArticleLookupLease(
            entry.Identity,
            entry.CacheUri,
            entry.Payload,
            entry.InsertedUtc,
            entry.ExpiresUtc,
            entry.Generation,
            () => ReleaseLease(entry)));
    }

    private void ReleaseLease(RetainedEntry entry)
    {
        lock (_gate)
        {
            if (!entry.TryRelease())
            {
                return;
            }

            if (entry.IsLogicallyRemoved)
            {
                DisposePhysicallyLocked(entry);
            }
        }
    }

    private long ReclaimForAdmissionLocked(int requiredBytes, DateTimeOffset now)
    {
        var released = ExpireEligibleLocked(now);
        while (_retainedBytes + requiredBytes > _maximumBytes)
        {
            var node = _insertionOrder.First;
            if (node is null)
            {
                break;
            }

            var candidate = node.Value;
            UnindexLocked(candidate);
            if (DisposePhysicallyLocked(candidate))
            {
                released += candidate.PayloadBytes;
            }
        }

        return released;
    }

    private long ExpireEligibleLocked(DateTimeOffset now)
    {
        var released = 0L;
        var node = _insertionOrder.First;
        while (node is not null)
        {
            var entry = node.Value;
            var next = node.Next;
            if (entry.ExpiresUtc > now)
            {
                break;
            }

            if (RemoveExpiredLocked(entry))
            {
                released += entry.WasPhysicallyDisposedThisRemoval ? entry.PayloadBytes : 0;
            }

            node = next;
        }

        return released;
    }

    private bool RemoveExpiredLocked(RetainedEntry entry)
    {
        UnindexLocked(entry);
        var disposed = DisposePhysicallyLocked(entry);
        entry.WasPhysicallyDisposedThisRemoval = disposed;
        return true;
    }

    private bool IsExpiredLocked(RetainedEntry entry, DateTimeOffset now) =>
        entry.ExpiresUtc <= now;

    private void UnindexLocked(RetainedEntry entry)
    {
        if (!entry.TryMarkLogicallyRemoved())
        {
            return;
        }

        _byMessageId.Remove(entry.Identity.MessageId);
        _messageIdByMd5.Remove(entry.Identity.Md5Hex);
        if (entry.Node is not null)
        {
            _insertionOrder.Remove(entry.Node);
            entry.Node = null;
        }
    }

    private bool DisposePhysicallyLocked(RetainedEntry entry)
    {
        if (!entry.TryDisposePhysically())
        {
            return false;
        }

        SubtractBytesLocked(entry.PayloadBytes);
        _physicalCount = Math.Max(0, _physicalCount - 1);
        return true;
    }

    private void AddBytesLocked(int bytes)
    {
        _retainedBytes += bytes;
    }

    private void SubtractBytesLocked(int bytes)
    {
        _retainedBytes = Math.Max(0, _retainedBytes - bytes);
    }

    private ArticleRetentionResult RejectLocked(
        ArticleRetentionKind kind,
        ArticleIdentity identity,
        int payloadBytes,
        long released)
    {
        ArticleRetentionLogMessages.Rejected(_logger, kind, identity.Md5Hex, payloadBytes, _retainedBytes);
        return new ArticleRetentionResult(kind, identity, null, _retainedBytes, released);
    }

    private sealed class RetainedEntry
    {
        private byte[]? _payload;
        private int _readers;
        private int _logicallyRemoved;
        private int _physicallyDisposed;

        internal RetainedEntry(
            ArticleIdentity identity,
            string cacheUri,
            byte[] payload,
            DateTimeOffset insertedUtc,
            DateTimeOffset expiresUtc,
            long generation)
        {
            Identity = identity;
            CacheUri = cacheUri;
            _payload = payload;
            PayloadBytes = payload.Length;
            InsertedUtc = insertedUtc;
            ExpiresUtc = expiresUtc;
            Generation = generation;
        }

        internal ArticleIdentity Identity { get; }

        internal string CacheUri { get; }

        internal int PayloadBytes { get; }

        internal DateTimeOffset InsertedUtc { get; }

        internal DateTimeOffset ExpiresUtc { get; }

        internal long Generation { get; }

        internal LinkedListNode<RetainedEntry>? Node { get; set; }

        internal bool IsLogicallyRemoved => Volatile.Read(ref _logicallyRemoved) == 1;

        internal bool WasPhysicallyDisposedThisRemoval { get; set; }

        internal ReadOnlyMemory<byte> Payload
        {
            get
            {
                var payload = _payload ?? throw new ObjectDisposedException(nameof(RetainedEntry));
                return payload;
            }
        }

        internal bool TryAcquire()
        {
            if (_logicallyRemoved == 1 || _physicallyDisposed == 1 || _payload is null)
            {
                return false;
            }

            _readers++;
            return true;
        }

        internal bool TryRelease()
        {
            if (_readers <= 0)
            {
                return false;
            }

            _readers--;
            return true;
        }

        internal bool TryMarkLogicallyRemoved() =>
            Interlocked.CompareExchange(ref _logicallyRemoved, 1, 0) == 0;

        internal bool TryDisposePhysically()
        {
            if (_physicallyDisposed == 1 || _readers > 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _physicallyDisposed, 1, 0) != 0)
            {
                return false;
            }

            _payload = null;
            return true;
        }
    }
}
