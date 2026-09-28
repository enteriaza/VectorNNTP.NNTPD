using System.ComponentModel.DataAnnotations;

namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// NNTPD-local transit/streaming runtime options under <c>Nntpd:Transit</c>.
/// </summary>
/// <remarks>
/// Peer authorization lives in the top-level <c>Transit</c> section
/// (<see cref="TransitPeersOptions"/>). This type holds STREAM TX depth and
/// site-wide INN-style junk-file settings (<see cref="WantTrash"/> /
/// <see cref="LogTrash"/>), corresponding to inn.conf <c>wanttrash</c> /
/// <c>logtrash</c>.
/// </remarks>
public sealed class TransitOptions
{
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

    /// <summary>
    /// Gets or sets whether TAKETHIS/IHAVE articles posted only to unknown or
    /// RFC 6048 <c>j</c> groups are accepted and treated as junk internally.
    /// </summary>
    /// <remarks>
    /// INN <c>wanttrash</c>. Default is <c>true</c>. When <see langword="true"/>,
    /// TAKETHIS/IHAVE articles posted only to unknown groups are accepted and
    /// classified as junk after dequeue. When <see langword="false"/>, those
    /// articles are rejected before enqueue with a <c>-</c> news event. RFC 6048
    /// <c>j</c> (PeerOnly) groups remain accepted junk. The original
    /// <c>Newsgroups:</c> header is not rewritten. POST is not subject to this
    /// policy. The in-memory catalogue snapshot is consulted only after
    /// <c>ArticleRecord</c> exists.
    /// </remarks>
    public bool WantTrash { get; set; } = true;

    /// <summary>
    /// Gets or sets whether accepted-junk events are written to the INN <c>news</c> log.
    /// </summary>
    /// <remarks>
    /// INN <c>logtrash</c>. Default is <c>true</c>. When <see langword="false"/>,
    /// junk articles are still accepted; only the <c>j</c> news line is omitted.
    /// Accepted (<c>+</c>) and rejected (<c>-</c>) events are still written.
    /// </remarks>
    public bool LogTrash { get; set; } = true;
}
