using System.Runtime.InteropServices;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;

namespace VectorNNTP.BackFiller.Retention;

/// <summary>
/// In-memory retention authority. One process-wide owner of retained CanonicalV1 article lifetime.
/// The sole retained byte representation is <see cref="ArticleRecord.ArtData"/>.
/// Openable RequestIds pin an entry against FIFO capacity reclaim until OPEN, cancel, TTL, or dispose.
/// </summary>
internal sealed class ArticleRetentionAuthority : IArticleRetentionAuthority, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RetainedEntry> _byMessageId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _messageIdByArticleId = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, RetainedEntry> _byRequestId = new();
    private readonly LinkedList<RetainedEntry> _insertionOrder = [];
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly long _maximumBytes;
    private readonly TimeSpan _ttl;
    private readonly int _maxOpenableRequestIdsPerArticle;
    private readonly string _fqdn;
    private readonly int _bindPort;
    private long _retainedBytes;
    private int _physicalCount;
    private long _nextGeneration;
    private bool _admissionClosed;
    private bool _disposed;

    /// <summary>Creates the authority from validated runtime options.</summary>
    internal ArticleRetentionAuthority(
        BackFillerRuntimeOptions runtime,
        TimeProvider time,
        ILogger<ArticleRetentionAuthority> logger)
        : this(
            (runtime ?? throw new ArgumentNullException(nameof(runtime))).ArticleRetention,
            runtime.Fqdn,
            runtime.BindPortTls,
            time,
            logger)
    {
    }

    /// <summary>Creates the authority with an explicit retention policy (tests and host).</summary>
    internal ArticleRetentionAuthority(
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

        if (retention.MaxOpenableRequestIdsPerArticle is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retention),
                "MaxOpenableRequestIdsPerArticle must be between 1 and 256.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(bindPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bindPort, 65535);

        _maximumBytes = retention.MaximumRetainedPayloadBytes;
        _ttl = retention.RetentionTtl;
        SweepInterval = retention.SweepInterval;
        _maxOpenableRequestIdsPerArticle = retention.MaxOpenableRequestIdsPerArticle;
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
    public ArticleRetentionResult RetainCanonical(
        string messageId,
        Guid requestId,
        ArticleRecord record,
        NntpArticleHeaderName selectedDateHeaderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        if (requestId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(requestId));
        }

        if (record.ParseStatus != ArticleParseStatus.CanonicalV1
            || record.ArtSize <= 0
            || !TryGetOwnedArtData(in record, out var artData))
        {
            ArticleRetentionLogMessages.Rejected(
                _logger,
                ArticleRetentionKind.InvalidPayload,
                null,
                record.ArtSize,
                RetainedPayloadBytes);
            return new ArticleRetentionResult(
                ArticleRetentionKind.InvalidPayload,
                null,
                null,
                null,
                RetainedPayloadBytes,
                0);
        }

        if (artData.Length > _maximumBytes)
        {
            ArticleRetentionLogMessages.Rejected(
                _logger,
                ArticleRetentionKind.PayloadExceedsCapacity,
                null,
                artData.Length,
                RetainedPayloadBytes);
            return new ArticleRetentionResult(
                ArticleRetentionKind.PayloadExceedsCapacity,
                null,
                null,
                null,
                RetainedPayloadBytes,
                0);
        }

        var identity = ArticleIdentity.From(messageId, record.ArtId);
        var now = _time.GetUtcNow();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_admissionClosed)
            {
                return RejectLocked(ArticleRetentionKind.ShuttingDown, identity, artData.Length, 0);
            }

            if (_byRequestId.TryGetValue(requestId, out var requestOwner)
                && !string.Equals(requestOwner.Identity.MessageId, messageId, StringComparison.Ordinal))
            {
                return RejectLocked(ArticleRetentionKind.ArticleIdCollision, identity, artData.Length, 0);
            }

            if (_byMessageId.TryGetValue(messageId, out var existing))
            {
                if (IsExpiredLocked(existing, now))
                {
                    RemoveExpiredLocked(existing);
                }
                else
                {
                    // First-wins ArtData: keep the retained ArticleRecord; add RequestId as another
                    // openable capability. Never revoke an existing RequestId to admit a newer one.
                    if (!AttachRequestIdLocked(existing, requestId))
                    {
                        return RejectLocked(
                            ArticleRetentionKind.OpenableRequestIdLimitExceeded,
                            existing.Identity,
                            artData.Length,
                            0);
                    }

                    return new ArticleRetentionResult(
                        ArticleRetentionKind.AlreadyPresent,
                        existing.Identity,
                        existing.Fqdn,
                        existing.VatpPort,
                        _retainedBytes,
                        0);
                }
            }

            if (_messageIdByArticleId.TryGetValue(identity.ArticleIdHex, out var colliding)
                && !string.Equals(colliding, messageId, StringComparison.Ordinal))
            {
                if (_byMessageId.TryGetValue(colliding, out var collidingEntry) && IsExpiredLocked(collidingEntry, now))
                {
                    RemoveExpiredLocked(collidingEntry);
                }
                else
                {
                    return RejectLocked(ArticleRetentionKind.ArticleIdCollision, identity, artData.Length, 0);
                }
            }

            var released = ReclaimForAdmissionLocked(artData.Length, now);
            if (_retainedBytes + artData.Length > _maximumBytes)
            {
                return RejectLocked(ArticleRetentionKind.CapacityUnavailable, identity, artData.Length, released);
            }

            var entry = new RetainedEntry(
                identity,
                _fqdn,
                _bindPort,
                record,
                selectedDateHeaderName,
                now,
                now + _ttl,
                Interlocked.Increment(ref _nextGeneration));
            entry.Node = _insertionOrder.AddLast(entry);
            _byMessageId[messageId] = entry;
            _messageIdByArticleId[identity.ArticleIdHex] = messageId;
            AttachRequestIdLocked(entry, requestId);
            AddBytesLocked(artData.Length);
            _physicalCount++;
            ArticleRetentionLogMessages.Retained(_logger, identity.ArticleIdHex, artData.Length, _retainedBytes);
            return new ArticleRetentionResult(
                ArticleRetentionKind.Retained,
                identity,
                entry.Fqdn,
                entry.VatpPort,
                _retainedBytes,
                released);
        }
    }

    /// <inheritdoc />
    public VatpOpenResult TryOpenTransfer(
        Guid requestId,
        ArticleId expectedArticleId,
        Func<int, bool>? tryReserveOutboundBytes = null)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(requestId));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_byRequestId.TryGetValue(requestId, out var entry))
            {
                return VatpOpenResult.Rejected();
            }

            if (IsExpiredLocked(entry, _time.GetUtcNow()))
            {
                RemoveExpiredLocked(entry);
                return VatpOpenResult.Rejected();
            }

            if (entry.Record is not { } record
                || entry.SelectedDateHeaderName is not { } selectedDate)
            {
                return VatpOpenResult.Rejected();
            }

            if (record.ArtId != expectedArticleId)
            {
                // Wrong ArticleId must not consume RequestId.
                return VatpOpenResult.Rejected();
            }

            // Acquire the transfer reader lease before reservation so a failed reservation can
            // roll back without consuming RequestId. Only this RequestId is detached after both succeed.
            if (!entry.TryAcquire())
            {
                return VatpOpenResult.Rejected();
            }

            if (tryReserveOutboundBytes is not null && !tryReserveOutboundBytes(record.ArtSize))
            {
                _ = entry.TryRelease();
                return VatpOpenResult.Rejected();
            }

            _ = DetachRequestIdLocked(entry, requestId);
            var lease = new VatpTransferLease(
                record,
                selectedDate,
                entry.Identity,
                () => ReleaseLease(entry));
            return VatpOpenResult.Opened(lease);
        }
    }

    /// <inheritdoc />
    public bool TryCancelPendingRequest(Guid requestId)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(requestId));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_byRequestId.TryGetValue(requestId, out var entry))
            {
                return false;
            }

            return DetachRequestIdLocked(entry, requestId);
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

    /// <summary>
    /// Reclaims space for a new admission: TTL-expired entries first, then oldest FIFO
    /// entries that have no openable RequestIds. Entries with openable Success capabilities
    /// are skipped so published RequestIds remain OPEN-able until OPEN, cancel, TTL, or shutdown.
    /// </summary>
    private long ReclaimForAdmissionLocked(int requiredBytes, DateTimeOffset now)
    {
        var released = ExpireEligibleLocked(now);
        while (_retainedBytes + requiredBytes > _maximumBytes)
        {
            var candidate = FindOldestFifoReclaimableLocked();
            if (candidate is null)
            {
                break;
            }

            UnindexLocked(candidate);
            if (DisposePhysicallyLocked(candidate))
            {
                released += candidate.PayloadBytes;
            }
        }

        return released;
    }

    /// <summary>
    /// Oldest insertion-order entry with zero openable RequestIds (unpinned).
    /// Entries that still carry Success capabilities are not FIFO-reclaimable.
    /// </summary>
    private RetainedEntry? FindOldestFifoReclaimableLocked()
    {
        for (var node = _insertionOrder.First; node is not null; node = node.Next)
        {
            if (node.Value.OpenableRequestIdCount == 0)
            {
                return node.Value;
            }
        }

        return null;
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
        _messageIdByArticleId.Remove(entry.Identity.ArticleIdHex);
        ClearAllRequestIdsLocked(entry);
        if (entry.Node is not null)
        {
            _insertionOrder.Remove(entry.Node);
            entry.Node = null;
        }
    }

    /// <summary>
    /// Adds <paramref name="requestId"/> as an openable capability for <paramref name="entry"/>.
    /// Idempotent when already attached. Returns <see langword="false"/> when the bound is full.
    /// </summary>
    private bool AttachRequestIdLocked(RetainedEntry entry, Guid requestId)
    {
        if (!entry.TryAddOpenableRequestId(requestId, _maxOpenableRequestIdsPerArticle))
        {
            return false;
        }

        _byRequestId[requestId] = entry;
        return true;
    }

    /// <summary>Removes one openable RequestId from the entry and reverse map.</summary>
    private bool DetachRequestIdLocked(RetainedEntry entry, Guid requestId)
    {
        if (!entry.RemoveOpenableRequestId(requestId))
        {
            return false;
        }

        if (_byRequestId.TryGetValue(requestId, out var owner) && ReferenceEquals(owner, entry))
        {
            _byRequestId.Remove(requestId);
        }

        return true;
    }

    /// <summary>Removes every openable RequestId for an entry (TTL / unindex / dispose).</summary>
    private void ClearAllRequestIdsLocked(RetainedEntry entry)
    {
        foreach (var requestId in entry.TakeAllOpenableRequestIds())
        {
            if (_byRequestId.TryGetValue(requestId, out var owner) && ReferenceEquals(owner, entry))
            {
                _byRequestId.Remove(requestId);
            }
        }
    }

    private static bool TryGetOwnedArtData(in ArticleRecord record, out byte[] payload)
    {
        if (MemoryMarshal.TryGetArray(record.ArtData, out ArraySegment<byte> segment)
            && segment.Array is not null
            && segment.Offset == 0
            && segment.Count == record.ArtSize
            && segment.Count > 0)
        {
            payload = segment.Array;
            return true;
        }

        payload = null!;
        return false;
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
        ArticleRetentionLogMessages.Rejected(_logger, kind, identity.ArticleIdHex, payloadBytes, _retainedBytes);
        return new ArticleRetentionResult(kind, identity, null, null, _retainedBytes, released);
    }

    private sealed class RetainedEntry
    {
        private ArticleRecord? _record;
        private NntpArticleHeaderName? _selectedDateHeaderName;
        private readonly HashSet<Guid> _openableRequestIds = [];
        private int _readers;
        private int _logicallyRemoved;
        private int _physicallyDisposed;

        internal RetainedEntry(
            ArticleIdentity identity,
            string fqdn,
            int vatpPort,
            ArticleRecord record,
            NntpArticleHeaderName selectedDateHeaderName,
            DateTimeOffset insertedUtc,
            DateTimeOffset expiresUtc,
            long generation)
        {
            Identity = identity;
            Fqdn = fqdn;
            VatpPort = vatpPort;
            _record = record;
            _selectedDateHeaderName = selectedDateHeaderName;
            PayloadBytes = record.ArtSize;
            InsertedUtc = insertedUtc;
            ExpiresUtc = expiresUtc;
            Generation = generation;
        }

        internal ArticleIdentity Identity { get; }

        internal string Fqdn { get; }

        internal int VatpPort { get; }

        internal int PayloadBytes { get; }

        internal DateTimeOffset InsertedUtc { get; }

        internal DateTimeOffset ExpiresUtc { get; }

        internal long Generation { get; }

        internal LinkedListNode<RetainedEntry>? Node { get; set; }

        internal int OpenableRequestIdCount => _openableRequestIds.Count;

        internal ArticleRecord? Record => _record;

        internal NntpArticleHeaderName? SelectedDateHeaderName => _selectedDateHeaderName;

        internal bool IsLogicallyRemoved => Volatile.Read(ref _logicallyRemoved) == 1;

        internal bool WasPhysicallyDisposedThisRemoval { get; set; }

        internal bool TryAddOpenableRequestId(Guid requestId, int maximum)
        {
            if (_openableRequestIds.Contains(requestId))
            {
                return true;
            }

            if (_openableRequestIds.Count >= maximum)
            {
                return false;
            }

            return _openableRequestIds.Add(requestId);
        }

        internal bool RemoveOpenableRequestId(Guid requestId) => _openableRequestIds.Remove(requestId);

        internal Guid[] TakeAllOpenableRequestIds()
        {
            if (_openableRequestIds.Count == 0)
            {
                return [];
            }

            var ids = new Guid[_openableRequestIds.Count];
            _openableRequestIds.CopyTo(ids);
            _openableRequestIds.Clear();
            return ids;
        }

        internal bool TryAcquire()
        {
            if (_logicallyRemoved == 1 || _physicallyDisposed == 1 || _record is null)
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

            _record = null;
            _selectedDateHeaderName = null;
            return true;
        }
    }
}
