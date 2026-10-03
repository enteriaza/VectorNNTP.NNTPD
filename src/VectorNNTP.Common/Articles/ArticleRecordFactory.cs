using System.IO.Hashing;
using VectorNNTP.Common.Articles.DateParser;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Articles.Processing;
using VectorNNTP.Common.Transport.ArticleTransfer;

namespace VectorNNTP.Common.Articles
{
    /// <summary>
    /// Builds an <see cref="ArticleRecord"/> from destuffed article bytes or from a validated
    /// canonical transfer (META + ArtData).
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
    /// <see cref="TryCreateFromCanonicalTransfer"/> reconstructs a CanonicalV1 record from
    /// transferred META + ArtData without destuff, parse, or rematerialization.
    /// </remarks>
    public static class ArticleRecordFactory
    {
        /// <summary>
        /// Parses and materializes an article, recording a receiving-system Path hop.
        /// </summary>
        /// <param name="parser">Common article parser. Its local identity is the application FQDN.</param>
        /// <param name="destuffedArticle">Unstuffed article bytes. Not retained after success.</param>
        /// <returns>Accepted record referencing the materialized ArtData buffer, or a failure classification.</returns>
        public static ArticleRecordCreateResult TryCreate(
            NntpArticleParser parser,
            ReadOnlyMemory<byte> destuffedArticle)
            => TryCreate(parser, destuffedArticle, ArticlePathMode.Traverse);

        /// <summary>
        /// Parses, classifies, and materializes destuffed article bytes into a CanonicalV1 record.
        /// </summary>
        /// <param name="parser">Common article parser. Its local identity is the application FQDN used by <see cref="ArticlePathMode.Traverse"/>.</param>
        /// <param name="destuffedArticle">Unstuffed article bytes. Not retained after success.</param>
        /// <param name="pathMode"><see cref="ArticlePathMode.Traverse"/> prepends the parser FQDN. <see cref="ArticlePathMode.Normalize"/> does not.</param>
        /// <returns>Accepted record referencing the materialized ArtData buffer, or a failure classification.</returns>
        internal static ArticleRecordCreateResult TryCreate(
            NntpArticleParser parser,
            ReadOnlyMemory<byte> destuffedArticle,
            ArticlePathMode pathMode)
        {
            ArgumentNullException.ThrowIfNull(parser);

            var parse = parser.Parse(destuffedArticle);
            if (!parse.IsAccepted)
            {
                return ArticleRecordCreateResult.RejectedParse(parse.FailureCode);
            }

            var materialize = NntpArticleCanonicalMaterializer.Materialize(in parse, pathMode);
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
            return ArticleRecordCreateResult.Accepted(record, parse.SelectedDateHeaderName);
        }

        /// <summary>
        /// Reconstructs a CanonicalV1 <see cref="ArticleRecord"/> from transferred META and ArtData
        /// without destuffing, reparsing, or rematerializing.
        /// </summary>
        /// <param name="artData">Receiver-owned ArtData buffer assembled from DATA frames. Not copied.</param>
        /// <param name="meta">Decoded META describing <paramref name="artData"/>.</param>
        /// <param name="expectedArtId">ArticleId from OPEN (not from META).</param>
        /// <returns>
        /// Accepted CanonicalV1 record referencing <paramref name="artData"/>, or a transfer
        /// rejection via <see cref="ArticleRecordCreateResult.MaterializeFailure"/>.
        /// </returns>
        /// <remarks>
        /// Validates ArtSize, FieldTable bounds, Message-ID → ArtId binding, ArtHash, Locate
        /// equality, CanonicalUtc from the Date range, and ArtLines sanity. Recomputes ArtType
        /// via <see cref="ArticleTypeClassifier"/> (header + ≤8 KiB body prefix). Does not
        /// recount body lines.
        /// </remarks>
        internal static ArticleRecordCreateResult TryCreateFromCanonicalTransfer(
            byte[] artData,
            in ArticleCanonicalTransferMeta meta,
            in ArticleId expectedArtId)
        {
            if (artData is null || artData.Length == 0)
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferNullArtData);
            }

            if (meta.ArtSize != artData.Length
                || meta.ArtSize < 1
                || meta.ArtSize > ArticleResourceLimits.MaxArticleBytes)
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferArtSizeMismatch);
            }

            if (!TryValidateFieldTable(meta.Fields, meta.ArtSize))
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferInvalidFieldRange);
            }

            if (!meta.Fields.MessageId.IsPresent || meta.Fields.MessageId.Length == 0)
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferMissingMessageId);
            }

            var artId = ArticleId.FromMessageId(meta.Fields.MessageId.Slice(artData));
            if (artId != expectedArtId)
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferArticleIdMismatch);
            }

            var artHash = XxHash3.HashToUInt64(artData);
            if (artHash != meta.ArtHash)
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferArtHashMismatch);
            }

            if (!IsDateFamilyHeader(meta.SelectedDateHeaderName))
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferInvalidSelectedDateHeader);
            }

            var located = ArticleFieldTable.Locate(artData, meta.SelectedDateHeaderName);
            if (located != meta.Fields)
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferFieldTableMismatch);
            }

            if (!meta.Fields.Date.IsPresent || meta.Fields.Date.Length == 0)
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferInvalidDate);
            }

            if (!NewsDateParser.TryGetCanonicalUtc(meta.Fields.Date.Slice(artData), out var canonicalUtc, out _)
                || canonicalUtc == default
                || canonicalUtc.Kind != DateTimeKind.Utc)
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferInvalidDate);
            }

            if (meta.ArtLines < 0 || meta.ArtLines > meta.ArtSize)
            {
                return ArticleRecordCreateResult.RejectedMaterialize(
                    NntpArticleCanonicalFailureCode.TransferInvalidArtLines);
            }

            SplitHeadersAndBody(artData, out var headers, out var body);
            var artType = ArticleTypeClassifier.Classify(headers, body);

            var record = new ArticleRecord(
                artId,
                artHash,
                artType,
                meta.ArtLines,
                canonicalUtc,
                ArticleParseStatus.CanonicalV1,
                artData,
                meta.Fields);
            return ArticleRecordCreateResult.Accepted(record, meta.SelectedDateHeaderName);
        }

        private static bool IsDateFamilyHeader(NntpArticleHeaderName name) =>
            name is NntpArticleHeaderName.Date
                or NntpArticleHeaderName.InjectionDate
                or NntpArticleHeaderName.NntpPostingDate
                or NntpArticleHeaderName.Posted
                or NntpArticleHeaderName.XDate
                or NntpArticleHeaderName.DeliveryDate;

        private static bool TryValidateFieldTable(in ArticleFieldTable fields, int artSize)
        {
            return TryValidateRange(fields.MessageId, artSize)
                && TryValidateRange(fields.Newsgroups, artSize)
                && TryValidateRange(fields.Subject, artSize)
                && TryValidateRange(fields.From, artSize)
                && TryValidateRange(fields.Date, artSize)
                && TryValidateRange(fields.References, artSize)
                && TryValidateRange(fields.Path, artSize);
        }

        private static bool TryValidateRange(ArticleByteRange range, int artSize)
        {
            if (!range.IsPresent)
            {
                return range.Offset == -1 && range.Length == 0;
            }

            if (range.Offset < 0 || range.Length < 0)
            {
                return false;
            }

            return (long)range.Offset + range.Length <= artSize;
        }

        private static void SplitHeadersAndBody(
            ReadOnlySpan<byte> artData,
            out ReadOnlySpan<byte> headers,
            out ReadOnlySpan<byte> body)
        {
            var separator = "\r\n\r\n"u8;
            var at = artData.IndexOf(separator);
            if (at < 0)
            {
                headers = artData;
                body = ReadOnlySpan<byte>.Empty;
                return;
            }

            headers = artData[..(at + separator.Length)];
            body = artData[(at + separator.Length)..];
        }
    }

    /// <summary>
    /// Result of one article-record creation attempt.
    /// </summary>
    public readonly struct ArticleRecordCreateResult
    {
        private ArticleRecordCreateResult(
            bool isAccepted,
            ArticleRecord record,
            NntpArticleParseFailureCode parseFailure,
            NntpArticleCanonicalFailureCode materializeFailure,
            NntpArticleHeaderName selectedDateHeaderName)
        {
            IsAccepted = isAccepted;
            Record = record;
            ParseFailure = parseFailure;
            MaterializeFailure = materializeFailure;
            SelectedDateHeaderName = selectedDateHeaderName;
        }

        /// <summary>Gets a value indicating whether a CanonicalV1 record was produced.</summary>
        public bool IsAccepted { get; }

        /// <summary>Gets the constructed record when accepted; otherwise default.</summary>
        public ArticleRecord Record { get; }

        /// <summary>Gets the parser failure when construction stopped at parse.</summary>
        public NntpArticleParseFailureCode ParseFailure { get; }

        /// <summary>Gets the materializer or transfer failure when construction stopped after parse.</summary>
        public NntpArticleCanonicalFailureCode MaterializeFailure { get; }

        /// <summary>
        /// Gets the Date-family header name selected during parse (for VATP META).
        /// <see cref="NntpArticleHeaderName.Unknown"/> when not accepted.
        /// </summary>
        internal NntpArticleHeaderName SelectedDateHeaderName { get; }

        /// <summary>Creates an accepted result that carries <paramref name="record"/> (no ArtData copy).</summary>
        internal static ArticleRecordCreateResult Accepted(
            ArticleRecord record,
            NntpArticleHeaderName selectedDateHeaderName = NntpArticleHeaderName.Unknown)
            => new(true, record, NntpArticleParseFailureCode.None, NntpArticleCanonicalFailureCode.None, selectedDateHeaderName);

        /// <summary>Creates a parse-rejected result with no ArtData.</summary>
        internal static ArticleRecordCreateResult RejectedParse(NntpArticleParseFailureCode parseFailure)
            => new(false, default, parseFailure, NntpArticleCanonicalFailureCode.None, NntpArticleHeaderName.Unknown);

        /// <summary>Creates a materialize-rejected result with no ArtData.</summary>
        internal static ArticleRecordCreateResult RejectedMaterialize(NntpArticleCanonicalFailureCode materializeFailure)
            => new(false, default, NntpArticleParseFailureCode.None, materializeFailure, NntpArticleHeaderName.Unknown);
    }
}
