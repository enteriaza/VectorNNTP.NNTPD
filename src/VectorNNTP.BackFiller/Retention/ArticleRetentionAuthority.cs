using System.Runtime.InteropServices;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;

namespace VectorNNTP.BackFiller.Retention
{
    /// <summary>
    /// In-memory retention authority. One process-wide owner of retained CanonicalV1 article lifetime.
    /// The sole retained byte representation is <see cref="ArticleRecord.ArtData"/>.
    /// Openable RequestIds pin an entry against FIFO capacity reclaim until OPEN, cancel, TTL, or dispose.
    /// </summary>
    /// <remarks>
    /// Index, counter, and admission mutations run under <see cref="_gate"/>.
    /// <see cref="SweepInterval"/> is fixed at construction.
    /// ArtData is referenced, not copied. An outstanding VATP reader does not pin FIFO reclaim;
    /// only <see cref="RetainedEntry.OpenableRequestIdCount"/> does. Physical release waits until
    /// that reader count is zero. One OPEN or cancel unpins the entry only when no openable
    /// RequestId remains. TTL removal and <see cref="DisposeAsync"/> drop every RequestId on the entry.
    /// <see cref="BeginShutdown"/> rejects new <see cref="RetainCanonical"/> calls and leaves existing
    /// entries openable.
    /// </remarks>
    internal sealed class ArticleRetentionAuthority : IArticleRetentionAuthority, IAsyncDisposable
    {
        /// <summary>
        /// Non-recursive lock for the indexes, byte counters, admission flag, and reader acquire/release.
        /// No caller enters it again on the same call stack. <see cref="SweepInterval"/> does not use it.
        /// </summary>
        private readonly Lock _gate = new();

        /// <summary>Exact Message-ID to its live entry. Comparison is ordinal. At most one entry per Message-ID.</summary>
        private readonly Dictionary<string, RetainedEntry> _byMessageId = new(StringComparer.Ordinal);

        /// <summary>
        /// ArticleId hex to the Message-ID that owns it. Used to reject a different Message-ID that
        /// presents the same ArticleId. Removed when the entry is unindexed.
        /// </summary>
        private readonly Dictionary<string, string> _messageIdByArticleId = new(StringComparer.Ordinal);

        /// <summary>
        /// Openable RequestId to the entry that holds it. A RequestId maps to at most one entry.
        /// Removed when that RequestId is consumed, cancelled, or cleared by unindex or dispose.
        /// </summary>
        private readonly Dictionary<Guid, RetainedEntry> _byRequestId = [];

        /// <summary>
        /// Admission order, oldest at <see cref="LinkedList{T}.First"/>. FIFO reclaim and TTL sweep
        /// walk from that end. <see cref="RetainedEntry.ExpiresUtc"/> is the admission time plus
        /// <see cref="_ttl"/>, so this order matches expiry order when <see cref="_time"/> does not
        /// move backward. <see cref="ExpireEligibleLocked"/> then stops at the first unexpired node.
        /// </summary>
        private readonly LinkedList<RetainedEntry> _insertionOrder = [];

        /// <summary>Clock for admission time, <see cref="RetainedEntry.ExpiresUtc"/>, and sweep. Not <see cref="DateTimeOffset.UtcNow"/>.</summary>
        private readonly TimeProvider _time;

        /// <summary>Retention event logger. Article bodies are not written.</summary>
        private readonly ILogger _logger;

        /// <summary>
        /// Maximum owned ArtData bytes. A single buffer larger than this is
        /// <see cref="ArticleRetentionKind.PayloadExceedsCapacity"/> and does not trigger reclaim.
        /// </summary>
        private readonly long _maximumBytes;

        /// <summary>Added to the admission timestamp to produce <see cref="RetainedEntry.ExpiresUtc"/>. Greater than zero.</summary>
        private readonly TimeSpan _ttl;

        /// <summary>Per-entry cap on openable RequestIds, from 1 through 256 inclusive.</summary>
        private readonly int _maxOpenableRequestIdsPerArticle;

        /// <summary>VATP host copied onto each newly retained entry. Existing entries keep the value from their admission.</summary>
        private readonly string _fqdn;

        /// <summary>TLS VATP port copied onto each newly retained entry. Existing entries keep the value from their admission.</summary>
        private readonly int _bindPort;

        /// <summary>
        /// Sum of <see cref="RetainedEntry.PayloadBytes"/> for entries not yet physically disposed,
        /// including logically removed entries that still have a reader. Subtracted only under
        /// <see cref="_gate"/> and never below zero.
        /// </summary>
        private long _retainedBytes;

        /// <summary>
        /// Entries not yet physically disposed. Logical removal does not decrement this until
        /// <see cref="DisposePhysicallyLocked"/> succeeds. Never below zero.
        /// </summary>
        private int _physicalCount;

        /// <summary>
        /// Monotonic source for <see cref="RetainedEntry.Generation"/>. Incremented when a new entry
        /// is constructed. No OPEN, sweep, reclaim, or cancel decision reads it.
        /// </summary>
        private long _nextGeneration;

        /// <summary>
        /// When <see langword="true"/>, <see cref="RetainCanonical"/> returns
        /// <see cref="ArticleRetentionKind.ShuttingDown"/>. Set by <see cref="BeginShutdown"/> and
        /// <see cref="DisposeAsync"/>. OPEN, cancel, and sweep do not consult it.
        /// </summary>
        private bool _admissionClosed;

        /// <summary>
        /// When <see langword="true"/>, the locked sections of <see cref="RetainCanonical"/>,
        /// <see cref="TryOpenTransfer"/>, and <see cref="TryCancelPendingRequest"/> throw
        /// <see cref="ObjectDisposedException"/>. <see cref="ArticleRetentionKind.InvalidPayload"/> and
        /// <see cref="ArticleRetentionKind.PayloadExceedsCapacity"/> are returned before that check.
        /// <see cref="SweepExpired"/> returns zero. <see cref="DisposeAsync"/> is idempotent.
        /// </summary>
        private bool _disposed;

        /// <summary>Creates the authority from validated runtime options.</summary>
        /// <param name="runtime">
        /// Host snapshot. <see cref="BackFillerRuntimeOptions.ArticleRetention"/>,
        /// <see cref="BackFillerRuntimeOptions.Fqdn"/>, and <see cref="BackFillerRuntimeOptions.BindPortTls"/>
        /// are forwarded to the explicit constructor.
        /// </param>
        /// <param name="time">Clock for admission timestamps, TTL, and sweep.</param>
        /// <param name="logger">Retention logger. Article bodies are not written.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="runtime"/>, <paramref name="time"/>, or <paramref name="logger"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException"><see cref="BackFillerRuntimeOptions.Fqdn"/> is null or whitespace.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The retention policy or <see cref="BackFillerRuntimeOptions.BindPortTls"/> fails the explicit constructor.
        /// </exception>
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
        /// <param name="retention">Capacity, TTL, sweep interval, and per-article RequestId bound.</param>
        /// <param name="fqdn">VATP host stored on every newly retained entry. Not null or whitespace.</param>
        /// <param name="bindPort">TLS VATP port stored on every newly retained entry. Inclusive range 1 through 65535.</param>
        /// <param name="time">Clock for admission timestamps, TTL, and sweep.</param>
        /// <param name="logger">Retention logger. Article bodies are not written.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="retention"/>, <paramref name="time"/>, or <paramref name="logger"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException"><paramref name="fqdn"/> is null or whitespace.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <see cref="BackFillerArticleRetentionRuntimeOptions.MaximumRetainedPayloadBytes"/> is not positive,
        /// <see cref="BackFillerArticleRetentionRuntimeOptions.RetentionTtl"/> or
        /// <see cref="BackFillerArticleRetentionRuntimeOptions.SweepInterval"/> is not positive,
        /// <see cref="BackFillerArticleRetentionRuntimeOptions.MaxOpenableRequestIdsPerArticle"/> is outside 1 through 256,
        /// or <paramref name="bindPort"/> is outside 1 through 65535.
        /// </exception>
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

        /// <summary>Gets currently owned retained ArtData bytes, read under <see cref="_gate"/>.</summary>
        /// <remarks>Includes bytes of logically removed entries that still have a VATP reader.</remarks>
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

        /// <summary>
        /// Gets the number of entries not yet physically disposed, read under <see cref="_gate"/>.
        /// </summary>
        /// <remarks>Logical removal does not decrement the count while a reader still holds ArtData.</remarks>
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

        /// <summary>
        /// Retains <paramref name="record"/> or attaches <paramref name="requestId"/> to the live Message-ID entry.
        /// </summary>
        /// <param name="messageId">Exact Message-ID. Not normalized. Null or whitespace is rejected.</param>
        /// <param name="requestId">ArticleWork Success capability. <see cref="Guid.Empty"/> is rejected.</param>
        /// <param name="record">
        /// Candidate record. Stored only when <see cref="ArticleRetentionKind.Retained"/> is returned.
        /// <see cref="ArticleParseStatus.CanonicalV1"/>, a positive <see cref="ArticleRecord.ArtSize"/>,
        /// and ArtData that is the record's whole array at offset zero are required.
        /// </param>
        /// <param name="selectedDateHeaderName">
        /// Date-family header name stored on a new entry. An <see cref="ArticleRetentionKind.AlreadyPresent"/>
        /// result keeps the header name from the first admission.
        /// </param>
        /// <param name="endpointFqdn">
        /// FQDN captured with this article's shared snapshot. Null or white space uses the FQDN supplied at construction.
        /// </param>
        /// <returns>
        /// The admission kind, the identity when one was computed, the VATP endpoint when the article
        /// remains available, the owned byte total, and bytes physically released during this attempt.
        /// </returns>
        /// <exception cref="ArgumentException"><paramref name="messageId"/> is null or whitespace.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="requestId"/> is <see cref="Guid.Empty"/>.</exception>
        /// <exception cref="ObjectDisposedException">Thrown from the locked section when <see cref="_disposed"/> is set.</exception>
        /// <remarks>
        /// <see cref="ArticleRetentionKind.InvalidPayload"/> and
        /// <see cref="ArticleRetentionKind.PayloadExceedsCapacity"/> are decided before the lock and do not
        /// throw <see cref="ObjectDisposedException"/>. Those results do not mutate indexes.
        /// An exact Message-ID hit is resolved before ArticleId collision. An expired hit is removed and
        /// admission continues with <paramref name="record"/>; a live hit keeps the first
        /// <see cref="ArticleRecord"/> and does not store <paramref name="record"/>.
        /// A RequestId already mapped to a different Message-ID is
        /// <see cref="ArticleRetentionKind.ArticleIdCollision"/>.
        /// An expired ArticleId collision is removed so the new Message-ID can be indexed.
        /// FIFO reclaim then runs only for a new entry. The incoming array is not copied.
        /// </remarks>
        public ArticleRetentionResult RetainCanonical(
            string messageId,
            Guid requestId,
            ArticleRecord record,
            NntpArticleHeaderName selectedDateHeaderName,
            string? endpointFqdn = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
            var admittedFqdn = string.IsNullOrWhiteSpace(endpointFqdn) ? _fqdn : endpointFqdn;
            // ThrowIfEqual changes both the message and ActualValue, so replacing the existing ArgumentOutOfRangeException would be a
            // behavioral change for no functional benefit
#pragma warning disable CA1512 // Use ArgumentOutOfRangeException throw helper
            if (requestId == Guid.Empty)
            {
                throw new ArgumentOutOfRangeException(nameof(requestId));
            }
#pragma warning restore CA1512 // Use ArgumentOutOfRangeException throw helper

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
                    admittedFqdn,
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

        /// <summary>
        /// Resolves a VATP OPEN. RequestId selects the entry; ArticleId is verified before consumption.
        /// </summary>
        /// <param name="requestId">Openable ArticleWork Success capability. <see cref="Guid.Empty"/> is rejected.</param>
        /// <param name="expectedArticleId">ArticleId from the OPEN frame. A mismatch does not consume <paramref name="requestId"/>.</param>
        /// <param name="tryReserveOutboundBytes">
        /// Optional session admission for <see cref="ArticleRecord.ArtSize"/>. Invoked under <see cref="_gate"/>
        /// after the reader is acquired and before <paramref name="requestId"/> is detached.
        /// <see langword="null"/> skips reservation. <see langword="false"/> rolls the reader back and leaves
        /// the RequestId openable.
        /// </param>
        /// <returns>
        /// <see cref="VatpOpenKind.Opened"/> with a <see cref="VatpTransferLease"/> after only
        /// <paramref name="requestId"/> is detached, or <see cref="VatpOpenKind.Rejected"/> with no lease.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="requestId"/> is <see cref="Guid.Empty"/>.</exception>
        /// <exception cref="ObjectDisposedException">The authority has been disposed.</exception>
        /// <remarks>
        /// An unknown RequestId is rejected without mutation. An expired entry is removed, including its
        /// other RequestIds, and the OPEN is rejected. A missing record or date header, a failed reader
        /// acquire, or a failed reservation rejects without detaching <paramref name="requestId"/>.
        /// Wrong ArticleId does not call <paramref name="tryReserveOutboundBytes"/>.
        /// </remarks>
        public VatpOpenResult TryOpenTransfer(
            Guid requestId,
            ArticleId expectedArticleId,
            Func<int, bool>? tryReserveOutboundBytes = null)
        {
            // ThrowIfEqual changes both the message and ActualValue, so replacing the existing ArgumentOutOfRangeException would be a
            // behavioral change for no functional benefit
#pragma warning disable CA1512 // Use ArgumentOutOfRangeException throw helper
            if (requestId == Guid.Empty)
            {
                throw new ArgumentOutOfRangeException(nameof(requestId));
            }
#pragma warning restore CA1512 // Use ArgumentOutOfRangeException throw helper

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

        /// <summary>Detaches one openable RequestId. Does not unindex the Message-ID or release ArtData.</summary>
        /// <param name="requestId">RequestId to remove. <see cref="Guid.Empty"/> is rejected.</param>
        /// <returns>
        /// <see langword="true"/> when the RequestId was removed from its entry.
        /// <see langword="false"/> when it is not currently openable.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="requestId"/> is <see cref="Guid.Empty"/>.</exception>
        /// <exception cref="ObjectDisposedException">The authority has been disposed.</exception>
        /// <remarks>
        /// Other RequestIds on the same entry stay openable. The entry becomes FIFO-reclaimable only
        /// when the last openable RequestId is gone. An active reader is not affected.
        /// </remarks>
        public bool TryCancelPendingRequest(Guid requestId)
        {
            // ThrowIfEqual changes both the message and ActualValue, so replacing the existing ArgumentOutOfRangeException would be a
            // behavioral change for no functional benefit
#pragma warning disable CA1512 // Use ArgumentOutOfRangeException throw helper
            if (requestId == Guid.Empty)
            {
                throw new ArgumentOutOfRangeException(nameof(requestId));
            }
#pragma warning restore CA1512 // Use ArgumentOutOfRangeException throw helper

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

        /// <summary>Reclaims TTL-expired entries in insertion order.</summary>
        /// <returns>
        /// Payload bytes physically disposed by this pass. Zero when the authority is disposed or when
        /// expiry only logically removes entries that still have readers.
        /// </returns>
        /// <remarks>
        /// Does not throw <see cref="ObjectDisposedException"/>. Does not close admission.
        /// Writes <see cref="ArticleRetentionLogMessages.Swept"/> only when the returned total is positive.
        /// The walk stops at the first entry with <see cref="RetainedEntry.ExpiresUtc"/> still in the future.
        /// That is complete when admission timestamps are non-decreasing, because every entry uses the same
        /// <see cref="_ttl"/>. A clock that moves backward can leave a later, already-expired node in the list.
        /// </remarks>
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

        /// <summary>Stops new <see cref="RetainCanonical"/> admissions by setting <see cref="_admissionClosed"/>.</summary>
        /// <remarks>
        /// Idempotent. Does not throw when <see cref="_disposed"/> is already set. Does not cancel
        /// RequestIds, dispose entries, or reject <see cref="TryOpenTransfer"/>.
        /// </remarks>
        public void BeginShutdown()
        {
            lock (_gate)
            {
                _admissionClosed = true;
            }
        }

        /// <summary>
        /// Marks the authority disposed, closes admission, and unindexes every retained entry.
        /// </summary>
        /// <returns>A completed task. The cleanup runs synchronously under <see cref="_gate"/>.</returns>
        /// <remarks>
        /// Idempotent. A second call does not throw. Entries with a VATP reader stay physically alive
        /// until <see cref="ReleaseLease"/> drops the last reader; their bytes remain in
        /// <see cref="_retainedBytes"/> until then. Abandoned leases are not waited for.
        /// After this method, locked <see cref="RetainCanonical"/>, <see cref="TryOpenTransfer"/>, and
        /// <see cref="TryCancelPendingRequest"/> calls throw <see cref="ObjectDisposedException"/>,
        /// and <see cref="SweepExpired"/> returns zero.
        /// </remarks>
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

        /// <summary>
        /// Drops one VATP reader. When the entry is already logically removed and no readers remain,
        /// finishes physical disposal.
        /// </summary>
        /// <param name="entry">Entry whose reader count the lease acquired.</param>
        /// <remarks>
        /// Invoked from <see cref="VatpTransferLease.Dispose"/> under <see cref="_gate"/>.
        /// A release that finds no outstanding reader returns without disposal.
        /// </remarks>
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
        /// are skipped so those RequestIds stay OPEN-able until OPEN, cancel, TTL, or dispose.
        /// </summary>
        /// <param name="requiredBytes">ArtData length of the entry about to be admitted.</param>
        /// <param name="now">Admission clock reading used for TTL expiry.</param>
        /// <returns>
        /// Bytes physically disposed by TTL expiry and by FIFO reclaim. An unpinned entry that still
        /// has a reader is unindexed but contributes nothing until that reader releases it.
        /// </returns>
        /// <remarks>
        /// Called under <see cref="_gate"/>. Stops when <see cref="_retainedBytes"/> can hold
        /// <paramref name="requiredBytes"/> or no unpinned FIFO candidate remains. An outstanding
        /// reader is not a pin. <see cref="BeginShutdown"/> is not a reclaim path.
        /// </remarks>
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
        /// <returns>
        /// The oldest unpinned entry, or <see langword="null"/> when every indexed entry still has
        /// an openable RequestId. Does not remove the entry. An active reader does not keep it pinned.
        /// </returns>
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

        /// <summary>
        /// Unindexes every expired entry from the oldest insertion-order node until the first
        /// <see cref="RetainedEntry.ExpiresUtc"/> still in the future.
        /// </summary>
        /// <param name="now">Clock reading compared with <see cref="RetainedEntry.ExpiresUtc"/>.</param>
        /// <returns>
        /// Bytes physically disposed. A logically removed entry that still has a reader adds zero
        /// for this call.
        /// </returns>
        /// <remarks>
        /// The early stop is valid when <see cref="_time"/> does not move backward between admissions.
        /// <see cref="RetainedEntry.ExpiresUtc"/> is the admission timestamp plus the same <see cref="_ttl"/>,
        /// so a later node then expires later. A backward clock step can leave a later expired node unvisited.
        /// </remarks>
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

        /// <summary>Unindexes <paramref name="entry"/> and attempts physical disposal.</summary>
        /// <param name="entry">Expired entry. May already be logically removed.</param>
        /// <returns>
        /// Always <see langword="true"/> after the attempt. Byte credit is
        /// <see cref="RetainedEntry.WasPhysicallyDisposedThisRemoval"/>, not this boolean.
        /// </returns>
        private bool RemoveExpiredLocked(RetainedEntry entry)
        {
            UnindexLocked(entry);
            var disposed = DisposePhysicallyLocked(entry);
            entry.WasPhysicallyDisposedThisRemoval = disposed;
            return true;
        }

        /// <summary>Reports whether <paramref name="entry"/> is due at <paramref name="now"/>.</summary>
        /// <param name="entry">Indexed or previously indexed entry.</param>
        /// <param name="now">Clock reading. Equal to <see cref="RetainedEntry.ExpiresUtc"/> is expired.</param>
        /// <returns><see langword="true"/> when <see cref="RetainedEntry.ExpiresUtc"/> is less than or equal to <paramref name="now"/>.</returns>
        private static bool IsExpiredLocked(RetainedEntry entry, DateTimeOffset now) =>
            entry.ExpiresUtc <= now;

        /// <summary>
        /// Removes <paramref name="entry"/> from the Message-ID, ArticleId, RequestId, and FIFO indexes once.
        /// </summary>
        /// <param name="entry">Entry to drop from lookup. ArtData is left in place when readers remain.</param>
        /// <remarks>
        /// Called under <see cref="_gate"/>. A second call returns at
        /// <see cref="RetainedEntry.TryMarkLogicallyRemoved"/> and does not clear indexes again.
        /// </remarks>
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
        /// <param name="entry">Live entry that will own the capability.</param>
        /// <param name="requestId">RequestId to attach. Not removed from any other entry by this method.</param>
        /// <returns>
        /// <see langword="false"/> when <paramref name="entry"/> is already at
        /// <see cref="_maxOpenableRequestIdsPerArticle"/> and does not already contain
        /// <paramref name="requestId"/>. Otherwise updates <see cref="_byRequestId"/> and returns
        /// <see langword="true"/>.
        /// </returns>
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
        /// <param name="entry">Entry expected to own <paramref name="requestId"/>.</param>
        /// <param name="requestId">Capability to detach.</param>
        /// <returns>
        /// <see langword="false"/> when <paramref name="entry"/> does not contain <paramref name="requestId"/>.
        /// Otherwise returns <see langword="true"/> after removing it from the entry, and also removes it from
        /// <see cref="_byRequestId"/> when that map still points at <paramref name="entry"/>.
        /// </returns>
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
        /// <param name="entry">Entry whose capability set is snapshotted and cleared.</param>
        /// <remarks>
        /// Each cleared id is removed from <see cref="_byRequestId"/> only when the map still points at
        /// <paramref name="entry"/>.
        /// </remarks>
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

        /// <summary>
        /// Accepts ArtData only when it is the record's single backing array, starting at offset zero,
        /// with length equal to <see cref="ArticleRecord.ArtSize"/> and greater than zero.
        /// </summary>
        /// <param name="record">Candidate record. Not copied.</param>
        /// <param name="payload">The backing array when the method returns <see langword="true"/>; otherwise <see langword="null"/>.</param>
        /// <returns>
        /// <see langword="true"/> when retention can take that array by reference.
        /// A slice, a short view, or an empty buffer returns <see langword="false"/>.
        /// </returns>
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

        /// <summary>
        /// Drops <paramref name="entry"/>'s ArtData reference when no reader remains, and subtracts its bytes.
        /// </summary>
        /// <param name="entry">Logically removed entry, or an entry being disposed with no readers.</param>
        /// <returns>
        /// <see langword="false"/> when a reader is outstanding or the entry was already physically disposed.
        /// <see langword="true"/> after <see cref="_retainedBytes"/> and <see cref="_physicalCount"/> are reduced.
        /// </returns>
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

        /// <summary>Adds <paramref name="bytes"/> to <see cref="_retainedBytes"/>. Called under <see cref="_gate"/>.</summary>
        /// <param name="bytes">ArtData length of an entry just indexed. Not checked against <see cref="_maximumBytes"/> here.</param>
        private void AddBytesLocked(int bytes)
        {
            _retainedBytes += bytes;
        }

        /// <summary>
        /// Subtracts <paramref name="bytes"/> from <see cref="_retainedBytes"/>, floored at zero.
        /// Called under <see cref="_gate"/>.
        /// </summary>
        /// <param name="bytes"><see cref="RetainedEntry.PayloadBytes"/> of an entry just physically disposed.</param>
        private void SubtractBytesLocked(int bytes)
        {
            _retainedBytes = Math.Max(0, _retainedBytes - bytes);
        }

        /// <summary>
        /// Logs a locked-section rejection and returns a result with no VATP endpoint.
        /// Does not change indexes or the incoming ArtData ownership.
        /// </summary>
        /// <param name="kind">Rejection classification.</param>
        /// <param name="identity">Identity computed for this attempt. Included even though admission failed.</param>
        /// <param name="payloadBytes">ArtData length that was refused.</param>
        /// <param name="released">Bytes physically released by reclaim or expiry earlier in this attempt.</param>
        /// <returns>
        /// A result whose endpoint fields are <see langword="null"/> and whose retained total is the
        /// current <see cref="_retainedBytes"/>.
        /// </returns>
        private ArticleRetentionResult RejectLocked(
            ArticleRetentionKind kind,
            ArticleIdentity identity,
            int payloadBytes,
            long released)
        {
            ArticleRetentionLogMessages.Rejected(_logger, kind, identity.ArticleIdHex, payloadBytes, _retainedBytes);
            return new ArticleRetentionResult(kind, identity, null, null, _retainedBytes, released);
        }

        /// <summary>
        /// One retained Message-ID. ArtData stays reachable until physical disposal, which waits for
        /// <see cref="_readers"/> to reach zero. Callers mutate this type only while holding
        /// <see cref="_gate"/>; it does not take its own lock.
        /// </summary>
        private sealed class RetainedEntry
        {
            /// <summary>
            /// CanonicalV1 record whose ArtData is the retained buffer. <see langword="null"/> after
            /// <see cref="TryDisposePhysically"/> succeeds.
            /// </summary>
            private ArticleRecord? _record;

            /// <summary>
            /// Date-family header name for VATP META. Cleared with <see cref="_record"/> on physical disposal.
            /// </summary>
            private NntpArticleHeaderName? _selectedDateHeaderName;

            /// <summary>
            /// RequestIds that may still OPEN. A non-empty set pins the entry against FIFO reclaim.
            /// Membership is idempotent. Order is not significant.
            /// </summary>
            private readonly HashSet<Guid> _openableRequestIds = [];

            /// <summary>
            /// Outstanding <see cref="VatpTransferLease"/> readers. Physical disposal fails while this
            /// is positive. Changed only under <see cref="_gate"/>.
            /// </summary>
            private int _readers;

            /// <summary>
            /// 0 until the first successful <see cref="TryMarkLogicallyRemoved"/>; 1 afterward.
            /// Logical removal drops indexes and does not by itself drop ArtData.
            /// </summary>
            private int _logicallyRemoved;

            /// <summary>
            /// 0 until <see cref="TryDisposePhysically"/> clears the record; 1 afterward.
            /// A second physical disposal fails.
            /// </summary>
            private int _physicallyDisposed;

            /// <summary>Stamps a new indexed entry. Does not attach a RequestId.</summary>
            /// <param name="identity">Message-ID and ArticleId hex used as index keys.</param>
            /// <param name="fqdn">VATP host copied from the authority at admission.</param>
            /// <param name="vatpPort">TLS VATP port copied from the authority at admission.</param>
            /// <param name="record">CanonicalV1 record retained by reference.</param>
            /// <param name="selectedDateHeaderName">Date-family header name stored for VATP META.</param>
            /// <param name="insertedUtc">Authority clock at admission.</param>
            /// <param name="expiresUtc"><paramref name="insertedUtc"/> plus the authority TTL.</param>
            /// <param name="generation">Value taken from <see cref="_nextGeneration"/>. Not read by later decisions.</param>
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

            /// <summary>Message-ID and ArticleId hex. These are the index keys for the entry's lifetime.</summary>
            internal ArticleIdentity Identity { get; }

            /// <summary>VATP host fixed at admission. Not updated if the authority endpoint later changes.</summary>
            internal string Fqdn { get; }

            /// <summary>TLS VATP port fixed at admission.</summary>
            internal int VatpPort { get; }

            /// <summary>
            /// <see cref="ArticleRecord.ArtSize"/> captured at admission. Remains after
            /// <see cref="_record"/> is cleared so byte accounting can subtract the same length.
            /// </summary>
            internal int PayloadBytes { get; }

            /// <summary>Authority clock at admission.</summary>
            internal DateTimeOffset InsertedUtc { get; }

            /// <summary>
            /// First instant at which the entry is expired. <see cref="IsExpiredLocked"/> treats equality as expired.
            /// </summary>
            internal DateTimeOffset ExpiresUtc { get; }

            /// <summary>
            /// Admission stamp from <see cref="_nextGeneration"/>. Stored only. Sweep and OPEN do not compare it.
            /// </summary>
            internal long Generation { get; }

            /// <summary>
            /// Link in <see cref="_insertionOrder"/>. <see langword="null"/> after <see cref="UnindexLocked"/> unlinks it.
            /// </summary>
            internal LinkedListNode<RetainedEntry>? Node { get; set; }

            /// <summary>Openable RequestIds still attached. Greater than zero pins the entry against FIFO reclaim.</summary>
            internal int OpenableRequestIdCount => _openableRequestIds.Count;

            /// <summary>Retained record, or <see langword="null"/> after physical disposal.</summary>
            internal ArticleRecord? Record => _record;

            /// <summary>Date-family header name, or <see langword="null"/> after physical disposal.</summary>
            internal NntpArticleHeaderName? SelectedDateHeaderName => _selectedDateHeaderName;

            /// <summary>Volatile read of logical removal. Indexes no longer contain the entry when this is <see langword="true"/>.</summary>
            internal bool IsLogicallyRemoved => Volatile.Read(ref _logicallyRemoved) == 1;

            /// <summary>
            /// Set by <see cref="RemoveExpiredLocked"/> to whether that call physically disposed the entry.
            /// Sweep byte credit uses this flag because <see cref="RemoveExpiredLocked"/> itself always returns <see langword="true"/>.
            /// </summary>
            internal bool WasPhysicallyDisposedThisRemoval { get; set; }

            /// <summary>
            /// Adds <paramref name="requestId"/> unless the set is already at <paramref name="maximum"/>.
            /// </summary>
            /// <param name="requestId">Capability to add. An id already in the set succeeds without growing it.</param>
            /// <param name="maximum">Inclusive cap. Compared with the count before an insert.</param>
            /// <returns>
            /// <see langword="true"/> when the id is present after the call, including when it was already present
            /// at the cap. <see langword="false"/> when the cap rejected a new id. Existing ids are not removed.
            /// </returns>
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

            /// <summary>Removes one openable RequestId from this entry only.</summary>
            /// <param name="requestId">Capability to remove.</param>
            /// <returns><see langword="false"/> when <paramref name="requestId"/> was not in the set.</returns>
            internal bool RemoveOpenableRequestId(Guid requestId) => _openableRequestIds.Remove(requestId);

            /// <summary>Copies every openable RequestId and clears the set.</summary>
            /// <returns>The ids that were openable. Empty when the set was empty. Order is undefined.</returns>
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

            /// <summary>
            /// Takes one VATP reader when the record is still physically present and the entry is not logically removed.
            /// </summary>
            /// <returns>
            /// <see langword="true"/> after <see cref="_readers"/> is incremented.
            /// <see langword="false"/> when the entry is logically removed, physically disposed, or has no record.
            /// Does not consume a RequestId.
            /// </returns>
            internal bool TryAcquire()
            {
                if (_logicallyRemoved == 1 || _physicallyDisposed == 1 || _record is null)
                {
                    return false;
                }

                _readers++;
                return true;
            }

            /// <summary>Drops one VATP reader.</summary>
            /// <returns><see langword="false"/> when <see cref="_readers"/> is already zero. Does not dispose ArtData.</returns>
            internal bool TryRelease()
            {
                if (_readers <= 0)
                {
                    return false;
                }

                _readers--;
                return true;
            }

            /// <summary>Marks the entry logically removed once.</summary>
            /// <returns><see langword="false"/> when it was already logically removed. Does not drop ArtData or readers.</returns>
            internal bool TryMarkLogicallyRemoved() =>
                Interlocked.CompareExchange(ref _logicallyRemoved, 1, 0) == 0;

            /// <summary>
            /// Clears <see cref="_record"/> and <see cref="_selectedDateHeaderName"/> when no reader remains.
            /// </summary>
            /// <returns>
            /// <see langword="false"/> when a reader is outstanding or this entry was already physically disposed.
            /// Does not subtract <see cref="PayloadBytes"/>; <see cref="DisposePhysicallyLocked"/> does that.
            /// </returns>
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
}
