namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Unique POST article Message-IDs of the same width as
/// <see cref="TakeThisArticlePayload.StaticMessageId"/>.
/// </summary>
/// <remarks>
/// Layout: <c>&lt;pIIIIIIIIII-CC-SSSSSSSSSSSS@vectornntp.local&gt;</c>.
/// Instance is unique per connection object so HistoryDB cannot see a prior run.
/// </remarks>
internal sealed class PostMessageIdBuffer
{
    private static long _nextInstance = TakeThisCommandBufferSeed.Next();

    public PostMessageIdBuffer(int connectionId, long? instance = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(connectionId);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(connectionId, TakeThisCommandBuffer.MaxConnections);

        Instance = instance ?? Interlocked.Increment(ref _nextInstance);
        ArgumentOutOfRangeException.ThrowIfNegative(Instance);
        ConnectionId = connectionId;
    }

    public long Instance { get; }

    public int ConnectionId { get; }

    public string Format(long sequence) =>
        FormatMessageId(ConnectionId, sequence, Instance);

    public static string FormatMessageId(int connectionId, long sequence, long instance)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(connectionId);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(connectionId, TakeThisCommandBuffer.MaxConnections);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfNegative(instance);
        return $"<p{instance:D10}-{connectionId:D2}-{sequence:D12}@vectornntp.local>";
    }
}

/// <summary>Shared 10-digit instance seed for TAKETHIS/POST History isolation.</summary>
internal static class TakeThisCommandBufferSeed
{
    public static long Next()
    {
        var seed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        seed ^= Environment.ProcessId * 1_000_000_007L;
        seed ^= System.Diagnostics.Stopwatch.GetTimestamp();
        var bounded = seed % 10_000_000_000L;
        return bounded < 0 ? -bounded : bounded;
    }
}
