using System.ComponentModel.DataAnnotations;

namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// NNTPD-local transit/streaming runtime options under <c>Nntpd:Transit</c>.
/// </summary>
/// <remarks>
/// Peer authorization lives in the top-level <c>Transit</c> section
/// (<see cref="TransitPeersOptions"/>). This type only holds STREAM TX depth.
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
}
