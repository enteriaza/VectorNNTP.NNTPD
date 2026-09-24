namespace VectorNNTP.NNTPD.Bench;

/// <summary>One client-observed serialized IHAVE transaction (microseconds).</summary>
internal readonly record struct IhaveTimingSample(
    int ArticleBytes,
    long CommandSendUs,
    long IhaveSentTo335Us,
    long ArticleSendUs,
    long ArticleSentTo235Us,
    long TransactionUs);
