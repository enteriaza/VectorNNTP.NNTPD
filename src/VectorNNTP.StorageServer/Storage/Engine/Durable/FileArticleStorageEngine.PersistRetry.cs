namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

/// <summary>Phase 5F.1a: transient pending queue over durable incomplete Accept work.</summary>
public sealed partial class FileArticleStorageEngine
{
    /// <summary>Initial backoff after the first retryable persist failure.</summary>
    private static readonly TimeSpan PersistRetryBaseDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>Cap so maintenance can reclaim between attempts without hot-looping.</summary>
    private static readonly TimeSpan PersistRetryMaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Conservatively treats <see cref="IOException"/> and <see cref="UnauthorizedAccessException"/>
    /// as retryable (transient filesystem conditions). Logical fail-closed outcomes
    /// (<see cref="InvalidOperationException"/> for unusable/rejected PhysicalWritten, journal
    /// conflicts handled elsewhere, catalogue/integrity failures) are not requeued.
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
    private async Task PersistSequenceExclusiveAsync(ulong sequence, CancellationToken cancellationToken)
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
                lock (_gate)
                {
                    _ = _persistRetryAttempts.TryGetValue(sequence, out attemptsBefore);
                }

                var incomplete = _journal.EnumerateIncomplete()
                    .FirstOrDefault(s => s.Accept.Sequence == sequence);
                if (incomplete.Accept is null)
                {
                    ClearPersistRetryAttempts(sequence);
                    return;
                }

                await RecoverOneAsync(incomplete, cancellationToken).ConfigureAwait(false);
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

    private void ClearPersistRetryAttempts(ulong sequence)
    {
        lock (_gate)
        {
            _ = _persistRetryAttempts.Remove(sequence);
        }
    }

    private void SchedulePersistRetry(ulong sequence)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        int attempt;
        lock (_gate)
        {
            _ = _persistRetryAttempts.TryGetValue(sequence, out attempt);
            attempt++;
            _persistRetryAttempts[sequence] = attempt;
        }

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
}
