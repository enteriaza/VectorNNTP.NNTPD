using System.ComponentModel.DataAnnotations;

namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Peer authorization for NNTP transit/streaming feeds.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AllowedPeers"/> lists effective client IP addresses trusted as transit peers.
/// Matching peers receive <c>AuthorizedTransit</c> and <c>StreamingPermitted</c> without
/// interactive authentication. This is peer authorization, not user authentication.
/// </para>
/// <para>
/// Default is empty (deny-by-default). Does not grant reader access, posting, or
/// <c>IsAuthenticated</c>. Distinct from <c>ProxyHosts</c> (HAProxy PROXY trust only).
/// Literal IPv4/IPv6 only (no CIDR/DNS in this version).
/// </para>
/// </remarks>
public sealed class TransitOptions
{
    /// <summary>
    /// Gets or sets effective client IP addresses authorized as transit/streaming peers.
    /// </summary>
    /// <remarks>
    /// Matched against <c>ConnectionClientIdentity.ClientAddress</c> (PROXY effective client
    /// when applicable). Empty by default.
    /// </remarks>
    public string[] AllowedPeers { get; set; } = [];

    /// <summary>
    /// Gets or sets the maximum number of concurrent outstanding STREAM article TX operations
    /// admitted by <c>NntpStreamArticleTxScheduler</c>.
    /// </summary>
    /// <remarks>
    /// Default is <c>8</c>. Valid range is <c>4–16</c> (rejected outside that range; not clamped).
    /// Independent of TX Channel capacity, Pipe thresholds, and TAKETHIS ingestion queue capacity.
    /// </remarks>
    [Range(4, 16)]
    public int StreamOutstandingArticleDepth { get; set; } = 8;
}
