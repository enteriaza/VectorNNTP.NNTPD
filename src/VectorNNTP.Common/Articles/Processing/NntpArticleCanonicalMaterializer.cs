using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;

namespace VectorNNTP.Common.Articles.Processing
{
    /// <summary>
    /// Classifies why canonical Date/Path materialization could not produce a retainable article.
    /// </summary>
    public enum NntpArticleCanonicalFailureCode
    {
        /// <summary>Materialization succeeded.</summary>
        None = 0,

        /// <summary>The parse result was not accepted.</summary>
        ParseNotAccepted = 1,

        /// <summary>The parse result did not carry article bytes.</summary>
        EmptyArticle = 2,

        /// <summary>The selected Date header could not be resolved for rewrite.</summary>
        MissingSelectedDateHeader = 3,

        /// <summary>Accepted parse output contained duplicate Path headers.</summary>
        DuplicatePath = 4,

        /// <summary>The header/body separator could not be resolved for Path insertion.</summary>
        InvalidHeaderSeparator = 5,

        /// <summary>Canonical output would exceed <see cref="ArticleResourceLimits.MaxArticleBytes"/>.</summary>
        ArticleTooLarge = 6,

        /// <summary>Canonical Date rewrite would exceed <see cref="ArticleResourceLimits.MaxArticleLineBytes"/>.</summary>
        DateLineTooLong = 7,

        /// <summary>Canonical Path rewrite would exceed <see cref="ArticleResourceLimits.MaxArticleLineBytes"/>.</summary>
        PathRewriteLineTooLong = 8,

        /// <summary>Canonical Path insertion would exceed <see cref="ArticleResourceLimits.MaxArticleLineBytes"/>.</summary>
        PathInsertionLineTooLong = 9,

        /// <summary>The planned write length did not match bytes written, or rewrite ranges overlapped.</summary>
        WriteMismatch = 10,

        /// <summary>Canonical transfer: ArtData was null or empty.</summary>
        TransferNullArtData = 100,

        /// <summary>Canonical transfer: ArtSize does not match ArtData.Length or exceeds limits.</summary>
        TransferArtSizeMismatch = 101,

        /// <summary>Canonical transfer: a FieldTable range is invalid or out of bounds.</summary>
        TransferInvalidFieldRange = 102,

        /// <summary>Canonical transfer: Message-ID range is absent, empty, or not an RFC 5536 msg-id.</summary>
        TransferMissingMessageId = 103,

        /// <summary>Canonical transfer: Message-ID-derived ArtId does not match expected OPEN ArtId.</summary>
        TransferArticleIdMismatch = 104,

        /// <summary>Canonical transfer: recomputed ArtHash does not match META.</summary>
        TransferArtHashMismatch = 105,

        /// <summary>Canonical transfer: SelectedDateHeaderName is not a Date-family value.</summary>
        TransferInvalidSelectedDateHeader = 106,

        /// <summary>Canonical transfer: Locate(ArtData) does not equal transferred FieldTable.</summary>
        TransferFieldTableMismatch = 107,

        /// <summary>Canonical transfer: Date range cannot be parsed to CanonicalUtc.</summary>
        TransferInvalidDate = 108,

        /// <summary>Canonical transfer: ArtLines is negative or greater than ArtSize.</summary>
        TransferInvalidArtLines = 109,
    }

    /// <summary>
    /// Result of one canonical article materialization attempt.
    /// </summary>
    /// <remarks>
    /// On success, <see cref="ArticleBytes"/> is a newly allocated exact-size buffer owned by the caller.
    /// The source parse slices are not mutated.
    /// </remarks>
    internal readonly struct NntpArticleCanonicalMaterializeResult
    {
        /// <summary>
        /// Initializes a materialization result.
        /// </summary>
        /// <param name="isAccepted">Whether a canonical article was produced.</param>
        /// <param name="failureCode">Failure classification when not accepted.</param>
        /// <param name="articleBytes">Exact-size canonical article when accepted; otherwise <see langword="null"/>.</param>
        private NntpArticleCanonicalMaterializeResult(
            bool isAccepted,
            NntpArticleCanonicalFailureCode failureCode,
            byte[]? articleBytes)
        {
            IsAccepted = isAccepted;
            FailureCode = failureCode;
            ArticleBytes = articleBytes;
        }

        /// <summary>Gets a value indicating whether materialization produced a canonical article.</summary>
        internal bool IsAccepted { get; }

        /// <summary>Gets the failure classification when <see cref="IsAccepted"/> is false.</summary>
        internal NntpArticleCanonicalFailureCode FailureCode { get; }

        /// <summary>Gets the exact-size canonical article bytes when accepted.</summary>
        internal byte[]? ArticleBytes { get; }

        /// <summary>Creates a rejected result with no output buffer.</summary>
        /// <param name="failureCode">Failure classification.</param>
        /// <returns>Rejected result.</returns>
        internal static NntpArticleCanonicalMaterializeResult Rejected(NntpArticleCanonicalFailureCode failureCode)
            => new(false, failureCode, null);

        /// <summary>Creates a successful result that transfers <paramref name="articleBytes"/> to the caller.</summary>
        /// <param name="articleBytes">Exact-size canonical article.</param>
        /// <returns>Accepted result.</returns>
        internal static NntpArticleCanonicalMaterializeResult Accepted(byte[] articleBytes)
            => new(true, NntpArticleCanonicalFailureCode.None, articleBytes);
    }

    /// <summary>
    /// Materializes one validated article into the canonical retained-byte representation.
    /// </summary>
    /// <remarks>
    /// Rewrites only the selected date-header value and the Path header (or inserts Path).
    /// All other header bytes and the entire destuffed body are copied exactly. yEnc is not decoded.
    /// </remarks>
    internal static class NntpArticleCanonicalMaterializer
    {
        /// <summary>ASCII prefix written when a Path header is inserted: <c>Path: </c>, including the trailing SP.</summary>
        private static ReadOnlySpan<byte> PathHeaderPrefix => "Path: "u8;

        /// <summary>
        /// Builds a canonicalized article payload from a parser-accepted article.
        /// </summary>
        /// <param name="parseResult">Accepted parse result carrying Date/Path metadata and source-byte header slices.</param>
        /// <param name="pathMode">Normalize revalidates Path. Traverse prepends the parser's application FQDN.</param>
        /// <returns>
        /// A result that owns one exact-size canonical buffer on success, or a failure classification without an output buffer.
        /// </returns>
        internal static NntpArticleCanonicalMaterializeResult Materialize(
            in NntpArticleParseResult parseResult,
            ArticlePathMode pathMode)
        {
            if (!parseResult.IsAccepted)
            {
                return NntpArticleCanonicalMaterializeResult.Rejected(NntpArticleCanonicalFailureCode.ParseNotAccepted);
            }

            var source = parseResult.ArticleBytes.Span;
            if (source.IsEmpty)
            {
                return NntpArticleCanonicalMaterializeResult.Rejected(NntpArticleCanonicalFailureCode.EmptyArticle);
            }

            if (!TryResolveSelectedDateHeader(parseResult, source, out var dateHeader))
            {
                return NntpArticleCanonicalMaterializeResult.Rejected(NntpArticleCanonicalFailureCode.MissingSelectedDateHeader);
            }

            if (!TryResolvePathHeader(parseResult, out var pathHeader, out var pathFailure))
            {
                return NntpArticleCanonicalMaterializeResult.Rejected(pathFailure);
            }

            Span<byte> canonicalDate = stackalloc byte[40];
            if (!parseResult.TryFormatCanonicalUtc(canonicalDate, out var dateWritten))
            {
                return NntpArticleCanonicalMaterializeResult.Rejected(NntpArticleCanonicalFailureCode.MissingSelectedDateHeader);
            }

            canonicalDate = canonicalDate[..dateWritten];

            Span<byte> canonicalPath = stackalloc byte[ArticlePathCanonicalizer.MaxPathLength + 256];
            if (!parseResult.TryWriteCanonicalPath(pathMode, canonicalPath, out var pathWritten))
            {
                return NntpArticleCanonicalMaterializeResult.Rejected(NntpArticleCanonicalFailureCode.WriteMismatch);
            }

            canonicalPath = canonicalPath[..pathWritten];

            var dateEdit = new HeaderEdit(dateHeader.ValueOffset, dateHeader.ValueLength, dateWritten);
            HeaderEdit? pathEdit = pathHeader.HasValue
                ? new HeaderEdit(pathHeader.Value.ValueOffset, pathHeader.Value.ValueLength, pathWritten)
                : null;

            HeaderInsert? pathInsert = null;
            if (!pathHeader.HasValue)
            {
                if (!TryResolveHeaderSeparator(source, parseResult.HeaderBytes.Length, out var separator))
                {
                    return NntpArticleCanonicalMaterializeResult.Rejected(NntpArticleCanonicalFailureCode.InvalidHeaderSeparator);
                }

                var insertedLength = PathHeaderPrefix.Length + pathWritten + separator.LineTerminatorLength;
                pathInsert = new HeaderInsert(separator.StartOffset, insertedLength, separator.LineTerminatorLength);
            }

            var lengthDelta = dateWritten - dateEdit.RemovedLength;
            if (pathEdit.HasValue)
            {
                lengthDelta += pathEdit.Value.ReplacementLength - pathEdit.Value.RemovedLength;
            }

            if (pathInsert.HasValue)
            {
                lengthDelta += pathInsert.Value.InsertedLength;
            }

            int destinationLength;
            try
            {
                destinationLength = checked(source.Length + lengthDelta);
            }
            catch (OverflowException)
            {
                return NntpArticleCanonicalMaterializeResult.Rejected(NntpArticleCanonicalFailureCode.ArticleTooLarge);
            }

            var boundaryFailure = ValidateCanonicalArticleBoundaries(
                source,
                parseResult,
                dateEdit,
                pathEdit,
                pathInsert,
                destinationLength);
            if (boundaryFailure != NntpArticleCanonicalFailureCode.None)
            {
                return NntpArticleCanonicalMaterializeResult.Rejected(boundaryFailure);
            }

            var destination = GC.AllocateUninitializedArray<byte>(destinationLength);
            var written = 0;
            var consumed = 0;
            var dest = destination.AsSpan();

            if (pathEdit is HeaderEdit resolvedPathEdit && resolvedPathEdit.StartOffset < dateEdit.StartOffset)
            {
                if (!TryApplyEdit(source, dest, ref consumed, ref written, resolvedPathEdit, canonicalPath)
                    || !TryApplyEdit(source, dest, ref consumed, ref written, dateEdit, canonicalDate))
                {
                    return NntpArticleCanonicalMaterializeResult.Rejected(NntpArticleCanonicalFailureCode.WriteMismatch);
                }
            }
            else
            {
                if (!TryApplyEdit(source, dest, ref consumed, ref written, dateEdit, canonicalDate))
                {
                    return NntpArticleCanonicalMaterializeResult.Rejected(NntpArticleCanonicalFailureCode.WriteMismatch);
                }

                if (pathEdit is HeaderEdit resolvedPathEditAfter
                    && !TryApplyEdit(source, dest, ref consumed, ref written, resolvedPathEditAfter, canonicalPath))
                {
                    return NntpArticleCanonicalMaterializeResult.Rejected(NntpArticleCanonicalFailureCode.WriteMismatch);
                }
            }

            if (pathInsert is HeaderInsert insert
                && !TryApplyInsert(source, dest, ref consumed, ref written, insert, canonicalPath))
            {
                return NntpArticleCanonicalMaterializeResult.Rejected(NntpArticleCanonicalFailureCode.WriteMismatch);
            }

            source[consumed..].CopyTo(dest[written..]);
            written += source.Length - consumed;

            return written == destinationLength
                ? NntpArticleCanonicalMaterializeResult.Accepted(destination)
                : NntpArticleCanonicalMaterializeResult.Rejected(NntpArticleCanonicalFailureCode.WriteMismatch);
        }

        /// <summary>
        /// Finds the header whose known name, value length, and value bytes match the parser's selected date.
        /// </summary>
        /// <param name="parseResult">Accepted parse result. <see cref="NntpArticleHeaderName.Unknown"/> fails immediately.</param>
        /// <param name="source">Original article bytes that own the header slices.</param>
        /// <param name="dateHeader">First wire-order header that matches name, length, and bytes. Otherwise default.</param>
        /// <returns><see langword="false"/> when no header matches. Later headers with the same name are not considered after a match.</returns>
        private static bool TryResolveSelectedDateHeader(
            in NntpArticleParseResult parseResult,
            ReadOnlySpan<byte> source,
            out NntpArticleHeaderEntry dateHeader)
        {
            dateHeader = default;
            if (parseResult.SelectedDateHeaderName == NntpArticleHeaderName.Unknown)
            {
                return false;
            }

            var originalDateValue = parseResult.OriginalDateValue.Span;
            for (var i = 0; i < parseResult.HeaderCount; i++)
            {
                var header = parseResult.GetHeader(i);
                if (header.KnownName != parseResult.SelectedDateHeaderName)
                {
                    continue;
                }

                if (header.ValueLength != originalDateValue.Length)
                {
                    continue;
                }

                if (source.Slice(header.ValueOffset, header.ValueLength).SequenceEqual(originalDateValue))
                {
                    dateHeader = header;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Finds the single Path header, or reports that Path is absent.
        /// </summary>
        /// <param name="parseResult">Accepted parse result.</param>
        /// <param name="pathHeader">The Path entry when exactly one exists; otherwise <see langword="null"/>.</param>
        /// <param name="failure"><see cref="NntpArticleCanonicalFailureCode.DuplicatePath"/> when a second Path is seen; otherwise <see cref="NntpArticleCanonicalFailureCode.None"/>, including when Path is absent.</param>
        /// <returns><see langword="false"/> only for a duplicate Path. Absence is success with a null header so the caller can insert one.</returns>
        private static bool TryResolvePathHeader(
            in NntpArticleParseResult parseResult,
            out NntpArticleHeaderEntry? pathHeader,
            out NntpArticleCanonicalFailureCode failure)
        {
            pathHeader = null;
            failure = NntpArticleCanonicalFailureCode.None;
            for (var i = 0; i < parseResult.HeaderCount; i++)
            {
                var header = parseResult.GetHeader(i);
                if (header.KnownName != NntpArticleHeaderName.Path)
                {
                    continue;
                }

                if (pathHeader.HasValue)
                {
                    failure = NntpArticleCanonicalFailureCode.DuplicatePath;
                    return false;
                }

                pathHeader = header;
            }

            return true;
        }

        /// <summary>
        /// Locates the blank line that ends the header section: two terminators back to back at <paramref name="headerLength"/>.
        /// </summary>
        /// <param name="source">Original article bytes.</param>
        /// <param name="headerLength">Parser header-section length, including the blank line.</param>
        /// <param name="separator">
        /// Start of the blank-line terminator and the length of the terminator that ended the previous header line.
        /// A CRLF blank line yields terminator length 2; a lone CR or LF yields 1.
        /// </param>
        /// <returns><see langword="false"/> when <paramref name="headerLength"/> is outside the buffer or either terminator is missing.</returns>
        private static bool TryResolveHeaderSeparator(ReadOnlySpan<byte> source, int headerLength, out HeaderSeparator separator)
        {
            separator = default;
            if (headerLength <= 0 || headerLength > source.Length)
            {
                return false;
            }

            var boundaryTerminatorLength = ResolveTerminatorLengthEndingAt(source, headerLength);
            if (boundaryTerminatorLength == 0)
            {
                return false;
            }

            var boundaryTerminatorStart = headerLength - boundaryTerminatorLength;
            var precedingHeaderTerminatorLength = ResolveTerminatorLengthEndingAt(source, boundaryTerminatorStart);
            if (precedingHeaderTerminatorLength == 0)
            {
                return false;
            }

            var precedingHeaderTerminatorStart = boundaryTerminatorStart - precedingHeaderTerminatorLength;
            if (precedingHeaderTerminatorStart < 0)
            {
                return false;
            }

            separator = new HeaderSeparator(boundaryTerminatorStart, precedingHeaderTerminatorLength);
            return true;
        }

        /// <summary>
        /// Returns the length of the CR, LF, or CRLF that ends at <paramref name="endExclusive"/>.
        /// </summary>
        /// <param name="buffer">Article bytes.</param>
        /// <param name="endExclusive">Index just after the candidate terminator.</param>
        /// <returns>2 for CRLF, 1 for a lone LF or CR, or 0 when the preceding byte is not a terminator or the index is not positive.</returns>
        private static int ResolveTerminatorLengthEndingAt(ReadOnlySpan<byte> buffer, int endExclusive)
        {
            if (endExclusive <= 0)
            {
                return 0;
            }

            var last = buffer[endExclusive - 1];
            if (last == (byte)'\n')
            {
                return endExclusive >= 2 && buffer[endExclusive - 2] == (byte)'\r' ? 2 : 1;
            }

            return last == (byte)'\r' ? 1 : 0;
        }

        /// <summary>
        /// Rejects a canonical article whose total size or rewritten date or Path line would exceed the resource limits.
        /// </summary>
        /// <param name="source">Original article bytes.</param>
        /// <param name="parseResult">Header index used to find the physical lines being rewritten.</param>
        /// <param name="dateEdit">Selected date value replacement.</param>
        /// <param name="pathEdit">Existing Path value replacement, or <see langword="null"/> when Path will be inserted.</param>
        /// <param name="pathInsert">Inserted Path line, or <see langword="null"/> when an existing Path is rewritten.</param>
        /// <param name="destinationLength">Projected canonical length, already checked for overflow by the caller.</param>
        /// <returns>
        /// <see cref="NntpArticleCanonicalFailureCode.ArticleTooLarge"/>,
        /// <see cref="NntpArticleCanonicalFailureCode.DateLineTooLong"/>,
        /// <see cref="NntpArticleCanonicalFailureCode.PathRewriteLineTooLong"/>,
        /// <see cref="NntpArticleCanonicalFailureCode.PathInsertionLineTooLong"/>,
        /// <see cref="NntpArticleCanonicalFailureCode.InvalidHeaderSeparator"/> when the physical line cannot be measured,
        /// or <see cref="NntpArticleCanonicalFailureCode.None"/>.
        /// Line limits use <see cref="ArticleResourceLimits.MaxArticleLineBytes"/> on the line content without its terminator.
        /// </returns>
        private static NntpArticleCanonicalFailureCode ValidateCanonicalArticleBoundaries(
            ReadOnlySpan<byte> source,
            in NntpArticleParseResult parseResult,
            HeaderEdit dateEdit,
            HeaderEdit? pathEdit,
            HeaderInsert? pathInsert,
            int destinationLength)
        {
            if (destinationLength > ArticleResourceLimits.MaxArticleBytes)
            {
                return NntpArticleCanonicalFailureCode.ArticleTooLarge;
            }

            if (!TryComputePhysicalHeaderLineLength(
                    source,
                    dateEdit.StartOffset,
                    dateEdit.RemovedLength,
                    dateEdit.ReplacementLength,
                    parseResult,
                    out var selectedDateLineLength))
            {
                return NntpArticleCanonicalFailureCode.InvalidHeaderSeparator;
            }

            if (selectedDateLineLength > ArticleResourceLimits.MaxArticleLineBytes)
            {
                return NntpArticleCanonicalFailureCode.DateLineTooLong;
            }

            if (pathEdit is HeaderEdit resolvedPathEdit)
            {
                if (!TryComputePhysicalHeaderLineLength(
                        source,
                        resolvedPathEdit.StartOffset,
                        resolvedPathEdit.RemovedLength,
                        resolvedPathEdit.ReplacementLength,
                        parseResult,
                        out var rewrittenPathLineLength))
                {
                    return NntpArticleCanonicalFailureCode.InvalidHeaderSeparator;
                }

                if (rewrittenPathLineLength > ArticleResourceLimits.MaxArticleLineBytes)
                {
                    return NntpArticleCanonicalFailureCode.PathRewriteLineTooLong;
                }
            }

            if (pathInsert is HeaderInsert resolvedPathInsert)
            {
                var insertedPathLineContentLength = resolvedPathInsert.InsertedLength - resolvedPathInsert.LineTerminatorLength;
                if (insertedPathLineContentLength > ArticleResourceLimits.MaxArticleLineBytes)
                {
                    return NntpArticleCanonicalFailureCode.PathInsertionLineTooLong;
                }
            }

            return NntpArticleCanonicalFailureCode.None;
        }

        /// <summary>
        /// Computes the header line length after replacing the value and excluding the trailing terminator.
        /// </summary>
        /// <param name="source">Original article bytes.</param>
        /// <param name="valueOffset">Header value offset that identifies the line.</param>
        /// <param name="removedLength">Original value length.</param>
        /// <param name="replacementLength">Canonical value length.</param>
        /// <param name="parseResult">Header table. The line runs from this header's name to the next header's name, or to the blank-line separator.</param>
        /// <param name="lineContentLength">Content length after replacement, not including CR/LF. 0 on failure.</param>
        /// <returns><see langword="false"/> when the header cannot be found, the separator is invalid, or the length overflows.</returns>
        private static bool TryComputePhysicalHeaderLineLength(
            ReadOnlySpan<byte> source,
            int valueOffset,
            int removedLength,
            int replacementLength,
            in NntpArticleParseResult parseResult,
            out int lineContentLength)
        {
            lineContentLength = 0;
            var headerIndex = -1;
            for (var i = 0; i < parseResult.HeaderCount; i++)
            {
                var header = parseResult.GetHeader(i);
                if (header.ValueOffset == valueOffset && header.ValueLength == removedLength)
                {
                    headerIndex = i;
                    break;
                }
            }

            if (headerIndex < 0)
            {
                return false;
            }

            var lineStart = parseResult.GetHeader(headerIndex).NameOffset;
            int lineEndExclusive;
            if (headerIndex + 1 < parseResult.HeaderCount)
            {
                lineEndExclusive = parseResult.GetHeader(headerIndex + 1).NameOffset;
            }
            else if (!TryResolveHeaderSeparator(source, parseResult.HeaderBytes.Length, out var separator))
            {
                return false;
            }
            else
            {
                lineEndExclusive = separator.StartOffset;
            }

            var originalLineLength = lineEndExclusive - lineStart;
            var lineTerminatorLength = ResolveTrailingLineTerminatorLength(source, lineStart, lineEndExclusive);
            var originalLineContentLength = originalLineLength - lineTerminatorLength;
            try
            {
                lineContentLength = checked(originalLineContentLength - removedLength + replacementLength);
            }
            catch (OverflowException)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Returns the terminator length at the end of <c>[lineStart, lineEndExclusive)</c>.
        /// </summary>
        /// <param name="source">Original article bytes.</param>
        /// <param name="lineStart">Start of the physical header line.</param>
        /// <param name="lineEndExclusive">Start of the next header, or the blank-line separator.</param>
        /// <returns>2 for a trailing CRLF, 1 for a trailing CR or LF, or 0 when the slice is empty or does not end in a terminator.</returns>
        private static int ResolveTrailingLineTerminatorLength(ReadOnlySpan<byte> source, int lineStart, int lineEndExclusive)
        {
            var physicalLength = lineEndExclusive - lineStart;
            return physicalLength <= 0
                ? 0
                : physicalLength >= 2
                  && source[lineEndExclusive - 2] == (byte)'\r'
                  && source[lineEndExclusive - 1] == (byte)'\n'
                    ? 2
                    : source[lineEndExclusive - 1] is (byte)'\r' or (byte)'\n' ? 1 : 0;
        }

        /// <summary>
        /// Copies unchanged source bytes up to a value, then writes the replacement.
        /// </summary>
        /// <param name="source">Original article bytes.</param>
        /// <param name="destination">Canonical buffer.</param>
        /// <param name="consumed">Source index already copied. Must be less than or equal to the edit start.</param>
        /// <param name="written">Destination index. Advanced by the gap and the replacement.</param>
        /// <param name="edit">Value range to replace. The name and colon before <see cref="HeaderEdit.StartOffset"/> are copied as source bytes.</param>
        /// <param name="replacement">Canonical value bytes. Length must match <see cref="HeaderEdit.ReplacementLength"/>; the method copies this span, not the stored length.</param>
        /// <returns><see langword="false"/> when the edit starts before <paramref name="consumed"/>.</returns>
        private static bool TryApplyEdit(
            ReadOnlySpan<byte> source,
            Span<byte> destination,
            ref int consumed,
            ref int written,
            HeaderEdit edit,
            ReadOnlySpan<byte> replacement)
        {
            if (edit.StartOffset < consumed)
            {
                return false;
            }

            source[consumed..edit.StartOffset].CopyTo(destination[written..]);
            written += edit.StartOffset - consumed;
            replacement.CopyTo(destination[written..]);
            written += replacement.Length;
            consumed = edit.StartOffset + edit.RemovedLength;
            return true;
        }

        /// <summary>
        /// Inserts <c>Path: </c>, the canonical path, and a copy of the preceding line terminator.
        /// </summary>
        /// <param name="source">Original article bytes.</param>
        /// <param name="destination">Canonical buffer.</param>
        /// <param name="consumed">Source index already copied. Must be less than or equal to the insert offset.</param>
        /// <param name="written">Destination index.</param>
        /// <param name="insert">Insert point at the blank-line terminator, the total inserted length, and the terminator length to copy.</param>
        /// <param name="canonicalPath">Path value bytes, without the <c>Path: </c> prefix.</param>
        /// <returns><see langword="false"/> when the insert point is before <paramref name="consumed"/>.</returns>
        private static bool TryApplyInsert(
            ReadOnlySpan<byte> source,
            Span<byte> destination,
            ref int consumed,
            ref int written,
            HeaderInsert insert,
            ReadOnlySpan<byte> canonicalPath)
        {
            if (insert.Offset < consumed)
            {
                return false;
            }

            source[consumed..insert.Offset].CopyTo(destination[written..]);
            written += insert.Offset - consumed;
            PathHeaderPrefix.CopyTo(destination[written..]);
            written += PathHeaderPrefix.Length;
            canonicalPath.CopyTo(destination[written..]);
            written += canonicalPath.Length;
            source.Slice(insert.Offset - insert.LineTerminatorLength, insert.LineTerminatorLength).CopyTo(destination[written..]);
            written += insert.LineTerminatorLength;
            consumed = insert.Offset;
            return true;
        }

        /// <summary>
        /// One in-place replacement of a header value. Bytes outside the value are copied unchanged.
        /// </summary>
        /// <param name="StartOffset">Source offset of the first value byte.</param>
        /// <param name="RemovedLength">Original value length.</param>
        /// <param name="ReplacementLength">Canonical value length used for the size delta. The write copies the supplied span.</param>
        private readonly record struct HeaderEdit(int StartOffset, int RemovedLength, int ReplacementLength);

        /// <summary>
        /// Blank line between headers and body, expressed as the start of its terminator and the previous line's terminator length.
        /// </summary>
        /// <param name="StartOffset">Source offset where the blank-line terminator begins. A Path insert is placed here.</param>
        /// <param name="LineTerminatorLength">Length of the terminator that ended the last header: 2 for CRLF, 1 for CR or LF.</param>
        private readonly record struct HeaderSeparator(int StartOffset, int LineTerminatorLength);

        /// <summary>
        /// A Path header inserted because the source article had none.
        /// </summary>
        /// <param name="Offset">Source offset of the blank-line terminator, where the new line is inserted.</param>
        /// <param name="InsertedLength"><c>Path: </c> plus the canonical path plus <paramref name="LineTerminatorLength"/>.</param>
        /// <param name="LineTerminatorLength">Terminator copied from the bytes immediately before <paramref name="Offset"/>.</param>
        private readonly record struct HeaderInsert(int Offset, int InsertedLength, int LineTerminatorLength);
    }
}
