using System.Net;
using VectorNNTP.Common.Articles;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.PostFilter;

/// <summary>NNTPD-only SPAMD CHECK seam. Does not mutate <see cref="ArticleRecord.ArtData"/>.</summary>
internal interface IPostFilterSpamAssassin
{
    /// <summary>Runs CHECK. Cancellation is rethrown and is not an <c>on_failure</c> event.</summary>
    ValueTask<PostFilterSpamAssassinResult> CheckAsync(
        ArticleRecord article,
        string? accountName,
        PostFilterSpamAssassinTarget target,
        SpamdScanContext scanContext,
        CancellationToken cancellationToken = default);
}

/// <summary>Peer and server identity used only to build the disposable SPAMD scan message.</summary>
internal readonly record struct SpamdScanContext(
    IPAddress ClientAddress,
    string ServerFqdn,
    DateTimeOffset Now);

/// <summary>SPAMD endpoints, pool, and timeouts captured with one PostFilter snapshot.</summary>
internal readonly record struct PostFilterSpamAssassinTarget(
    IReadOnlyList<string> Hosts,
    int Port,
    TimeSpan ConnectTimeout,
    TimeSpan OperationTimeout,
    string ProtocolVersion,
    int MaxConnections,
    PostFilterSpamAssassinHostSelection HostSelection);

/// <summary>CHECK outcome classified for PostFilter policy.</summary>
internal enum PostFilterSpamAssassinStatus
{
    /// <summary>SPAMD reported ham.</summary>
    Ham = 0,

    /// <summary>SPAMD reported spam.</summary>
    Spam = 1,

    /// <summary>Connect, protocol, timeout, or malformed response.</summary>
    Failed = 2,
}

/// <summary>CHECK result.</summary>
internal readonly struct PostFilterSpamAssassinResult
{
    /// <summary>Initializes a result.</summary>
    public PostFilterSpamAssassinResult(PostFilterSpamAssassinStatus status, string detail)
    {
        Status = status;
        Detail = detail;
    }

    /// <summary>Gets the classified status.</summary>
    public PostFilterSpamAssassinStatus Status { get; }

    /// <summary>Gets an internal detail.</summary>
    public string Detail { get; }

    /// <summary>Ham.</summary>
    public static PostFilterSpamAssassinResult Ham() =>
        new(PostFilterSpamAssassinStatus.Ham, "ham");

    /// <summary>Spam.</summary>
    public static PostFilterSpamAssassinResult Spam(string detail) =>
        new(PostFilterSpamAssassinStatus.Spam, detail);

    /// <summary>Scanner fault.</summary>
    public static PostFilterSpamAssassinResult Failed(string detail) =>
        new(PostFilterSpamAssassinStatus.Failed, detail);
}
