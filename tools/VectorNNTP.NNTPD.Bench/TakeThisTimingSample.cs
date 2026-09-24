namespace VectorNNTP.NNTPD.Bench;

/// <summary>One client-observed pipelined TAKETHIS transaction (microseconds).</summary>
internal readonly record struct TakeThisClientTimingSample(
    long CommandAndArticleSendUs,
    long SendTo239Us,
    long TransactionUs,
    int OutstandingAtSend,
    int OutstandingAt239)
{
    /// <summary>How many <c>Socket.SendAsync</c> calls completed this article (includes partials).</summary>
    public int SendCalls { get; init; }

    /// <summary>Bytes accepted by the first <c>SendAsync</c>.</summary>
    public int FirstSendBytes { get; init; }

    /// <summary>Smallest successful <c>SendAsync</c> for this article.</summary>
    public int MinSendBytes { get; init; }

    /// <summary>Largest successful <c>SendAsync</c> for this article.</summary>
    public int MaxSendBytes { get; init; }

    /// <summary>Sum of <c>SendAsync</c> return values (command + article).</summary>
    public int TotalSendBytes { get; init; }

    /// <summary>Application-level <c>SendAsync</c> operations in flight when this send started.</summary>
    public int ActiveSendsAtStart { get; init; }

    /// <summary>Articles whose send had completed and were still awaiting 239 when this send started.</summary>
    public int Awaiting239AtSendStart { get; init; }
}
