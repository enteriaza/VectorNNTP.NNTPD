namespace VectorNNTP.NNTPD.ArticleIngestion.OverviewDb;

/// <summary>
/// Decoded OverviewDB ingest handoff. Production encoding writes this shape as
/// protobuf; this type is the decoded view used by tests and consumers.
/// </summary>
internal sealed class OverviewArticleV1
{
    /// <summary>Current contract version encoded in every payload.</summary>
    public const uint CurrentSchemaVersion = 1;

    /// <summary>Gets or sets the schema version. Must be <see cref="CurrentSchemaVersion"/>.</summary>
    public uint SchemaVersion { get; set; }

    /// <summary>Gets or sets the 32-byte VectorNNTP <c>ArticleId</c> / ArtId digest.</summary>
    public byte[] ArticleId { get; set; } = [];

    /// <summary>Gets or sets the clear-text Message-ID value, including angle brackets.</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>Gets the newsgroup membership in article header order.</summary>
    public List<string> Newsgroups { get; } = [];

    /// <summary>Gets or sets the Subject header value.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Gets or sets the From header value.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>Gets or sets the canonical Date header value from <c>ArticleRecord</c>.</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>Gets or sets the References header value.</summary>
    public string References { get; set; } = string.Empty;

    /// <summary>Gets or sets canonical article byte size (<c>ArticleRecord.ArtSize</c>).</summary>
    public uint Bytes { get; set; }

    /// <summary>Gets or sets the overview line count (<c>ArticleRecord.ArtLines</c>).</summary>
    public uint Lines { get; set; }
}
