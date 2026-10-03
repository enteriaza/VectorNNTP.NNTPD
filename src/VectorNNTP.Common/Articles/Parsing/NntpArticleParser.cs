using System.Buffers;
using System.Text;
using VectorNNTP.Common.Articles.DateParser;
using VectorNNTP.Common.Articles.Validation;
using VectorNNTP.Common.Articles.YEnc;

namespace VectorNNTP.Common.Articles.Parsing
{
    /// <summary>
    /// Parses untrusted NNTP article bytes into a compact structured result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Input bytes are expected to be transport-normalized article bytes received after acquisition
    /// removes NNTP terminator framing and performs transport dot-unstuffing exactly once.
    /// </para>
    /// <para>
    /// Header, body, and header-value slices in the returned result alias the caller-provided buffer
    /// and therefore inherit its lifetime.
    /// </para>
    /// <para>
    /// The parser does not download, retry, queue, publish, or rewrite the article.
    /// When a yEnc marker is detected in the configured scan window, <see cref="YEncArticleValidator"/>
    /// validates the body in place. Invalid yEnc is reported as <see cref="NntpArticleParseFailureCode.YEncDecodingFailed"/>.
    /// </para>
    /// </remarks>
    public sealed class NntpArticleParser
    {
        /// <summary>Maximum accepted Newsgroups value length.</summary>
        private const int MaxNewsgroupsLength = 4096;

        /// <summary>Maximum accepted From value length.</summary>
        private const int MaxFromLength = 2048;

        private const int InlineHeaderCapacity = NntpArticleHeaderInlineStore.Capacity;

        private static ReadOnlySpan<byte> YEncBeginMarker => "=ybegin "u8;

        private static ReadOnlySpan<byte> MultipartMarker => "multipart/"u8;

        private static ReadOnlySpan<byte> Base64Encoding => "base64"u8;

        private static ReadOnlySpan<byte> BinaryEncoding => "binary"u8;

        private readonly byte[] _localIdentity;
        private readonly NntpArticleParserOptions _options;

        /// <summary>
        /// Initializes a new parser instance with immutable local identity and parse limits.
        /// </summary>
        /// <param name="localIdentity">Local FQDN used for Path rewrite classification and canonical Path writes.</param>
        /// <param name="options">Parser guardrail options.</param>
        internal NntpArticleParser(ReadOnlySpan<byte> localIdentity, NntpArticleParserOptions options)
        {
            if (localIdentity.IsEmpty || IsAllAsciiWhitespace(localIdentity))
            {
                throw new ArgumentException("Local identity must be a non-empty FQDN.", nameof(localIdentity));
            }

            _localIdentity = localIdentity.ToArray();
            _options = CapOptions(options);
        }

        /// <summary>
        /// Initializes a new parser instance with default parser limits.
        /// </summary>
        /// <param name="localIdentity">Local FQDN used for Path rewrite classification and canonical Path writes.</param>
        internal NntpArticleParser(ReadOnlySpan<byte> localIdentity)
            : this(localIdentity, NntpArticleParserOptions.Default)
        {
        }

        /// <summary>
        /// Initializes a new parser instance from an ASCII local identity string.
        /// </summary>
        /// <param name="localIdentity">Local FQDN used for Path rewrite classification and canonical Path writes.</param>
        /// <param name="options">Parser guardrail options.</param>
        internal NntpArticleParser(string localIdentity, NntpArticleParserOptions options)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(localIdentity);
            _localIdentity = Encoding.ASCII.GetBytes(localIdentity.Trim());
            _options = CapOptions(options);
        }

        /// <summary>
        /// Initializes a new parser instance from an ASCII local identity string and default limits.
        /// </summary>
        /// <param name="localIdentity">Local FQDN used for Path rewrite classification and canonical Path writes.</param>
        public NntpArticleParser(string localIdentity)
            : this(localIdentity, NntpArticleParserOptions.Default)
        {
        }

        /// <summary>
        /// Parses one NNTP article payload and returns deterministic acceptance, rejection, and classification metadata.
        /// </summary>
        /// <param name="articleBytes">Complete article bytes after acquisition has removed ARTICLE framing and transport dot-stuffing.</param>
        /// <returns>
        /// A parse result that preserves slices into the original buffer, validates required headers,
        /// resolves Date, classifies Path rewrite, classifies content type, and validates detected yEnc sections.
        /// </returns>
        internal NntpArticleParseResult Parse(ReadOnlyMemory<byte> articleBytes)
        {
            var localIdentity = (ReadOnlyMemory<byte>)_localIdentity;
            if (articleBytes.IsEmpty)
            {
                return NntpArticleParseResult.Rejected(
                    NntpArticleParseFailureCode.EmptyArticle,
                    NntpArticleType.Malformed,
                    articleBytes,
                    ReadOnlyMemory<byte>.Empty,
                    ReadOnlyMemory<byte>.Empty,
                    [],
                    0,
                    localIdentity);
            }

            if (articleBytes.Length > _options.MaxArticleBytes)
            {
                return NntpArticleParseResult.Rejected(
                    NntpArticleParseFailureCode.ArticleTooLarge,
                    NntpArticleType.Malformed,
                    articleBytes,
                    ReadOnlyMemory<byte>.Empty,
                    ReadOnlyMemory<byte>.Empty,
                    [],
                    0,
                    localIdentity);
            }

            var articleSpan = articleBytes.Span;
            Span<NntpArticleHeaderEntry> inlineHeaders = stackalloc NntpArticleHeaderEntry[InlineHeaderCapacity];
            NntpArticleHeaderEntry[]? rentedHeaders = null;
            try
            {
                var headerOutcome = TryParseHeaders(articleSpan, articleBytes, _options, inlineHeaders, ref rentedHeaders);
                var parsedHeaders = headerOutcome.Headers;
                if (!headerOutcome.Success)
                {
                    return NntpArticleParseResult.Rejected(
                        headerOutcome.FailureCode,
                        NntpArticleType.Malformed,
                        articleBytes,
                        headerOutcome.HeaderBytes,
                        headerOutcome.BodyBytes,
                        parsedHeaders,
                        parsedHeaders.Length,
                        localIdentity);
                }

                if (!TryValidateBodyLineLengths(headerOutcome.BodyBytes.Span, _options.MaxHeaderLineBytes, out var bodyLineCount, out var bodyLineFailure))
                {
                    return NntpArticleParseResult.Rejected(
                        bodyLineFailure,
                        NntpArticleType.Malformed,
                        articleBytes,
                        headerOutcome.HeaderBytes,
                        headerOutcome.BodyBytes,
                        parsedHeaders,
                        parsedHeaders.Length,
                        localIdentity);
                }

                if (!TryValidateMessageId(articleSpan, parsedHeaders, articleBytes, out var originalMessageIdValue, out var messageIdFailure))
                {
                    return NntpArticleParseResult.Rejected(
                        messageIdFailure,
                        NntpArticleType.Malformed,
                        articleBytes,
                        headerOutcome.HeaderBytes,
                        headerOutcome.BodyBytes,
                        parsedHeaders,
                        parsedHeaders.Length,
                        localIdentity,
                        originalMessageIdValue: originalMessageIdValue);
                }

                if (!TryValidateNewsgroups(articleSpan, parsedHeaders, out var newsgroupsFailure))
                {
                    return NntpArticleParseResult.Rejected(
                        newsgroupsFailure,
                        NntpArticleType.Malformed,
                        articleBytes,
                        headerOutcome.HeaderBytes,
                        headerOutcome.BodyBytes,
                        parsedHeaders,
                        parsedHeaders.Length,
                        localIdentity);
                }

                if (!TryValidateFrom(articleSpan, parsedHeaders, out var fromFailure))
                {
                    return NntpArticleParseResult.Rejected(
                        fromFailure,
                        NntpArticleType.Malformed,
                        articleBytes,
                        headerOutcome.HeaderBytes,
                        headerOutcome.BodyBytes,
                        parsedHeaders,
                        parsedHeaders.Length,
                        localIdentity);
                }

                if (!ArticleDateHeaderResolver.TryResolve(
                        articleBytes,
                        parsedHeaders,
                        out var canonicalUtc,
                        out var originalDateValue,
                        out var selectedDateHeaderName,
                        out var dateFailure))
                {
                    return NntpArticleParseResult.Rejected(
                        NntpArticleParseFailureCode.MissingOrInvalidDate,
                        NntpArticleType.Malformed,
                        articleBytes,
                        headerOutcome.HeaderBytes,
                        headerOutcome.BodyBytes,
                        parsedHeaders,
                        parsedHeaders.Length,
                        localIdentity,
                        dateFailureReason: dateFailure,
                        originalMessageIdValue: originalMessageIdValue);
                }

                if (!TryAnalyzePath(articleSpan, parsedHeaders, articleBytes, localIdentity.Span, out var pathKind, out var containsOrganizationalTracker, out var originalPathValue, out var pathFailure))
                {
                    return NntpArticleParseResult.Rejected(
                        pathFailure,
                        NntpArticleType.Malformed,
                        articleBytes,
                        headerOutcome.HeaderBytes,
                        headerOutcome.BodyBytes,
                        parsedHeaders,
                        parsedHeaders.Length,
                        localIdentity,
                        dateFailureReason: DateParseFailureReason.None,
                        canonicalUtc: canonicalUtc,
                        originalDateValue: originalDateValue,
                        selectedDateHeaderName: selectedDateHeaderName,
                        originalMessageIdValue: originalMessageIdValue);
                }

                var yEncDetected = DetectYEnc(headerOutcome.BodyBytes.Span, _options.YEncDetectionScanBytes);
                var articleType = ClassifyArticle(articleSpan, parsedHeaders, headerOutcome.BodyBytes.Span, yEncDetected);
                var yEncValidation = YEncArticleValidationResult.ValidNonYEnc();
                if (yEncDetected)
                {
                    yEncValidation = YEncArticleValidator.Validate(headerOutcome.BodyBytes.Span);
                    if (!yEncValidation.IsValid)
                    {
                        return NntpArticleParseResult.Rejected(
                            NntpArticleParseFailureCode.YEncDecodingFailed,
                            NntpArticleType.YEnc,
                            articleBytes,
                            headerOutcome.HeaderBytes,
                            headerOutcome.BodyBytes,
                            parsedHeaders,
                            parsedHeaders.Length,
                            localIdentity,
                            dateFailureReason: DateParseFailureReason.None,
                            canonicalUtc: canonicalUtc,
                            originalDateValue: originalDateValue,
                            selectedDateHeaderName: selectedDateHeaderName,
                            pathKind: pathKind,
                            containsOrganizationalTracker: containsOrganizationalTracker,
                            originalPathValue: originalPathValue,
                            originalMessageIdValue: originalMessageIdValue,
                            yEncDetected: true,
                            yEncValidation: yEncValidation);
                    }
                }

                return new NntpArticleParseResult(
                    isAccepted: true,
                    NntpArticleParseFailureCode.None,
                    articleType,
                    articleBytes,
                    headerOutcome.HeaderBytes,
                    headerOutcome.BodyBytes,
                    parsedHeaders,
                    parsedHeaders.Length,
                    DateParseFailureReason.None,
                    canonicalUtc,
                    originalDateValue,
                    selectedDateHeaderName,
                    pathKind,
                    containsOrganizationalTracker,
                    originalPathValue,
                    originalMessageIdValue,
                    yEncDetected,
                    localIdentity,
                    yEncValidation,
                    bodyLineCount);
            }
            finally
            {
                if (rentedHeaders is not null)
                {
                    ArrayPool<NntpArticleHeaderEntry>.Shared.Return(rentedHeaders, clearArray: true);
                }
            }
        }

        private static NntpArticleParserOptions CapOptions(NntpArticleParserOptions options)
            => options with
            {
                MaxArticleBytes = Math.Min(options.MaxArticleBytes, ArticleResourceLimits.MaxArticleBytes),
                MaxHeaderLineBytes = Math.Min(options.MaxHeaderLineBytes, ArticleResourceLimits.MaxArticleLineBytes),
            };

        private static bool IsAllAsciiWhitespace(ReadOnlySpan<byte> value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] is not ((byte)' ' or (byte)'\t'))
                {
                    return false;
                }
            }

            return true;
        }

        private static HeaderParseOutcome TryParseHeaders(
            ReadOnlySpan<byte> articleSpan,
            ReadOnlyMemory<byte> articleBytes,
            NntpArticleParserOptions options,
            Span<NntpArticleHeaderEntry> inlineHeaders,
            ref NntpArticleHeaderEntry[]? rentedHeaders)
        {
            var index = 0;
            const int headerStart = 0;
            var headerBytesScanned = 0;
            var headerCount = 0;
            var headers = inlineHeaders;

            var currentHeaderNameOffset = -1;
            var currentHeaderNameLength = 0;
            var currentHeaderValueOffset = -1;
            var currentHeaderValueEndExclusive = -1;
            var currentKnownName = NntpArticleHeaderName.Unknown;
            var currentHeaderHasValue = false;
            var firstLineChecked = false;

            while (index < articleSpan.Length)
            {
                var maxLineLength = options.MaxHeaderLineBytes;
                var lineEnd = FindLineTerminator(articleSpan, index, maxLineLength + 1);
                var lineContentEnd = lineEnd >= 0 ? lineEnd : Math.Min(articleSpan.Length, index + maxLineLength + 1);
                var lineLength = lineContentEnd - index;
                if (lineEnd < 0 && lineLength > maxLineLength)
                {
                    return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.HeaderLineTooLong, articleBytes, headerStart, index + maxLineLength, headers[..headerCount]);
                }

                if (lineLength > maxLineLength)
                {
                    return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.HeaderLineTooLong, articleBytes, headerStart, index + maxLineLength, headers[..headerCount]);
                }

                if (lineLength == 0)
                {
                    if (currentHeaderNameOffset >= 0
                        && !TryAddHeader(
                            ref headers,
                            ref headerCount,
                            ref rentedHeaders,
                            options.MaxHeaderCount,
                            new NntpArticleHeaderEntry(
                                currentKnownName,
                                currentHeaderNameOffset,
                                currentHeaderNameLength,
                                currentHeaderValueOffset,
                                currentHeaderHasValue ? currentHeaderValueEndExclusive - currentHeaderValueOffset : 0)))
                    {
                        return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.TooManyHeaders, articleBytes, headerStart, lineContentEnd, headers[..headerCount]);
                    }

                    var bodyOffset = lineEnd >= 0 ? AdvancePastTerminator(articleSpan, lineEnd) : lineContentEnd;
                    var headerLength = bodyOffset;
                    return headerLength > options.MaxHeaderSectionBytes
                        ? HeaderParseOutcome.Fail(NntpArticleParseFailureCode.HeaderSectionTooLarge, articleBytes, headerStart, bodyOffset, headers[..headerCount])
                        : HeaderParseOutcome.SuccessResult(
                            articleBytes,
                            articleBytes[..headerLength],
                            articleBytes[bodyOffset..],
                            headers[..headerCount]);
                }

                headerBytesScanned += lineLength;
                if (headerBytesScanned > options.MaxHeaderSectionBytes)
                {
                    return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.HeaderSectionTooLarge, articleBytes, headerStart, lineContentEnd, headers[..headerCount]);
                }

                var line = articleSpan.Slice(index, lineLength);
                if (ContainsIllegalHeaderControl(line))
                {
                    return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.ContainsIllegalControlByte, articleBytes, headerStart, lineContentEnd, headers[..headerCount]);
                }

                if (!firstLineChecked)
                {
                    firstLineChecked = true;
                    var firstColon = line.IndexOf((byte)':');
                    if (firstColon < 0)
                    {
                        return line[0] is (byte)' ' or (byte)'\t'
                            ? HeaderParseOutcome.Fail(NntpArticleParseFailureCode.MalformedHeaderContinuation, articleBytes, headerStart, lineContentEnd, headers[..headerCount])
                            : HeaderParseOutcome.SuccessResult(articleBytes, ReadOnlyMemory<byte>.Empty, articleBytes, []);
                    }
                }

                var continuation = line[0] is (byte)' ' or (byte)'\t';
                if (continuation)
                {
                    if (currentHeaderNameOffset < 0)
                    {
                        return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.MalformedHeaderContinuation, articleBytes, headerStart, lineContentEnd, headers[..headerCount]);
                    }

                    currentHeaderHasValue = true;
                    currentHeaderValueEndExclusive = lineContentEnd;
                    var aggregateHeaderValueLength = currentHeaderValueEndExclusive - currentHeaderValueOffset;
                    if (aggregateHeaderValueLength > options.MaxHeaderValueBytes)
                    {
                        return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.HeaderValueTooLong, articleBytes, headerStart, index + maxLineLength, headers[..headerCount]);
                    }

                    index = lineEnd >= 0 ? AdvancePastTerminator(articleSpan, lineEnd) : articleSpan.Length;
                    continue;
                }

                if (currentHeaderNameOffset >= 0)
                {
                    if (!TryAddHeader(
                            ref headers,
                            ref headerCount,
                            ref rentedHeaders,
                            options.MaxHeaderCount,
                            new NntpArticleHeaderEntry(
                                currentKnownName,
                                currentHeaderNameOffset,
                                currentHeaderNameLength,
                                currentHeaderValueOffset,
                                currentHeaderHasValue ? currentHeaderValueEndExclusive - currentHeaderValueOffset : 0)))
                    {
                        return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.TooManyHeaders, articleBytes, headerStart, lineContentEnd, headers[..headerCount]);
                    }
                }

                if (headerCount >= options.MaxHeaderCount)
                {
                    return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.TooManyHeaders, articleBytes, headerStart, lineContentEnd, headers[..headerCount]);
                }

                var colonIndex = line.IndexOf((byte)':');
                if (colonIndex < 0)
                {
                    return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.MissingHeaderBodySeparator, articleBytes, headerStart, lineContentEnd, headers[..headerCount]);
                }

                if (colonIndex == 0)
                {
                    return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.MalformedHeader, articleBytes, headerStart, lineContentEnd, headers[..headerCount]);
                }

                if (colonIndex > options.MaxHeaderNameBytes)
                {
                    return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.HeaderNameTooLong, articleBytes, headerStart, lineContentEnd, headers[..headerCount]);
                }

                var valueStartInLine = colonIndex + 1;
                while (valueStartInLine < line.Length && line[valueStartInLine] is (byte)' ' or (byte)'\t')
                {
                    valueStartInLine++;
                }

                var valueEndInLine = line.Length;
                while (valueEndInLine > valueStartInLine && line[valueEndInLine - 1] is (byte)' ' or (byte)'\t')
                {
                    valueEndInLine--;
                }

                var headerValueLength = valueEndInLine - valueStartInLine;
                if (headerValueLength > options.MaxHeaderValueBytes)
                {
                    return HeaderParseOutcome.Fail(NntpArticleParseFailureCode.HeaderValueTooLong, articleBytes, headerStart, lineContentEnd, headers[..headerCount]);
                }

                currentHeaderNameOffset = index;
                currentHeaderNameLength = colonIndex;
                currentHeaderValueOffset = index + valueStartInLine;
                currentHeaderValueEndExclusive = index + valueEndInLine;
                currentHeaderHasValue = headerValueLength > 0;
                currentKnownName = ClassifyKnownHeaderName(articleSpan.Slice(currentHeaderNameOffset, currentHeaderNameLength));
                index = lineEnd >= 0 ? AdvancePastTerminator(articleSpan, lineEnd) : articleSpan.Length;
            }

            return HeaderParseOutcome.Fail(
                NntpArticleParseFailureCode.MissingHeaderBodySeparator,
                articleBytes,
                headerStart,
                articleBytes.Length,
                headers[..headerCount]);
        }

        private static bool TryAddHeader(
            ref Span<NntpArticleHeaderEntry> headers,
            ref int headerCount,
            ref NntpArticleHeaderEntry[]? rentedHeaders,
            int maxHeaderCount,
            NntpArticleHeaderEntry entry)
        {
            if (headerCount >= maxHeaderCount)
            {
                return false;
            }

            if (headerCount >= headers.Length)
            {
                var rented = ArrayPool<NntpArticleHeaderEntry>.Shared.Rent(maxHeaderCount);
                headers.CopyTo(rented);
                if (rentedHeaders is not null)
                {
                    ArrayPool<NntpArticleHeaderEntry>.Shared.Return(rentedHeaders, clearArray: true);
                }

                rentedHeaders = rented;
                headers = rented;
            }

            headers[headerCount++] = entry;
            return true;
        }

        private static NntpArticleHeaderName ClassifyKnownHeaderName(ReadOnlySpan<byte> nameBytes)
            => AsciiEqualsIgnoreCase(nameBytes, "Date"u8)
                ? NntpArticleHeaderName.Date
                : AsciiEqualsIgnoreCase(nameBytes, "Injection-Date"u8)
                    ? NntpArticleHeaderName.InjectionDate
                    : AsciiEqualsIgnoreCase(nameBytes, "NNTP-Posting-Date"u8)
                        ? NntpArticleHeaderName.NntpPostingDate
                        : AsciiEqualsIgnoreCase(nameBytes, "Posted"u8)
                            ? NntpArticleHeaderName.Posted
                            : AsciiEqualsIgnoreCase(nameBytes, "X-Date"u8)
                                ? NntpArticleHeaderName.XDate
                                : AsciiEqualsIgnoreCase(nameBytes, "Delivery-Date"u8)
                                    ? NntpArticleHeaderName.DeliveryDate
                                    : AsciiEqualsIgnoreCase(nameBytes, "Path"u8)
                                        ? NntpArticleHeaderName.Path
                                        : AsciiEqualsIgnoreCase(nameBytes, "Message-ID"u8)
                                            ? NntpArticleHeaderName.MessageId
                                            : AsciiEqualsIgnoreCase(nameBytes, "Newsgroups"u8)
                                                ? NntpArticleHeaderName.Newsgroups
                                                : AsciiEqualsIgnoreCase(nameBytes, "From"u8)
                                                    ? NntpArticleHeaderName.From
                                                    : AsciiEqualsIgnoreCase(nameBytes, "Subject"u8)
                                                        ? NntpArticleHeaderName.Subject
                                                        : AsciiEqualsIgnoreCase(nameBytes, "Content-Type"u8)
                                                            ? NntpArticleHeaderName.ContentType
                                                            : AsciiEqualsIgnoreCase(nameBytes, "Content-Transfer-Encoding"u8)
                                                                ? NntpArticleHeaderName.ContentTransferEncoding
                                                                : AsciiEqualsIgnoreCase(nameBytes, "References"u8)
                                                                    ? NntpArticleHeaderName.References
                                                                    : NntpArticleHeaderName.Unknown;

        private static bool TryValidateMessageId(
            ReadOnlySpan<byte> articleSpan,
            ReadOnlySpan<NntpArticleHeaderEntry> headers,
            ReadOnlyMemory<byte> articleBytes,
            out ReadOnlyMemory<byte> messageIdBytes,
            out NntpArticleParseFailureCode failureCode)
        {
            messageIdBytes = default;
            failureCode = NntpArticleParseFailureCode.None;

            var messageIdHeader = default(NntpArticleHeaderEntry);
            var found = false;
            for (var i = 0; i < headers.Length; i++)
            {
                var entry = headers[i];
                if (entry.KnownName != NntpArticleHeaderName.MessageId)
                {
                    continue;
                }

                if (found)
                {
                    failureCode = NntpArticleParseFailureCode.DuplicateMessageId;
                    return false;
                }

                found = true;
                messageIdHeader = entry;
            }

            if (!found)
            {
                failureCode = NntpArticleParseFailureCode.MissingMessageId;
                return false;
            }

            var rawValue = articleSpan.Slice(messageIdHeader.ValueOffset, messageIdHeader.ValueLength);
            Span<byte> unfolded = stackalloc byte[NntpMessageIdValidation.MaxMessageIdLength];
            if (!NntpArticleHeaderValueUnfolder.TryUnfold(rawValue, unfolded, out var unfoldedLength))
            {
                failureCode = NntpArticleParseFailureCode.InvalidMessageId;
                return false;
            }

            if (!NntpMessageIdValidation.IsValidMessageId(unfolded[..unfoldedLength]))
            {
                failureCode = NntpArticleParseFailureCode.InvalidMessageId;
                return false;
            }

            messageIdBytes = articleBytes.Slice(messageIdHeader.ValueOffset, messageIdHeader.ValueLength);
            return true;
        }

        private static bool TryValidateNewsgroups(
            ReadOnlySpan<byte> articleSpan,
            ReadOnlySpan<NntpArticleHeaderEntry> headers,
            out NntpArticleParseFailureCode failureCode)
        {
            failureCode = NntpArticleParseFailureCode.None;
            var newsgroupsHeader = default(NntpArticleHeaderEntry);
            var found = false;
            for (var i = 0; i < headers.Length; i++)
            {
                var entry = headers[i];
                if (entry.KnownName != NntpArticleHeaderName.Newsgroups)
                {
                    continue;
                }

                if (found)
                {
                    failureCode = NntpArticleParseFailureCode.DuplicateNewsgroups;
                    return false;
                }

                found = true;
                newsgroupsHeader = entry;
            }

            if (!found)
            {
                failureCode = NntpArticleParseFailureCode.MissingNewsgroups;
                return false;
            }

            if (newsgroupsHeader.ValueLength is 0 or > MaxNewsgroupsLength)
            {
                failureCode = NntpArticleParseFailureCode.InvalidNewsgroups;
                return false;
            }

            var value = articleSpan.Slice(newsgroupsHeader.ValueOffset, newsgroupsHeader.ValueLength);
            var tokenHasChar = false;
            for (var i = 0; i < value.Length; i++)
            {
                var b = value[i];
                if (b == (byte)',')
                {
                    if (!tokenHasChar)
                    {
                        failureCode = NntpArticleParseFailureCode.InvalidNewsgroups;
                        return false;
                    }

                    tokenHasChar = false;
                    continue;
                }

                if (b is (byte)' ' or (byte)'\t')
                {
                    continue;
                }

                if (!IsPlausibleNewsgroupChar(b))
                {
                    failureCode = NntpArticleParseFailureCode.InvalidNewsgroups;
                    return false;
                }

                tokenHasChar = true;
            }

            if (!tokenHasChar)
            {
                failureCode = NntpArticleParseFailureCode.InvalidNewsgroups;
                return false;
            }

            return true;
        }

        private static bool TryValidateFrom(
            ReadOnlySpan<byte> articleSpan,
            ReadOnlySpan<NntpArticleHeaderEntry> headers,
            out NntpArticleParseFailureCode failureCode)
        {
            failureCode = NntpArticleParseFailureCode.None;
            Span<byte> unfoldedFromBuffer = stackalloc byte[MaxFromLength];
            for (var i = 0; i < headers.Length; i++)
            {
                var entry = headers[i];
                if (entry.KnownName != NntpArticleHeaderName.From)
                {
                    continue;
                }

                if (entry.ValueLength == 0)
                {
                    failureCode = NntpArticleParseFailureCode.InvalidFrom;
                    return false;
                }

                var rawValue = articleSpan.Slice(entry.ValueOffset, entry.ValueLength);
                if (!NntpArticleHeaderValueUnfolder.TryUnfold(rawValue, unfoldedFromBuffer, out var unfoldedLength))
                {
                    failureCode = NntpArticleParseFailureCode.InvalidFrom;
                    return false;
                }

                var value = unfoldedFromBuffer[..unfoldedLength];
                var at = value.IndexOf((byte)'@');
                if (at <= 0 || at >= value.Length - 1)
                {
                    failureCode = NntpArticleParseFailureCode.InvalidFrom;
                    return false;
                }

                var hasPrintable = false;
                for (var c = 0; c < value.Length; c++)
                {
                    var b = value[c];
                    if (b is < 0x20 or > 0x7E)
                    {
                        failureCode = NntpArticleParseFailureCode.InvalidFrom;
                        return false;
                    }

                    if (b is not ((byte)' ' or (byte)'\t'))
                    {
                        hasPrintable = true;
                    }
                }

                if (!hasPrintable)
                {
                    failureCode = NntpArticleParseFailureCode.InvalidFrom;
                    return false;
                }
            }

            return true;
        }

        private static bool TryAnalyzePath(
            ReadOnlySpan<byte> articleSpan,
            ReadOnlySpan<NntpArticleHeaderEntry> headers,
            ReadOnlyMemory<byte> articleBytes,
            ReadOnlySpan<byte> localIdentity,
            out ArticlePathKind pathKind,
            out bool containsOrganizationalTracker,
            out ReadOnlyMemory<byte> originalPathValue,
            out NntpArticleParseFailureCode failureCode)
        {
            pathKind = ArticlePathKind.Missing;
            containsOrganizationalTracker = false;
            originalPathValue = default;
            failureCode = NntpArticleParseFailureCode.None;

            var pathHeader = default(NntpArticleHeaderEntry);
            var found = false;
            for (var i = 0; i < headers.Length; i++)
            {
                var entry = headers[i];
                if (entry.KnownName != NntpArticleHeaderName.Path)
                {
                    continue;
                }

                if (found)
                {
                    failureCode = NntpArticleParseFailureCode.DuplicatePath;
                    return false;
                }

                found = true;
                pathHeader = entry;
            }

            var rawPath = found
                ? articleSpan.Slice(pathHeader.ValueOffset, pathHeader.ValueLength)
                : ReadOnlySpan<byte>.Empty;
            if (!ArticlePathCanonicalizer.TryAnalyze(rawPath, localIdentity, found, out pathKind, out containsOrganizationalTracker, out failureCode))
            {
                return false;
            }

            if (found)
            {
                originalPathValue = articleBytes.Slice(pathHeader.ValueOffset, pathHeader.ValueLength);
            }

            return true;
        }

        private static NntpArticleType ClassifyArticle(
            ReadOnlySpan<byte> articleSpan,
            ReadOnlySpan<NntpArticleHeaderEntry> headers,
            ReadOnlySpan<byte> body,
            bool yEncDetected)
        {
            var mimeMultipart = false;
            var binaryTransferEncoding = false;
            for (var i = 0; i < headers.Length; i++)
            {
                var header = headers[i];
                if (header.KnownName == NntpArticleHeaderName.ContentType)
                {
                    var value = articleSpan.Slice(header.ValueOffset, header.ValueLength);
                    if (IndexOfAsciiIgnoreCase(value, MultipartMarker) >= 0)
                    {
                        mimeMultipart = true;
                    }
                }
                else if (header.KnownName == NntpArticleHeaderName.ContentTransferEncoding)
                {
                    var value = articleSpan.Slice(header.ValueOffset, header.ValueLength);
                    if (IndexOfAsciiIgnoreCase(value, Base64Encoding) >= 0 || IndexOfAsciiIgnoreCase(value, BinaryEncoding) >= 0)
                    {
                        binaryTransferEncoding = true;
                    }
                }
            }

            return yEncDetected
                ? NntpArticleType.YEnc
                : mimeMultipart
                    ? NntpArticleType.MimeMultipart
                    : binaryTransferEncoding
                        ? NntpArticleType.BinaryEncoded
                        : IsLikelyTextBody(body) ? NntpArticleType.Text : NntpArticleType.Unknown;
        }

        private static bool TryValidateBodyLineLengths(
            ReadOnlySpan<byte> body,
            int maxLineBytes,
            out int lineCount,
            out NntpArticleParseFailureCode failureCode)
        {
            failureCode = NntpArticleParseFailureCode.None;
            lineCount = 0;
            var position = 0;
            while (position < body.Length)
            {
                var maxScanBytes = Math.Min(maxLineBytes + 1, body.Length - position);
                var lineEnd = -1;
                for (var i = 0; i < maxScanBytes; i++)
                {
                    var current = body[position + i];
                    if (current is (byte)'\r' or (byte)'\n')
                    {
                        lineEnd = position + i;
                        break;
                    }
                }

                var lineContentEnd = lineEnd >= 0 ? lineEnd : position + maxScanBytes;
                var lineLength = lineContentEnd - position;
                if (lineLength > maxLineBytes)
                {
                    failureCode = NntpArticleParseFailureCode.BodyLineTooLong;
                    return false;
                }

                lineCount++;
                if (lineEnd < 0)
                {
                    return true;
                }

                position = AdvancePastTerminator(body, lineEnd);
            }

            return true;
        }

        private static bool DetectYEnc(ReadOnlySpan<byte> body, int maxScanBytes)
        {
            var scanLength = Math.Min(body.Length, maxScanBytes);
            var position = 0;
            while (position < scanLength)
            {
                var lineEnd = FindLineTerminator(body, position, scanLength - position);
                var lineContentEnd = lineEnd >= 0 ? lineEnd : scanLength;
                var line = body[position..lineContentEnd];
                if (line.StartsWith(YEncBeginMarker))
                {
                    return true;
                }

                if (lineEnd < 0)
                {
                    break;
                }

                position = AdvancePastTerminator(body, lineEnd);
            }

            return false;
        }

        private static bool IsLikelyTextBody(ReadOnlySpan<byte> body)
        {
            var sampleLength = Math.Min(body.Length, 4096);
            if (sampleLength == 0)
            {
                return true;
            }

            var suspicious = 0;
            for (var i = 0; i < sampleLength; i++)
            {
                var b = body[i];
                if (b == 0)
                {
                    return false;
                }

                if (b is < 0x09 or (> 0x0D and < 0x20))
                {
                    suspicious++;
                }
            }

            return sampleLength < 16 ? suspicious == 0 : suspicious < (sampleLength / 16);
        }

        private static int FindLineTerminator(ReadOnlySpan<byte> buffer, int start, int maximumScanBytes = int.MaxValue)
        {
            var boundedScanBytes = Math.Max(0, maximumScanBytes);
            var endExclusive = boundedScanBytes == int.MaxValue
                ? buffer.Length
                : Math.Min(buffer.Length, start + boundedScanBytes);

            for (var i = start; i < endExclusive; i++)
            {
                var b = buffer[i];
                if (b == (byte)'\r' || b == (byte)'\n')
                {
                    return i;
                }

                if (b == 0)
                {
                    return -2;
                }
            }

            return -1;
        }

        private static int AdvancePastTerminator(ReadOnlySpan<byte> buffer, int lineTerminatorIndex)
            => lineTerminatorIndex < 0 || lineTerminatorIndex >= buffer.Length
                ? buffer.Length
                : buffer[lineTerminatorIndex] == (byte)'\r'
                  && lineTerminatorIndex + 1 < buffer.Length
                  && buffer[lineTerminatorIndex + 1] == (byte)'\n'
                    ? lineTerminatorIndex + 2
                    : lineTerminatorIndex + 1;

        private static bool ContainsIllegalHeaderControl(ReadOnlySpan<byte> line)
        {
            for (var i = 0; i < line.Length; i++)
            {
                var b = line[i];
                if (b == 0)
                {
                    return true;
                }

                if (b is < 0x20 and not ((byte)'\t'))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsPlausibleNewsgroupChar(byte b)
            => b is (>= ((byte)'a') and <= ((byte)'z'))
                or (>= ((byte)'A') and <= ((byte)'Z'))
                or (>= ((byte)'0') and <= ((byte)'9'))
                or (byte)'.' or (byte)'-' or (byte)'_' or (byte)'+';

        private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (var i = 0; i < left.Length; i++)
            {
                if (ToLowerAscii(left[i]) != ToLowerAscii(right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static int IndexOfAsciiIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
        {
            if (needle.Length == 0)
            {
                return 0;
            }

            if (needle.Length > haystack.Length)
            {
                return -1;
            }

            var max = haystack.Length - needle.Length;
            for (var i = 0; i <= max; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (ToLowerAscii(haystack[i + j]) != ToLowerAscii(needle[j]))
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return i;
                }
            }

            return -1;
        }

        private static byte ToLowerAscii(byte value)
            => (uint)(value - (byte)'A') <= 'Z' - 'A' ? (byte)(value + 32) : value;

        private readonly ref struct HeaderParseOutcome
        {
            private HeaderParseOutcome(
                bool success,
                NntpArticleParseFailureCode failureCode,
                ReadOnlyMemory<byte> articleBytes,
                ReadOnlyMemory<byte> headerBytes,
                ReadOnlyMemory<byte> bodyBytes,
                ReadOnlySpan<NntpArticleHeaderEntry> headers)
            {
                Success = success;
                FailureCode = failureCode;
                ArticleBytes = articleBytes;
                HeaderBytes = headerBytes;
                BodyBytes = bodyBytes;
                Headers = headers;
            }

            internal bool Success { get; }

            internal NntpArticleParseFailureCode FailureCode { get; }

            private ReadOnlyMemory<byte> ArticleBytes { get; }

            internal ReadOnlyMemory<byte> HeaderBytes { get; }

            internal ReadOnlyMemory<byte> BodyBytes { get; }

            internal ReadOnlySpan<NntpArticleHeaderEntry> Headers { get; }

            internal static HeaderParseOutcome SuccessResult(
                ReadOnlyMemory<byte> articleBytes,
                ReadOnlyMemory<byte> headerBytes,
                ReadOnlyMemory<byte> bodyBytes,
                ReadOnlySpan<NntpArticleHeaderEntry> headers)
                => new(true, NntpArticleParseFailureCode.None, articleBytes, headerBytes, bodyBytes, headers);

            internal static HeaderParseOutcome Fail(
                NntpArticleParseFailureCode failureCode,
                ReadOnlyMemory<byte> article,
                int headerOffset,
                int bodyOffset,
                ReadOnlySpan<NntpArticleHeaderEntry> headers)
            {
                var boundedBodyOffset = bodyOffset < 0 ? 0 : bodyOffset > article.Length ? article.Length : bodyOffset;
                return new HeaderParseOutcome(
                    false,
                    failureCode,
                    article,
                    article[headerOffset..boundedBodyOffset],
                    article[boundedBodyOffset..],
                    headers);
            }
        }
    }
}
