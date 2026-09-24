namespace VectorNNTP.NNTPD.Bench;

/// <summary>Counts from a raw SPEEDTEST payload drain. Does not own payload bytes.</summary>
internal readonly struct SpeedTestRawReceiveResult
{
    public SpeedTestRawReceiveResult(long receivedBytes, TimeSpan elapsed, int receiveCalls, int prefixBytes)
    {
        ReceivedBytes = receivedBytes;
        Elapsed = elapsed;
        ReceiveCalls = receiveCalls;
        PrefixBytes = prefixBytes;
    }

    public long ReceivedBytes { get; }
    public TimeSpan Elapsed { get; }
    public int ReceiveCalls { get; }
    public int PrefixBytes { get; }

    public double GbitPerSecond
    {
        get
        {
            var seconds = Elapsed.TotalSeconds;
            return seconds > 0 && ReceivedBytes > 0
                ? ReceivedBytes * 8d / seconds / 1_000_000_000d
                : 0;
        }
    }
}
