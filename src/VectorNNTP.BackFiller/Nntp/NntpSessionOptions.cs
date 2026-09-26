namespace VectorNNTP.BackFiller.Nntp;

/// <summary>
/// Immutable acquisition guardrails. Defaults match the old BackFiller acquisition contract.
/// </summary>
/// <param name="ReceiveBufferBytes">Socket send/receive buffer size.</param>
/// <param name="MaxStatusLineBytes">Maximum accepted status-line length excluding CRLF.</param>
/// <param name="MaxArticleBytes">Maximum destuffed ARTICLE payload. Defaults to the old 5 MiB contract.</param>
/// <param name="ConnectTimeout">TCP connect and TLS handshake budget.</param>
/// <param name="CommandTimeout">Command write and single-line status read budget.</param>
/// <param name="ReceiveTimeout">Multiline ARTICLE payload read budget.</param>
public sealed record NntpSessionOptions(
    int ReceiveBufferBytes,
    int MaxStatusLineBytes,
    int MaxArticleBytes,
    TimeSpan ConnectTimeout,
    TimeSpan CommandTimeout,
    TimeSpan ReceiveTimeout)
{
    /// <summary>
    /// Old-worker defaults: 64 KiB buffers, 16 KiB status lines, 5 MiB articles, 30 s connect/command, 2 min receive.
    /// </summary>
    public static NntpSessionOptions Default { get; } = new(
        ReceiveBufferBytes: 64 * 1024,
        MaxStatusLineBytes: ArticleResourceLimits.MaxStatusLineBytes,
        MaxArticleBytes: ArticleResourceLimits.MaxArticleBytes,
        ConnectTimeout: TimeSpan.FromSeconds(30),
        CommandTimeout: TimeSpan.FromSeconds(30),
        ReceiveTimeout: TimeSpan.FromMinutes(2));
}
