using System.IO.Hashing;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Articles.Processing;

namespace VectorNNTP.Common.Articles;

/// <summary>
/// Builds an <see cref="ArticleRecord"/> from destuffed article bytes.
/// </summary>
/// <remarks>
/// Uses the existing Common parser, Diablo <see cref="ArticleTypeClassifier"/>, and
/// Date/Path materializer. ArtHash is one-shot XXH3-64 of the completed
/// canonical ArtData (<c>XxHash3.HashToUInt64</c>), not an incremental hash
/// and not IEEE CRC-32.
/// The destuffed source buffer is not retained. FieldTable
/// ranges are located on the materialized ArtData so Date/Path length changes cannot
/// leave offsets pointing at the discarded source.
/// This factory has no request Message-ID parameter and does not match an IHAVE/ARTICLE
/// request. Request matching remains an ingestion/orchestration responsibility.
/// NNTPD TAKETHIS and POST construct the record before queue admission.
/// IHAVE and BackFiller ingestion are not wired through this factory.
/// </remarks>
public static class ArticleRecordFactory
{
    /// <summary>
    /// Parses, classifies, and materializes destuffed article bytes into a CanonicalV1 record.
    /// </summary>
    /// <param name="parser">Common article parser (local identity used for Path hops).</param>
    /// <param name="destuffedArticle">Unstuffed article bytes. Not retained after success.</param>
    /// <returns>Accepted record referencing the materialized ArtData buffer, or a failure classification.</returns>
    public static ArticleRecordCreateResult TryCreate(
        NntpArticleParser parser,
        ReadOnlyMemory<byte> destuffedArticle)
    {
        ArgumentNullException.ThrowIfNull(parser);

        var parse = parser.Parse(destuffedArticle);
        if (!parse.IsAccepted)
        {
            return ArticleRecordCreateResult.RejectedParse(parse.FailureCode);
        }

        var materialize = NntpArticleCanonicalMaterializer.Materialize(in parse);
        if (!materialize.IsAccepted || materialize.ArticleBytes is null)
        {
            return ArticleRecordCreateResult.RejectedMaterialize(materialize.FailureCode);
        }

        var artData = materialize.ArticleBytes;
        var fields = ArticleFieldTable.Locate(artData, parse.SelectedDateHeaderName);
        var artId = ArticleId.FromMessageId(fields.MessageId.Slice(artData));
        var artHash = XxHash3.HashToUInt64(artData);
        var artType = ArticleTypeClassifier.Classify(parse.HeaderBytes.Span, parse.BodyBytes.Span);
        var record = new ArticleRecord(
            artId,
            artHash,
            artType,
            parse.BodyLineCount,
            parse.CanonicalUtc,
            ArticleParseStatus.CanonicalV1,
            artData,
            fields);
        return ArticleRecordCreateResult.Accepted(record);
    }
}

/// <summary>
/// Result of one <see cref="ArticleRecordFactory.TryCreate"/> attempt.
/// </summary>
public readonly struct ArticleRecordCreateResult
{
    private ArticleRecordCreateResult(
        bool isAccepted,
        ArticleRecord record,
        NntpArticleParseFailureCode parseFailure,
        NntpArticleCanonicalFailureCode materializeFailure)
    {
        IsAccepted = isAccepted;
        Record = record;
        ParseFailure = parseFailure;
        MaterializeFailure = materializeFailure;
    }

    /// <summary>Gets a value indicating whether a CanonicalV1 record was produced.</summary>
    public bool IsAccepted { get; }

    /// <summary>Gets the constructed record when accepted; otherwise default.</summary>
    public ArticleRecord Record { get; }

    /// <summary>Gets the parser failure when construction stopped at parse.</summary>
    public NntpArticleParseFailureCode ParseFailure { get; }

    /// <summary>Gets the materializer failure when construction stopped at materialize.</summary>
    public NntpArticleCanonicalFailureCode MaterializeFailure { get; }

    /// <summary>Creates an accepted result that carries <paramref name="record"/> (no ArtData copy).</summary>
    /// <param name="record">CanonicalV1 record.</param>
    /// <returns>Accepted result.</returns>
    public static ArticleRecordCreateResult Accepted(ArticleRecord record)
        => new(true, record, NntpArticleParseFailureCode.None, NntpArticleCanonicalFailureCode.None);

    /// <summary>Creates a parse-rejected result with no ArtData.</summary>
    /// <param name="parseFailure">Parser failure code.</param>
    /// <returns>Rejected result.</returns>
    public static ArticleRecordCreateResult RejectedParse(NntpArticleParseFailureCode parseFailure)
        => new(false, default, parseFailure, NntpArticleCanonicalFailureCode.None);

    /// <summary>Creates a materialize-rejected result with no ArtData.</summary>
    /// <param name="materializeFailure">Materializer failure code.</param>
    /// <returns>Rejected result.</returns>
    public static ArticleRecordCreateResult RejectedMaterialize(NntpArticleCanonicalFailureCode materializeFailure)
        => new(false, default, NntpArticleParseFailureCode.None, materializeFailure);
}
