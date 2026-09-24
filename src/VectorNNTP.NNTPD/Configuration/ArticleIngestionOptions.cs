using System.ComponentModel.DataAnnotations;

namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Incoming-spool writer and per-article size settings for the Transit ingestion path.
/// </summary>
/// <remarks>
/// Used by <c>TAKETHIS</c> and <c>IHAVE</c> as the
/// <c>network → queue → background spool writer → spool/incoming</c> boundary.
/// Disk persistence is never on the session receive critical path.
/// Queue admission is bounded by <see cref="NntpdOptions.TransitQueueMemoryLimit"/>,
/// not by <see cref="QueueCapacity"/>.
/// </remarks>
public sealed class ArticleIngestionOptions
{
    /// <summary>Default relative directory for accepted incoming articles.</summary>
    public const string DefaultIncomingDirectory = "spool/incoming";

    /// <summary>
    /// Legacy default article-count setting. Not an admission bound.
    /// </summary>
    public const int DefaultQueueCapacity = 256;

    /// <summary>Default maximum article payload size in bytes (4 MiB).</summary>
    public const int DefaultMaxArticleBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the filesystem directory for accepted incoming articles.
    /// </summary>
    /// <remarks>Default is <c>spool/incoming</c>. Created on demand by the spool writer.</remarks>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(512)]
    public string IncomingDirectory { get; set; } = DefaultIncomingDirectory;

    /// <summary>
    /// Gets or sets a leftover article-count setting retained for binding compatibility.
    /// </summary>
    /// <remarks>
    /// Not an admission bound. The historical 256-entry cap was a memory-safety
    /// choke and has been replaced by <see cref="NntpdOptions.TransitQueueMemoryLimit"/>.
    /// Valid range remains <c>1–100000</c> so existing configuration files still validate.
    /// </remarks>
    [Range(1, 100_000)]
    public int QueueCapacity { get; set; } = DefaultQueueCapacity;

    /// <summary>
    /// Gets or sets the maximum accepted article size in bytes (after dot-unstuffing).
    /// </summary>
    /// <remarks>Valid range is <c>1–104857600</c> (100 MiB). Default is 4 MiB.</remarks>
    [Range(1, 100 * 1024 * 1024)]
    public int MaxArticleBytes { get; set; } = DefaultMaxArticleBytes;
}
