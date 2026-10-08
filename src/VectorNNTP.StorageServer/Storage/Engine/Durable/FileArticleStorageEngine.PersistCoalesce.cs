using System.Diagnostics;

namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

public sealed partial class FileArticleStorageEngine
{
    /// <summary>
    /// Upper bound on how long the persistence worker waits for more already-ACKed sequences
    /// before it runs the existing persistence batch. Zero restores immediate drain.
    /// The wait never runs before Accept returns.
    /// </summary>
    internal TimeSpan PersistCoalesceMaxDelay
    {
        get => TimeSpan.FromMicroseconds(Volatile.Read(ref _persistCoalesceMaxDelayMicroseconds));
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            Volatile.Write(ref _persistCoalesceMaxDelayMicroseconds, (long)value.TotalMicroseconds);
        }
    }

    /// <summary>Maximum already-ACKed articles in one persistence batch. Further sequences stay queued.</summary>
    internal int PersistCoalesceMaxArticles
    {
        get => Volatile.Read(ref _persistCoalesceMaxArticles);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            Volatile.Write(ref _persistCoalesceMaxArticles, value);
        }
    }

    /// <summary>
    /// Maximum summed ArtSize in one persistence batch.
    /// A single article larger than the bound is still taken alone.
    /// </summary>
    internal long PersistCoalesceMaxBytes
    {
        get => Volatile.Read(ref _persistCoalesceMaxBytes);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            Volatile.Write(ref _persistCoalesceMaxBytes, value);
        }
    }

    /// <summary>
    /// Replaces the coalesce wait. Tests only. The production path is null.
    /// When set, the delegate is the entire remaining wait: after it returns, the worker
    /// takes whatever is queued.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task>? TestPersistCoalesceDelay { get; set; }

    /// <summary>Default post-ACK coalesce wait. One caller gathered about 23 articles at this delay.</summary>
    internal static readonly TimeSpan DefaultPersistCoalesceDelay = TimeSpan.FromMilliseconds(40);

    /// <summary>Default article cap for one persistence batch.</summary>
    internal const int DefaultPersistCoalesceMaxArticles = 32;

    /// <summary>
    /// Default ArtSize cap for one persistence batch.
    /// Matches the one-caller batch that formed at <see cref="DefaultPersistCoalesceDelay"/>
    /// and stays under the 128 MiB journal hard limit.
    /// </summary>
    internal const long DefaultPersistCoalesceMaxBytes = 16L * 1024 * 1024;

    private long _persistCoalesceMaxDelayMicroseconds = (long)DefaultPersistCoalesceDelay.TotalMicroseconds;
    private int _persistCoalesceMaxArticles = DefaultPersistCoalesceMaxArticles;
    private long _persistCoalesceMaxBytes = DefaultPersistCoalesceMaxBytes;

    /// <summary>
    /// Takes a bounded batch of already-ACKed sequences.
    /// A full cap is taken immediately. A short queue waits at most <see cref="PersistCoalesceMaxDelay"/>,
    /// and wakes early when a later Accept fills the cap.
    /// </summary>
    private async Task<List<ulong>> TakeCoalescedBatchAsync(CancellationToken cancellationToken)
    {
        var delay = PersistCoalesceMaxDelay;
        var maxArticles = PersistCoalesceMaxArticles;
        var maxBytes = PersistCoalesceMaxBytes;
        var waitStart = Stopwatch.GetTimestamp();
        var deadline = delay <= TimeSpan.Zero
            ? waitStart
            : waitStart + (long)(delay.TotalSeconds * Stopwatch.Frequency);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<ulong>? ready = null;
            lock (_gate)
            {
                if (_pendingSequences.Count == 0)
                {
                    return [];
                }

                var due = delay <= TimeSpan.Zero
                    || PendingMeetsCapUnlocked(maxArticles, maxBytes)
                    || Stopwatch.GetTimestamp() >= deadline;
                if (due)
                {
                    ready = DequeueCappedUnlocked(maxArticles, maxBytes, waitStart);
                }
            }

            if (ready is not null)
            {
                return ready;
            }

            var remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks < 0)
            {
                remainingTicks = 0;
            }

            var remaining = TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency);
            var custom = TestPersistCoalesceDelay;
            if (custom is not null)
            {
                await custom(remaining, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    return _pendingSequences.Count == 0
                        ? []
                        : DequeueCappedUnlocked(maxArticles, maxBytes, waitStart);
                }
            }

            await _workerSignal.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>True when the queued handoff already fills the article or byte cap.</summary>
    private bool PendingMeetsCapUnlocked(int maxArticles, long maxBytes)
    {
        if (_pendingSequences.Count >= maxArticles)
        {
            return true;
        }

        long bytes = 0;
        foreach (var pending in _pendingSequences)
        {
            bytes += pending.ArtSize;
            if (bytes >= maxBytes)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Removes a prefix of the handoff queue up to the article and byte caps.</summary>
    private List<ulong> DequeueCappedUnlocked(int maxArticles, long maxBytes, long waitStart)
    {
        var batch = new List<ulong>();
        long bytes = 0;
        while (_pendingSequences.TryPeek(out var pending))
        {
            if (batch.Count > 0
                && (batch.Count >= maxArticles || (pending.ArtSize > 0 && bytes + pending.ArtSize > maxBytes)))
            {
                break;
            }

            _ = _pendingSequences.Dequeue();
            _ = _pendingSet.Remove(pending.Sequence);
            if (!_persistInFlight.Add(pending.Sequence))
            {
                continue;
            }

            batch.Add(pending.Sequence);
            bytes += pending.ArtSize;
        }

        RememberBatchWaitUnlocked(waitStart);
        var observedArticles = Volatile.Read(ref _persistBatchMaxArticles);
        if (batch.Count > observedArticles)
        {
            Volatile.Write(ref _persistBatchMaxArticles, batch.Count);
        }

        var observedBytes = Volatile.Read(ref _persistBatchMaxBytes);
        if (bytes > observedBytes)
        {
            Volatile.Write(ref _persistBatchMaxBytes, bytes);
        }

        return batch;
    }

    /// <summary>Records the coalesce wait for the batch about to be returned. Caller holds <see cref="_gate"/>.</summary>
    private void RememberBatchWaitUnlocked(long waitStart)
    {
        if (waitStart == 0)
        {
            return;
        }

        var microseconds = (Stopwatch.GetTimestamp() - waitStart) * 1_000_000d / Stopwatch.Frequency;
        if (microseconds < 0)
        {
            microseconds = 0;
        }

        Volatile.Write(ref _lastPersistBatchWaitMicroseconds, (long)microseconds);
    }

    /// <summary>One already-ACKed journal sequence waiting for the persistence worker.</summary>
    /// <param name="Sequence">Journal sequence.</param>
    /// <param name="ArtSize">ArtSize counted toward the byte cap. Zero is used when the retry path does not have it.</param>
    private readonly record struct PendingPersistWork(ulong Sequence, int ArtSize);
}
