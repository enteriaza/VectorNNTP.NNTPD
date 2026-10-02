namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

/// <summary>Phase 5F.1a: transient pending queue over durable incomplete Accept work.</summary>
public sealed partial class FileArticleStorageEngine
{
    /// <summary>Initial backoff after the first retryable persist failure.</summary>
    private static readonly TimeSpan PersistRetryBaseDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>Cap so maintenance can reclaim between attempts without hot-looping.</summary>
    private static readonly TimeSpan PersistRetryMaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Filesystem failures are retried on the IO path and keep their capacity reservation.
    /// Logical failures stay incomplete, release the process-local reservation, and use
    /// <see cref="ScheduleBlockedPersistRetry"/> so they are not abandoned and are not
    /// mixed with IO retries.
    /// </summary>
    private static bool IsRetryablePersistFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;

    private void EnqueuePersistWorkUnlocked(ulong sequence)
    {
        if (!_pendingSet.Add(sequence))
        {
            return;
        }

        _pendingSequences.Enqueue(sequence);
    }

    private void SignalPersistWorker()
    {
        try
        {
            _ = _workerSignal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Links durable incomplete journal sequences into the transient pending queue (deduped).
    /// No-op while <see cref="SuspendBackgroundPersist"/> is set.
    /// </summary>
    private void EnqueueIncompleteFromJournal()
    {
        if (SuspendBackgroundPersist || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var enqueued = false;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || SuspendBackgroundPersist)
            {
                return;
            }

            foreach (var incomplete in _journal.EnumerateIncomplete())
            {
                var sequence = incomplete.Accept.Sequence;
                if (_persistInFlight.Contains(sequence))
                {
                    continue;
                }

                if (_pendingSet.Add(sequence))
                {
                    _pendingSequences.Enqueue(sequence);
                    enqueued = true;
                }
            }
        }

        if (enqueued)
        {
            SignalPersistWorker();
        }
    }

    /// <summary>
    /// Runs Accept→PhysicalWritten→IndexCommitted for one sequence with single-flight exclusion.
    /// </summary>
    private async Task PersistSequenceExclusiveAsync(
        ulong sequence,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<ulong, List<StoredArticleLocation>>? acceptOnlyCandidates = null)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            bool acquired;
            lock (_gate)
            {
                acquired = _persistInFlight.Add(sequence);
            }

            if (!acquired)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1), _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                if (!IsJournalIncomplete(sequence))
                {
                    return;
                }

                continue;
            }

            var attemptsBefore = 0;
            try
            {
                attemptsBefore = ReadPersistRetryAttempts(sequence);

                var incomplete = _journal.EnumerateIncomplete()
                    .FirstOrDefault(s => s.Accept.Sequence == sequence);
                if (incomplete.Accept is null)
                {
                    ClearPersistRetryAttempts(sequence);
                    return;
                }

                IReadOnlyList<StoredArticleLocation>? candidates = null;
                if (acceptOnlyCandidates is not null
                    && acceptOnlyCandidates.TryGetValue(sequence, out var found))
                {
                    candidates = found;
                }

                await RecoverOneAsync(incomplete, cancellationToken, candidates).ConfigureAwait(false);
                ClearPersistRetryAttempts(sequence);
                if (attemptsBefore > 0)
                {
                    FileArticleStorageEngineLogMessages.PersistRetrySucceeded(
                        _logger,
                        sequence,
                        attemptsBefore);
                }

                return;
            }
            finally
            {
                lock (_gate)
                {
                    _ = _persistInFlight.Remove(sequence);
                }
            }
        }
    }

    private bool IsJournalIncomplete(ulong sequence)
    {
        return _journal.EnumerateIncomplete().Any(s => s.Accept.Sequence == sequence);
    }

    /// <summary>
    /// Drops process-local retry and never-written state for <paramref name="sequence"/>.
    /// Takes only <see cref="_retryStateGate"/>.
    /// </summary>
    internal void ClearPersistRetryAttempts(ulong sequence)
    {
        var waitStart = IndexCommittedProbe.MarkClear();
        lock (_retryStateGate)
        {
            var acquired = IndexCommittedProbe.MarkClear();
            IndexCommittedProbe.AddClearWait(waitStart, acquired);
            var dictStart = IndexCommittedProbe.MarkClear();
            _ = _persistRetryAttempts.Remove(sequence);
            _ = _persistBlockedRetryAttempts.Remove(sequence);
            _ = _acceptWithoutPhysicalBytes.Remove(sequence);
            IndexCommittedProbe.AddClearDict(dictStart);
            IndexCommittedProbe.AddClearHold(acquired);
        }

        IndexCommittedProbe.AddClearAfter(IndexCommittedProbe.MarkClear());
    }

    /// <summary>
    /// True when this process journaled <paramref name="accept"/> and can still prove that
    /// no physical append of that sequence has been attempted. A pending or unreconciled
    /// segment tail, or an Evicted or Invalid index row, keeps discovery.
    /// </summary>
    private bool CanSkipProvenLocationScan(JournalAcceptRecord accept)
    {
        lock (_retryStateGate)
        {
            if (!_acceptWithoutPhysicalBytes.Contains(accept.Sequence))
            {
                return false;
            }
        }

        if (_index.TryGet(accept.ArtId, out var existing)
            && existing.State is ArticleStorageState.Evicted or ArticleStorageState.Invalid)
        {
            return false;
        }

        return !_segments.HasPendingOrUnreconciledTail();
    }

    /// <summary>
    /// Drops the never-written mark so the next attempt searches for physical bytes.
    /// Takes only <see cref="_retryStateGate"/>.
    /// </summary>
    internal void RemoveAcceptWithoutPhysicalBytes(ulong sequence)
    {
        lock (_retryStateGate)
        {
            _ = _acceptWithoutPhysicalBytes.Remove(sequence);
        }
    }

    /// <summary>
    /// Records that this process journaled <paramref name="sequence"/> and has not appended it.
    /// Caller may already hold <see cref="_gate"/>. This method takes only <see cref="_retryStateGate"/>.
    /// </summary>
    private void AddAcceptWithoutPhysicalBytes(ulong sequence)
    {
        lock (_retryStateGate)
        {
            _ = _acceptWithoutPhysicalBytes.Add(sequence);
        }
    }

    private int ReadPersistRetryAttempts(ulong sequence)
    {
        lock (_retryStateGate)
        {
            _ = _persistRetryAttempts.TryGetValue(sequence, out var attempt);
            return attempt;
        }
    }

    private void IncrementPersistRetryAttempts(ulong sequence, out int attempt)
    {
        lock (_retryStateGate)
        {
            _ = _persistRetryAttempts.TryGetValue(sequence, out attempt);
            attempt++;
            _persistRetryAttempts[sequence] = attempt;
        }
    }

    private int ReadPersistBlockedRetryAttempts(ulong sequence)
    {
        lock (_retryStateGate)
        {
            _ = _persistBlockedRetryAttempts.TryGetValue(sequence, out var attempt);
            return attempt;
        }
    }

    private void IncrementPersistBlockedRetryAttempts(ulong sequence, out int attempt)
    {
        lock (_retryStateGate)
        {
            _ = _persistBlockedRetryAttempts.TryGetValue(sequence, out attempt);
            attempt++;
            _persistBlockedRetryAttempts[sequence] = attempt;
        }
    }

    /// <summary>Test seam for the Accept never-written mark. Uses <see cref="_retryStateGate"/> only.</summary>
    internal void TestAddAcceptWithoutPhysicalBytes(ulong sequence) => AddAcceptWithoutPhysicalBytes(sequence);

    /// <summary>Test seam for never-written membership. Uses <see cref="_retryStateGate"/> only.</summary>
    internal bool TestContainsAcceptWithoutPhysicalBytes(ulong sequence)
    {
        lock (_retryStateGate)
        {
            return _acceptWithoutPhysicalBytes.Contains(sequence);
        }
    }

    /// <summary>Test seam for one IO-retry increment. Does not schedule a delay.</summary>
    internal int TestIncrementPersistRetryAttempts(ulong sequence)
    {
        IncrementPersistRetryAttempts(sequence, out var attempt);
        return attempt;
    }

    /// <summary>Test seam for one blocked-retry increment. Does not schedule a delay.</summary>
    internal int TestIncrementPersistBlockedRetryAttempts(ulong sequence)
    {
        IncrementPersistBlockedRetryAttempts(sequence, out var attempt);
        return attempt;
    }

    /// <summary>Test seam for the current IO-retry count. Uses <see cref="_retryStateGate"/> only.</summary>
    internal int TestReadPersistRetryAttempts(ulong sequence) => ReadPersistRetryAttempts(sequence);

    /// <summary>Test seam for the current blocked-retry count. Uses <see cref="_retryStateGate"/> only.</summary>
    internal int TestReadPersistBlockedRetryAttempts(ulong sequence) => ReadPersistBlockedRetryAttempts(sequence);

    private void SchedulePersistRetry(ulong sequence)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        IncrementPersistRetryAttempts(sequence, out var attempt);

        var delay = TestPersistRetryDelay ?? ComputePersistRetryDelay(attempt);
        _ = Interlocked.Increment(ref _persistRetryScheduledCount);
        FileArticleStorageEngineLogMessages.PersistRetryScheduled(
            _logger,
            sequence,
            attempt,
            delay.TotalMilliseconds);
        _ = PersistRetryAfterDelayAsync(sequence, delay);
    }

    private static TimeSpan ComputePersistRetryDelay(int failureAttempt)
    {
        // failureAttempt is 1-based. 50ms, 100ms, 200ms, … capped at 30s.
        var exponent = Math.Clamp(failureAttempt - 1, 0, 10);
        var ms = PersistRetryBaseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        if (ms >= PersistRetryMaxDelay.TotalMilliseconds)
        {
            return PersistRetryMaxDelay;
        }

        return TimeSpan.FromMilliseconds(ms);
    }

    private async Task PersistRetryAfterDelayAsync(ulong sequence, TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _timeProvider, _workerCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (!IsJournalIncomplete(sequence))
        {
            ClearPersistRetryAttempts(sequence);
            return;
        }

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            EnqueuePersistWorkUnlocked(sequence);
        }

        SignalPersistWorker();
    }

    /// <summary>
    /// Deferred retry for a non-retryable persist failure. Does not publish and does not
    /// remove the journal Accept. Backoff matches IO retry but the counter and log do not.
    /// </summary>
    private void ScheduleBlockedPersistRetry(ulong sequence)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        IncrementPersistBlockedRetryAttempts(sequence, out var attempt);

        var delay = TestPersistRetryDelay ?? ComputePersistRetryDelay(attempt);
        _ = Interlocked.Increment(ref _persistBlockedRetryScheduledCount);
        FileArticleStorageEngineLogMessages.PersistBlockedRetryScheduled(
            _logger,
            sequence,
            attempt,
            delay.TotalMilliseconds);
        _ = PersistRetryAfterDelayAsync(sequence, delay);
    }

    /// <summary>
    /// Links durable incomplete sequences into the worker queue. Tests use this to start one
    /// drain after accepts that were taken while background persist was suspended.
    /// </summary>
    internal void TestEnqueueIncompleteWork()
    {
        if (Volatile.Read(ref _suspendBackgroundPersist) != 0)
        {
            SuspendBackgroundPersist = false;
        }

        EnqueueIncompleteFromJournal();
    }

    /// <summary>
    /// Records one sequence failure from the worker. Retryable filesystem failures keep
    /// reservations. Other failures release only copies that were not written.
    /// </summary>
    private void HandlePersistFailure(ulong sequence, Exception ex)
    {
        FileArticleStorageEngineLogMessages.PersistStageFailed(
            _logger,
            sequence,
            "persist",
            ex);
        if (IsRetryablePersistFailure(ex))
        {
            SchedulePersistRetry(sequence);
            return;
        }

        var releasedUnwrittenSegmentBytes = 0L;
        var releasedUnboundIndexBytes = 0L;
        var retainedWrittenSegmentBytes = 0L;
        var retainedJournalBytes = 0L;
        if (_capacityAdmissionEnabled)
        {
            (releasedUnwrittenSegmentBytes, retainedWrittenSegmentBytes) =
                ReadSegmentLedger(ledger => ledger.SegmentCopyReservationBytes(sequence));
            (releasedUnboundIndexBytes, retainedJournalBytes) = ReadControlLedger(ledger =>
                (ledger.UnboundIndexReservationBytes(sequence),
                    ledger.JournalReservationBytes(sequence)));
        }

        ReleaseUnwrittenSegmentCopies(sequence);
        ReleaseUnboundIndexReservation(sequence);
        FileArticleStorageEngineLogMessages.PersistNonRetryableFailure(
            _logger,
            sequence,
            ex.GetType().Name,
            ex.Message,
            releasedUnwrittenSegmentBytes,
            releasedUnboundIndexBytes,
            retainedWrittenSegmentBytes,
            retainedJournalBytes);
        ScheduleBlockedPersistRetry(sequence);
    }
}

/// <summary>
/// Accept is durable and incomplete, but this attempt cannot reserve capacity or finish.
/// The worker releases any pin and schedules <c>ScheduleBlockedPersistRetry</c>.
/// Not an <see cref="IOException"/>, so it is not classified as a filesystem retry.
/// </summary>
internal sealed class PersistCompletionDeferredException : Exception
{
    /// <summary>Creates a deferred-completion failure.</summary>
    public PersistCompletionDeferredException(string message)
        : base(message)
    {
    }
}
