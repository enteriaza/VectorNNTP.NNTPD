using System.Diagnostics;

namespace VectorNNTP.TftpSmoke.Transfer;

/// <summary>
/// Opt-in aggregated timers for one benchmark process. Counts are summed across overlapping transfers,
/// so a section total may exceed wall-clock time. Divide by the measured interval to get the average
/// number of transfers inside that section. These are direct <see cref="Stopwatch"/> durations, not samples.
/// </summary>
public sealed class TransferDiagnostics
{
    private static readonly double[] AckWaitBucketUpperMilliseconds = [0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 250, 1000];

    private readonly long[] _ackWaitBuckets = new long[AckWaitBucketUpperMilliseconds.Length + 1];
    private long _timerSetupTicks;
    private long _socketReceiveTicks;
    private long _socketSendTicks;
    private long _fileWriteTicks;
    private long _hashTicks;
    private long _verifyTicks;
    private long _flushTicks;
    private long _commitTicks;
    private long _ackWaitTicks;
    private long _ackWaitCount;
    private long _windowSendTicks;
    private long _windowBlocks;
    private long _windowCount;

    /// <summary>Time spent constructing the per-datagram acknowledgement timer.</summary>
    public void AddTimerSetup(long ticks) => Interlocked.Add(ref _timerSetupTicks, ticks);

    /// <summary>Time spent inside the socket receive, including the kernel wait.</summary>
    public void AddSocketReceive(long ticks) => Interlocked.Add(ref _socketReceiveTicks, ticks);

    /// <summary>Time spent inside the socket send.</summary>
    public void AddSocketSend(long ticks) => Interlocked.Add(ref _socketSendTicks, ticks);

    /// <summary>Time spent in <see cref="Stream.Write(ReadOnlySpan{byte})"/> on the receive path.</summary>
    public void AddFileWrite(long ticks) => Interlocked.Add(ref _fileWriteTicks, ticks);

    /// <summary>Time spent appending article bytes to SHA-256.</summary>
    public void AddHash(long ticks) => Interlocked.Add(ref _hashTicks, ticks);

    /// <summary>Time spent in the benchmark's post-receive check, rename, and delete.</summary>
    public void AddVerify(long ticks) => Interlocked.Add(ref _verifyTicks, ticks);

    /// <summary>Time spent flushing and disposing the received file.</summary>
    public void AddFlush(long ticks) => Interlocked.Add(ref _flushTicks, ticks);

    /// <summary>Time from the last DATA block until the final ACK is sent, including verification.</summary>
    public void AddCommit(long ticks) => Interlocked.Add(ref _commitTicks, ticks);

    /// <summary>Records one window burst. <paramref name="blocks"/> is how many DATA packets that burst contained.</summary>
    public void RecordWindowSend(long ticks, int blocks)
    {
        Interlocked.Add(ref _windowSendTicks, ticks);
        Interlocked.Add(ref _windowBlocks, blocks);
        Interlocked.Increment(ref _windowCount);
    }

    /// <summary>Records the wait from the end of a window burst until a progressing ACK. Timeouts are not recorded here.</summary>
    public void RecordAckWait(long ticks)
    {
        Interlocked.Add(ref _ackWaitTicks, ticks);
        Interlocked.Increment(ref _ackWaitCount);
        var milliseconds = ticks * 1000d / Stopwatch.Frequency;
        var bucket = AckWaitBucketUpperMilliseconds.Length;
        for (var index = 0; index < AckWaitBucketUpperMilliseconds.Length; index++)
        {
            if (milliseconds < AckWaitBucketUpperMilliseconds[index])
            {
                bucket = index;
                break;
            }
        }

        Interlocked.Increment(ref _ackWaitBuckets[bucket]);
    }

    /// <summary>Copies the counters into a report object. Bucket percentiles use the upper edge of the containing bucket.</summary>
    public DiagnosticSnapshot Snapshot()
    {
        var waits = Volatile.Read(ref _ackWaitCount);
        var windows = Volatile.Read(ref _windowCount);
        var sendTicks = Volatile.Read(ref _windowSendTicks);
        var waitTicks = Volatile.Read(ref _ackWaitTicks);
        var buckets = new long[_ackWaitBuckets.Length];
        for (var index = 0; index < buckets.Length; index++)
        {
            buckets[index] = Volatile.Read(ref _ackWaitBuckets[index]);
        }

        return new DiagnosticSnapshot
        {
            TimingBasis = "sums of Stopwatch durations across overlapping transfers; divide milliseconds by elapsed seconds for the average number of transfers in that section",
            TimerSetupMilliseconds = Milliseconds(_timerSetupTicks),
            SocketReceiveMilliseconds = Milliseconds(_socketReceiveTicks),
            SocketSendMilliseconds = Milliseconds(_socketSendTicks),
            FileWriteMilliseconds = Milliseconds(_fileWriteTicks),
            HashMilliseconds = Milliseconds(_hashTicks),
            VerifyMilliseconds = Milliseconds(_verifyTicks),
            FlushMilliseconds = Milliseconds(_flushTicks),
            CommitMilliseconds = Milliseconds(_commitTicks),
            AckWaitMilliseconds = Milliseconds(_ackWaitTicks),
            AckWaitCount = waits,
            AckWaitMeanMilliseconds = waits == 0 ? 0 : Milliseconds(_ackWaitTicks) / waits,
            AckWaitP50Milliseconds = BucketPercentile(buckets, waits, 0.50),
            AckWaitP95Milliseconds = BucketPercentile(buckets, waits, 0.95),
            WindowCount = windows,
            MeanBlocksPerWindow = windows == 0 ? 0 : Volatile.Read(ref _windowBlocks) / (double)windows,
            WindowSendMilliseconds = Milliseconds(_windowSendTicks),
            EstimatedSendFraction = sendTicks + waitTicks == 0 ? 0 : sendTicks / (double)(sendTicks + waitTicks),
            AckWaitBucketUpperMilliseconds = AckWaitBucketUpperMilliseconds,
            AckWaitBucketCounts = buckets,
        };
    }

    private static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    private static double BucketPercentile(long[] buckets, long count, double rank)
    {
        if (count <= 0)
        {
            return 0;
        }

        var target = (long)Math.Ceiling(rank * count);
        if (target < 1)
        {
            target = 1;
        }

        long seen = 0;
        for (var index = 0; index < buckets.Length; index++)
        {
            seen += buckets[index];
            if (seen >= target)
            {
                return index < AckWaitBucketUpperMilliseconds.Length
                    ? AckWaitBucketUpperMilliseconds[index]
                    : AckWaitBucketUpperMilliseconds[^1];
            }
        }

        return AckWaitBucketUpperMilliseconds[^1];
    }
}

/// <summary>Frozen diagnostic counters for one measured interval.</summary>
public sealed class DiagnosticSnapshot
{
    /// <summary>How to read the summed durations.</summary>
    public string TimingBasis { get; init; } = "";

    /// <summary>Summed time constructing per-datagram acknowledgement timers.</summary>
    public double TimerSetupMilliseconds { get; init; }

    /// <summary>Summed time inside socket receives.</summary>
    public double SocketReceiveMilliseconds { get; init; }

    /// <summary>Summed time inside socket sends.</summary>
    public double SocketSendMilliseconds { get; init; }

    /// <summary>Summed time in receive-path stream writes.</summary>
    public double FileWriteMilliseconds { get; init; }

    /// <summary>Summed time appending payload bytes to SHA-256.</summary>
    public double HashMilliseconds { get; init; }

    /// <summary>Summed time in the benchmark reread, rename, and delete.</summary>
    public double VerifyMilliseconds { get; init; }

    /// <summary>Summed time flushing the received file.</summary>
    public double FlushMilliseconds { get; init; }

    /// <summary>Summed time from the last DATA block until the final ACK, including verification.</summary>
    public double CommitMilliseconds { get; init; }

    /// <summary>Summed time waiting for a progressing ACK after a window burst.</summary>
    public double AckWaitMilliseconds { get; init; }

    /// <summary>Window bursts that received a progressing ACK.</summary>
    public long AckWaitCount { get; init; }

    /// <summary>Mean ACK wait. Directly measured, one sample per progressing window.</summary>
    public double AckWaitMeanMilliseconds { get; init; }

    /// <summary>Approximate p50 ACK wait. The value is the upper edge of the histogram bucket.</summary>
    public double AckWaitP50Milliseconds { get; init; }

    /// <summary>Approximate p95 ACK wait. The value is the upper edge of the histogram bucket.</summary>
    public double AckWaitP95Milliseconds { get; init; }

    /// <summary>Window bursts transmitted.</summary>
    public long WindowCount { get; init; }

    /// <summary>Average DATA packets placed in one window burst, including the short final window.</summary>
    public double MeanBlocksPerWindow { get; init; }

    /// <summary>Summed time spent transmitting those window bursts.</summary>
    public double WindowSendMilliseconds { get; init; }

    /// <summary>
    /// Window-send time divided by window-send time plus progressing ACK-wait time.
    /// This estimates how much of a window cycle is transmission rather than waiting. It is not a wire duty cycle.
    /// </summary>
    public double EstimatedSendFraction { get; init; }

    /// <summary>Upper bounds, in milliseconds, for every bucket except the last.</summary>
    public double[] AckWaitBucketUpperMilliseconds { get; init; } = [];

    /// <summary>
    /// Window counts. Entry <c>i</c> waited strictly less than <see cref="AckWaitBucketUpperMilliseconds"/> entry <c>i</c>.
    /// The final entry waited at least as long as the last upper bound.
    /// </summary>
    public long[] AckWaitBucketCounts { get; init; } = [];
}
