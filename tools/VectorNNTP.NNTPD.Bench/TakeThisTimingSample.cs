namespace VectorNNTP.NNTPD.Bench;

/// <summary>One client-observed pipelined TAKETHIS transaction (microseconds).</summary>
internal readonly record struct TakeThisClientTimingSample(
    long CommandAndArticleSendUs,
    long SendTo239Us,
    long TransactionUs,
    int OutstandingAtSend,
    int OutstandingAt239);
