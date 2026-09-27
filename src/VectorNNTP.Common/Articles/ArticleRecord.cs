namespace VectorNNTP.Common.Articles;

/// <summary>
/// Canonical in-memory Vector article after one-time parse, classification, and materialization.
/// </summary>
/// <remarks>
/// References the canonical article buffer produced by materialization. FieldTable ranges
/// refer to that same buffer. The buffer lifetime is managed by the producer/consumer
/// workflow; copying this struct shares the reference and does not copy ArtData.
/// Overview <c>:bytes</c> is <see cref="ArtSize"/>; <c>:lines</c> is <see cref="ArtLines"/>.
/// Newsgroups is the <see cref="ArticleFieldTable.Newsgroups"/> range (no allocated group
/// list). This type is not a serialization protocol.
/// </remarks>
public readonly struct ArticleRecord
{
    private readonly byte[] _artData;

    /// <summary>
    /// Initializes a record that references <paramref name="artData"/> without copying it.
    /// </summary>
    /// <param name="artId">BLAKE3 of the Message-ID header <em>value</em> bytes in <paramref name="artData"/>.</param>
    /// <param name="artCrc">IEEE CRC-32 of the entire <paramref name="artData"/> buffer.</param>
    /// <param name="artType">Diablo-derived classification flags.</param>
    /// <param name="artLines">Body line count for overview <c>:lines</c>.</param>
    /// <param name="canonicalUtc">Winning Date-family header as UTC.</param>
    /// <param name="parseStatus">Parse/validation state of this record, not of any request.</param>
    /// <param name="artData">Canonical unstuffed article bytes whose lifetime is managed by the caller workflow.</param>
    /// <param name="fields">Header value ranges into <paramref name="artData"/>.</param>
    internal ArticleRecord(
        ArticleId artId,
        uint artCrc,
        ArticleType artType,
        int artLines,
        DateTime canonicalUtc,
        ArticleParseStatus parseStatus,
        byte[] artData,
        ArticleFieldTable fields)
    {
        ArtId = artId;
        ArtCrc = artCrc;
        ArtType = artType;
        ArtLines = artLines;
        CanonicalUtc = canonicalUtc;
        ParseStatus = parseStatus;
        _artData = artData;
        Fields = fields;
    }

    /// <summary>Gets the internal article identity BLAKE3(Message-ID value bytes).</summary>
    public ArticleId ArtId { get; }

    /// <summary>Gets IEEE CRC-32 of <see cref="ArtData"/>.</summary>
    public uint ArtCrc { get; }

    /// <summary>Gets the canonical unstuffed article size; always <c>ArtData.Length</c>.</summary>
    public int ArtSize => _artData?.Length ?? 0;

    /// <summary>Gets the Diablo-derived article type flags. Not <c>NntpArticleType</c>.</summary>
    public ArticleType ArtType { get; }

    /// <summary>
    /// Gets the body line count for overview <c>:lines</c>.
    /// Empty body is 0. Each CRLF/CR/LF-terminated line counts as one line; a final
    /// unterminated fragment also counts as one line. Uses the same walk as the parser
    /// body-length check. Materialization does not rewrite the body, so the count is
    /// valid for canonical ArtData.
    /// </summary>
    public int ArtLines { get; }

    /// <summary>Gets the winning Date-family header resolved to UTC.</summary>
    public DateTime CanonicalUtc { get; }

    /// <summary>
    /// Gets the parse/validation state of this record.
    /// <see cref="ArticleParseStatus.CanonicalV1"/> does not imply request Message-ID matching.
    /// </summary>
    public ArticleParseStatus ParseStatus { get; }

    /// <summary>Gets the canonical unstuffed article bytes referenced by this record (headers, blank line, encoded body).</summary>
    public ReadOnlyMemory<byte> ArtData => _artData;

    /// <summary>Gets header value ranges into <see cref="ArtData"/>.</summary>
    public ArticleFieldTable Fields { get; }

    /// <summary>Gets the Message-ID header value bytes in ArtData.</summary>
    public ReadOnlySpan<byte> MessageId => Fields.MessageId.Slice(_artData);

    /// <summary>Gets the Newsgroups header value bytes in ArtData (ArtGroups).</summary>
    public ReadOnlySpan<byte> Newsgroups => Fields.Newsgroups.Slice(_artData);

    /// <summary>Gets the Subject header value bytes in ArtData.</summary>
    public ReadOnlySpan<byte> Subject => Fields.Subject.Slice(_artData);

    /// <summary>Gets the From header value bytes in ArtData.</summary>
    public ReadOnlySpan<byte> From => Fields.From.Slice(_artData);

    /// <summary>Gets the winning Date-family header value bytes in ArtData.</summary>
    public ReadOnlySpan<byte> Date => Fields.Date.Slice(_artData);

    /// <summary>Gets the References header value bytes in ArtData.</summary>
    public ReadOnlySpan<byte> References => Fields.References.Slice(_artData);

    /// <summary>Gets the Path header value bytes in ArtData.</summary>
    public ReadOnlySpan<byte> Path => Fields.Path.Slice(_artData);
}
