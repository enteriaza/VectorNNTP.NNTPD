using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Configuration;

namespace VectorNNTP.StorageServer.Storage.Engine.Memory;

/// <summary>
/// Deterministic in-memory engine implementing Model A staged journal and engine-owned recovery.
/// </summary>
public sealed class MemoryArticleStorageEngine : IArticleStorageEngine, IArticleStorageRecovery, IAsyncDisposable
{
    private readonly MemoryArticleJournal _journal;
    private readonly MemorySegmentStore _segments;
    private readonly MemoryArticleIndex _index;
    private readonly MemorySegmentCatalogue _catalogue;
    private readonly MemoryStorageTelemetryLog _telemetry;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Queue<ulong> _pendingSequences = new();
    private readonly SemaphoreSlim _workerSignal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _workerCts = new();
    private readonly Task _worker;
    private int _disposed;
    private int _suspendBackgroundPersist;

    /// <summary>Creates a memory engine with default component fakes.</summary>
    public MemoryArticleStorageEngine(
        ArticleStorageRuntimeOptions options,
        TimeProvider? timeProvider = null)
        : this(
            new MemoryArticleJournal(options),
            new MemorySegmentStore(),
            new MemoryArticleIndex(),
            new MemorySegmentCatalogue(),
            new MemoryStorageTelemetryLog(),
            timeProvider)
    {
    }

    /// <summary>Creates a memory engine with injected fakes (tests).</summary>
    public MemoryArticleStorageEngine(
        MemoryArticleJournal journal,
        MemorySegmentStore segments,
        MemoryArticleIndex index,
        MemorySegmentCatalogue catalogue,
        MemoryStorageTelemetryLog telemetry,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(telemetry);
        _journal = journal;
        _segments = segments;
        _index = index;
        _catalogue = catalogue;
        _telemetry = telemetry;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _worker = Task.Run(() => RunPhysicalWorkerAsync(_workerCts.Token));
    }

    /// <summary>Gets the journal.</summary>
    public MemoryArticleJournal Journal => _journal;

    /// <summary>Gets the segment store.</summary>
    public MemorySegmentStore Segments => _segments;

    /// <summary>Gets the index.</summary>
    public MemoryArticleIndex Index => _index;

    /// <summary>Gets the catalogue.</summary>
    public MemorySegmentCatalogue Catalogue => _catalogue;

    /// <summary>Gets telemetry.</summary>
    public MemoryStorageTelemetryLog Telemetry => _telemetry;

    /// <summary>
    /// When true, Accept does not enqueue background SATA/index work (crash-after-Accept tests).
    /// Recovery via <see cref="RecoverAsync"/> still completes outstanding sequences.
    /// </summary>
    public bool SuspendBackgroundPersist
    {
        get => Volatile.Read(ref _suspendBackgroundPersist) != 0;
        set => Volatile.Write(ref _suspendBackgroundPersist, value ? 1 : 0);
    }

    /// <inheritdoc />
    public StorageWritePressure GetWritePressure() => _journal.Pressure;

    /// <inheritdoc />
    public Task<ArticleAcceptResult> AcceptAsync(
        ArticleRecord record,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (record.ParseStatus != ArticleParseStatus.CanonicalV1 || record.ArtSize <= 0)
        {
            return Task.FromResult(ArticleAcceptResult.RejectedInvalid(record.ArtId, "not-canonical-v1"));
        }

        var artData = record.ArtData;
        if (artData.Length != record.ArtSize
            || !ArticleStorageIntegrity.TryProve(
                artData.Span,
                record.ArtId,
                record.ArtHash,
                record.ArtSize))
        {
            return Task.FromResult(ArticleAcceptResult.RejectedInvalid(record.ArtId, "integrity"));
        }

        JournalAcceptRecord journalRecord;
        lock (_gate)
        {
            if (_index.TryGet(record.ArtId, out var existing)
                && existing.State == ArticleStorageState.Present)
            {
                if (existing.ArtHash == record.ArtHash && existing.ArtSize == record.ArtSize)
                {
                    WriteTelemetry(ArticleAcceptOutcome.Duplicate, record);
                    return Task.FromResult(ArticleAcceptResult.Duplicate(record.ArtId));
                }

                WriteTelemetry(ArticleAcceptOutcome.Conflict, record);
                return Task.FromResult(ArticleAcceptResult.Conflict(record.ArtId));
            }

            if (!_journal.TryAppendNewAccept(
                    record.ArtId,
                    record.ArtHash,
                    record.ArtSize,
                    _timeProvider.GetUtcNow(),
                    artData,
                    out journalRecord!,
                    out var rejectOutcome))
            {
                WriteTelemetry(rejectOutcome, record);
                return Task.FromResult(rejectOutcome switch
                {
                    ArticleAcceptOutcome.Duplicate => ArticleAcceptResult.Duplicate(record.ArtId),
                    ArticleAcceptOutcome.Conflict => ArticleAcceptResult.Conflict(record.ArtId),
                    ArticleAcceptOutcome.RejectedInvalid =>
                        ArticleAcceptResult.RejectedInvalid(record.ArtId, "integrity"),
                    _ => ArticleAcceptResult.RejectedPressure(record.ArtId),
                });
            }

            if (!SuspendBackgroundPersist)
            {
                _pendingSequences.Enqueue(journalRecord.Sequence);
            }
        }

        if (!SuspendBackgroundPersist)
        {
            _ = _workerSignal.Release();
        }

        WriteTelemetry(ArticleAcceptOutcome.Accepted, record);
        return Task.FromResult(ArticleAcceptResult.Accepted(record.ArtId, journalRecord.Sequence));
    }

    /// <inheritdoc />
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        foreach (var incomplete in _journal.EnumerateIncomplete())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RecoverOneAsync(incomplete, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits until no incomplete journal sequences remain.</summary>
    public async Task DrainPendingAsync(CancellationToken cancellationToken)
    {
        while (_journal.EnumerateIncomplete().Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SuspendBackgroundPersist)
            {
                await RecoverAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(1), _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public bool TryRead(ArticleId artId, out ArticleReadResult result)
    {
        var presentBefore = _index.TryGet(artId, out var published)
            && published.State == ArticleStorageState.Present;
        if (TryReadPresent(artId, out result))
        {
            return true;
        }

        if (TryReadOutstandingJournal(artId, out result))
        {
            return true;
        }

        if (presentBefore)
        {
            return false;
        }

        return TryReadPresent(artId, out result);
    }

    /// <summary>Reads a Present memory-segment article.</summary>
    private bool TryReadPresent(ArticleId artId, out ArticleReadResult result)
    {
        result = default;
        if (!_index.TryGet(artId, out var metadata) || metadata.State != ArticleStorageState.Present)
        {
            return false;
        }

        if (!_segments.TryRead(metadata.Location, out var artData))
        {
            _ = TryInvalidate(artId);
            return false;
        }

        if (!ArticleStorageIntegrity.TryProve(
                artData.Span,
                metadata.ArtId,
                metadata.ArtHash,
                metadata.ArtSize))
        {
            _ = TryInvalidate(artId);
            return false;
        }

        // TouchHint enforces its own no-durable-write rule under the index lock.
        _index.TouchHint(artId, _timeProvider.GetUtcNow());

        _ = _index.TryGet(artId, out metadata);
        result = new ArticleReadResult(metadata, artData);
        return true;
    }

    /// <summary>
    /// Serves a proved outstanding Accept when the memory index has not published Present.
    /// </summary>
    /// <remarks>
    /// Proves the copy and returns it only while that Accept is still outstanding.
    /// A Present row that wins the race is read by the caller, not here.
    /// </remarks>
    private bool TryReadOutstandingJournal(ArticleId artId, out ArticleReadResult result)
    {
        result = default;
        if (!_journal.TryGetOutstanding(artId, out var accept)
            || accept.ArtId != artId
            || !accept.TryCopyArtData(out var bytes)
            || bytes is null
            || !ArticleStorageIntegrity.TryProve(bytes, accept.ArtId, accept.ArtHash, accept.ArtSize))
        {
            return false;
        }

        if (!_journal.TryGetOutstanding(artId, out var still)
            || still.Sequence != accept.Sequence
            || still.ArtHash != accept.ArtHash)
        {
            return false;
        }

        result = new ArticleReadResult(
            new StoredArticleMetadata(
                accept.ArtId,
                accept.ArtHash,
                accept.ArtSize,
                new StoredArticleLocation(default, 0, accept.ArtSize),
                ArticleStorageState.Present,
                accept.AcceptedUtc,
                accept.Sequence,
                accept.AcceptedUtc),
            bytes);
        return true;
    }

    /// <inheritdoc />
    public bool TryEvict(ArticleId artId) => TransitionLogicalDeath(artId, ArticleStorageState.Evicted);

    /// <inheritdoc />
    public bool TryInvalidate(ArticleId artId) => TransitionLogicalDeath(artId, ArticleStorageState.Invalid);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _workerCts.CancelAsync().ConfigureAwait(false);
        _ = _workerSignal.Release();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _workerCts.Dispose();
        _workerSignal.Dispose();
    }

    private async Task RecoverOneAsync(JournalIncompleteSequence incomplete, CancellationToken cancellationToken)
    {
        var accept = incomplete.Accept;
        if (incomplete.PhysicalWritten is { } written)
        {
            await CompleteFromPhysicalWrittenAsync(accept, written, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Accept only: never infer prior SATA tails; perform a new append.
        var appender = await _segments.GetActiveAppenderAsync(cancellationToken).ConfigureAwait(false);
        EnsureCatalogueActive(appender.SegmentId);
        var location = await appender.AppendAsync(accept.ArtData, cancellationToken).ConfigureAwait(false);
        var pw = new JournalPhysicalWrittenRecord(1, accept.Sequence, location);
        var pwOutcome = await _journal.AppendPhysicalWrittenAsync(pw, cancellationToken).ConfigureAwait(false);
        if (pwOutcome == JournalAppendOutcome.Conflict)
        {
            throw new InvalidOperationException(
                $"PhysicalWritten conflict for sequence {accept.Sequence} during recovery.");
        }

        await CompleteFromPhysicalWrittenAsync(accept, pw, cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteFromPhysicalWrittenAsync(
        JournalAcceptRecord accept,
        JournalPhysicalWrittenRecord written,
        CancellationToken cancellationToken)
    {
        if (!_segments.TryRead(written.Location, out var artData)
            || !ArticleStorageIntegrity.TryProve(
                artData.Span,
                accept.ArtId,
                accept.ArtHash,
                accept.ArtSize))
        {
            // Physical range unusable: leave outstanding for operator/BackFiller path via Invalid if indexed.
            if (_index.TryGet(accept.ArtId, out _))
            {
                _ = TryInvalidate(accept.ArtId);
            }

            throw new InvalidOperationException(
                $"Physical range for sequence {accept.Sequence} failed integrity proof during recovery.");
        }

        if (_index.TryGet(accept.ArtId, out var existing)
            && (existing.Sequence >= accept.Sequence
                || (existing.State == ArticleStorageState.Present
                    && existing.ArtHash == accept.ArtHash
                    && existing.ArtSize == accept.ArtSize)))
        {
            _ = await _journal
                .AppendIndexCommittedAsync(new JournalIndexCommittedRecord(1, accept.Sequence), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var metadata = new StoredArticleMetadata(
            accept.ArtId,
            accept.ArtHash,
            accept.ArtSize,
            written.Location,
            ArticleStorageState.Present,
            _timeProvider.GetUtcNow(),
            accept.Sequence,
            accept.AcceptedUtc);

        if (!_index.TryCommitPresent(in metadata))
        {
            if (_index.TryGet(accept.ArtId, out var present)
                && present.State == ArticleStorageState.Present
                && present.ArtHash == accept.ArtHash
                && present.ArtSize == accept.ArtSize
                && LocationsEqual(present.Location, written.Location))
            {
                _ = await _journal
                    .AppendIndexCommittedAsync(new JournalIndexCommittedRecord(1, accept.Sequence), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            throw new InvalidOperationException(
                $"Index commit failed for sequence {accept.Sequence} during recovery.");
        }

        _catalogue.ApplyLiveDeadDelta(
            written.Location.SegmentId,
            liveDelta: written.Location.Length,
            deadDelta: 0,
            sizeBytes: written.Location.Offset + written.Location.Length);

        var ic = await _journal
            .AppendIndexCommittedAsync(new JournalIndexCommittedRecord(1, accept.Sequence), cancellationToken)
            .ConfigureAwait(false);
        if (ic is JournalAppendOutcome.Rejected or JournalAppendOutcome.Conflict)
        {
            throw new InvalidOperationException(
                $"IndexCommitted rejected for sequence {accept.Sequence} during recovery.");
        }
    }

    private bool TransitionLogicalDeath(ArticleId artId, ArticleStorageState state)
    {
        if (!_index.TryTransitionPresentOnce(artId, state, _timeProvider.GetUtcNow(), out var transitioned))
        {
            return _index.TryGet(artId, out var existing) && existing.State == state;
        }

        _catalogue.ApplyLiveDeadDelta(
            transitioned.Location.SegmentId,
            liveDelta: -transitioned.ArtSize,
            deadDelta: transitioned.ArtSize,
            sizeBytes: transitioned.Location.Offset + transitioned.ArtSize);
        return true;
    }

    private async Task RunPhysicalWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _workerSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                while (true)
                {
                    ulong sequence;
                    lock (_gate)
                    {
                        if (!_pendingSequences.TryDequeue(out sequence))
                        {
                            break;
                        }
                    }

                    await PersistSequenceAsync(sequence, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task PersistSequenceAsync(ulong sequence, CancellationToken cancellationToken)
    {
        var incomplete = _journal.EnumerateIncomplete().FirstOrDefault(s => s.Accept.Sequence == sequence);
        if (incomplete.Accept is null)
        {
            return;
        }

        if (incomplete.PhysicalWritten is null)
        {
            var appender = await _segments.GetActiveAppenderAsync(cancellationToken).ConfigureAwait(false);
            EnsureCatalogueActive(appender.SegmentId);
            var location = await appender
                .AppendAsync(incomplete.Accept.ArtData, cancellationToken)
                .ConfigureAwait(false);
            var pwOutcome = await _journal
                .AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, sequence, location),
                    cancellationToken)
                .ConfigureAwait(false);
            if (pwOutcome == JournalAppendOutcome.Conflict)
            {
                throw new InvalidOperationException($"PhysicalWritten conflict for sequence {sequence}.");
            }
        }

        incomplete = _journal.EnumerateIncomplete().First(s => s.Accept.Sequence == sequence);
        await CompleteFromPhysicalWrittenAsync(
                incomplete.Accept,
                incomplete.PhysicalWritten!.Value,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void EnsureCatalogueActive(SegmentId segmentId)
    {
        if (_catalogue.TryGet(segmentId, out _))
        {
            return;
        }

        _catalogue.Upsert(
            new SegmentInfo(
                segmentId,
                SegmentState.Active,
                Generation: _catalogue.AllocateGeneration(),
                SizeBytes: 0,
                LiveBytes: 0,
                DeadBytes: 0,
                CreatedUtc: _timeProvider.GetUtcNow(),
                ClosedUtc: null));
    }

    private void WriteTelemetry(ArticleAcceptOutcome outcome, in ArticleRecord record) =>
        _telemetry.Append(
            ArticleStorageIntegrity.EncodeAcceptTelemetryV1(
                outcome,
                record.ArtId,
                record.ArtSize,
                _timeProvider.GetUtcNow()));

    private static bool LocationsEqual(in StoredArticleLocation left, in StoredArticleLocation right) =>
        left.SegmentId.Value == right.SegmentId.Value
        && left.Offset == right.Offset
        && left.Length == right.Length;
}
