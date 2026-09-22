using System.ComponentModel.DataAnnotations;

namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Bounded article ingestion queue and incoming-spool writer settings.
/// </summary>
/// <remarks>
/// Used by <c>TAKETHIS</c> (and later <c>POST</c>) as the
/// <c>network → queue → background spool writer → spool/incoming</c> boundary.
/// Disk persistence is never on the session receive critical path.
/// </remarks>
public sealed class ArticleIngestionOptions
{
    /// <summary>Default relative directory for accepted incoming articles.</summary>
    public const string DefaultIncomingDirectory = "spool/incoming";

    /// <summary>Default bounded queue capacity (articles).</summary>
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
    /// Gets or sets the maximum number of accepted articles waiting for spool persistence.
    /// </summary>
    /// <remarks>
    /// When full, enqueue waits for capacity (backpressure) until shutdown completes the queue.
    /// Valid range is <c>1–100000</c>. Default is <c>256</c>.
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
