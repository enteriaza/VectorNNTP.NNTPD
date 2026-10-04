using System.IO.Hashing;
using System.Runtime.CompilerServices;

namespace VectorNNTP.Common.Articles.YEnc
{
    /// <summary>
    /// Validates yEnc correctness for transport-normalized NNTP article body bytes without materializing decoded payload output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The validator scans for <c>=ybegin</c> sections, optionally handles <c>=ypart</c>, decodes payload bytes
    /// directly into a streaming CRC accumulator, and validates trailer metadata from <c>=yend</c>.
    /// </para>
    /// <para>
    /// Input must already have NNTP transport framing removed and line-start dot-stuffing normalized by the acquisition layer.
    /// This validator does not perform NNTP transport dot-unstuffing.
    /// </para>
    /// <para>
    /// Control lines are recognized on CRLF and LF-only boundaries via <see cref="ArticleLineScanner"/>;
    /// CR-only framing remains part of the payload and therefore typically yields <see cref="YEncArticleValidationStatus.Truncated"/>.
    /// </para>
    /// <para>
    /// Corrupt or malformed remote article data is reported through <see cref="YEncArticleValidationResult"/>
    /// instead of exceptions.
    /// </para>
    /// <para>
    /// Each section folds decoded bytes with a thread-local <see cref="Crc32"/>. That value is the same IEEE CRC-32
    /// as <see cref="YEncCrc32.Compute"/>. <see cref="YEncCrc32"/> remains the scalar reference and is not used here.
    /// </para>
    /// </remarks>
    internal static class YEncArticleValidator
    {
        /// <summary>yEnc bias. An unescaped payload byte decodes as <c>unchecked((byte)(encoded - 42))</c>.</summary>
        private const int YEncOffset = 42;

        /// <summary>
        /// Extra bias for an escaped byte. The byte after <c>=</c> decodes as <c>unchecked((byte)(next - 42 - 64))</c>.
        /// </summary>
        private const int YEncEscapedByteDelta = 64;

        /// <summary>Decoded-byte stack buffer flushed to the section CRC when full.</summary>
        private const int CrcBatchSize = 512;

        /// <summary>
        /// Production IEEE CRC-32 for the section being validated on this thread.
        /// </summary>
        /// <remarks>
        /// <see cref="Crc32"/> selects its hardware path or its own scalar fallback. One instance cannot be shared:
        /// validation is synchronous and concurrent callers would race the accumulator.
        /// The instance is created on first use. A <c>[ThreadStatic]</c> field initializer runs once for the type, not once per thread.
        /// Every section calls <see cref="Crc32.Reset"/>, which does not allocate.
        /// </remarks>
        [ThreadStatic]
        private static Crc32? t_sectionCrc;

        /// <summary>yEnc escape byte <c>0x3D</c>. It consumes the following payload byte and is not itself decoded.</summary>
        private const byte EscapeChar = (byte)'=';

        /// <summary>Case-sensitive <c>=ybegin </c> prefix, including the required trailing SP.</summary>
        private static ReadOnlySpan<byte> YEncBegin => "=ybegin "u8;

        /// <summary>Case-sensitive <c>=ypart </c> prefix, including the required trailing SP.</summary>
        private static ReadOnlySpan<byte> YEncPart => "=ypart "u8;

        /// <summary>Case-sensitive <c>=yend </c> prefix, including the required trailing SP.</summary>
        private static ReadOnlySpan<byte> YEncEnd => "=yend "u8;

        /// <summary>Multipart trailer key <c> pcrc32=</c>, including the leading SP.</summary>
        private static ReadOnlySpan<byte> YEncPcrc32KeyWithLeadingSpace => " pcrc32="u8;

        /// <summary>Trailer key <c> crc32=</c>, including the leading SP.</summary>
        private static ReadOnlySpan<byte> YEncCrc32KeyWithLeadingSpace => " crc32="u8;

        /// <summary>Size key <c> size=</c>, including the leading SP, used on <c>=ybegin</c> and <c>=yend</c> lines.</summary>
        private static ReadOnlySpan<byte> YEncSizeKeyWithLeadingSpace => " size="u8;

        /// <summary><c>=ypart</c> key <c> begin=</c>, including the leading SP. The value is the 1-based part start.</summary>
        private static ReadOnlySpan<byte> YEncPartBeginKeyWithLeadingSpace => " begin="u8;

        /// <summary><c>=ypart</c> key <c> end=</c>, including the leading SP. The value is the inclusive part end.</summary>
        private static ReadOnlySpan<byte> YEncPartEndKeyWithLeadingSpace => " end="u8;

        /// <summary>
        /// Validates the yEnc sections contained in a transport-normalized NNTP article body.
        /// </summary>
        /// <param name="articleBody">Article body bytes after NNTP transport framing removal and dot-unstuffing.</param>
        /// <returns>Allocation-free validation result containing the terminal status and the number of independently validated sections.</returns>
        /// <remarks>
        /// <para>The validator performs a single forward scan through section metadata and encoded payload lines.</para>
        /// <para>Decoded bytes are streamed directly into CRC computation without allocating a decoded payload buffer.</para>
        /// <para>Multipart success means each encountered section validated independently; it does not reconstruct the complete file across articles.</para>
        /// </remarks>
        internal static YEncArticleValidationResult Validate(ReadOnlySpan<byte> articleBody)
        {
            var position = 0;
            var sectionsValidated = 0;
            var sawMultipart = false;

            while (position < articleBody.Length)
            {
                var beginLineStart = ArticleLineScanner.FindLineStartingWith(articleBody, position, YEncBegin);
                var beginStemLineStart = ArticleLineScanner.FindLineStartingWith(articleBody, position, "=ybegin"u8);

                if (beginStemLineStart >= 0 && (beginLineStart < 0 || beginStemLineStart <= beginLineStart))
                {
                    var beginStemLineEnd = ArticleLineScanner.IndexOfCrLf(articleBody, beginStemLineStart);
                    var beginStemContentEnd = beginStemLineEnd >= 0 ? beginStemLineEnd : articleBody.Length;
                    var beginStemLine = articleBody[beginStemLineStart..beginStemContentEnd];

                    var hasRequiredSpaceAfterYBegin = beginStemLine.Length > "=ybegin"u8.Length && beginStemLine["=ybegin"u8.Length] == (byte)' ';
                    if (!hasRequiredSpaceAfterYBegin)
                    {
                        return new YEncArticleValidationResult(YEncArticleValidationStatus.InvalidMetadata, sectionsValidated);
                    }
                }

                if (beginLineStart < 0)
                {
                    break;
                }

                var beginLineEnd = ArticleLineScanner.IndexOfCrLf(articleBody, beginLineStart);
                if (beginLineEnd < 0)
                {
                    return new YEncArticleValidationResult(YEncArticleValidationStatus.Truncated, sectionsValidated);
                }

                var beginLine = articleBody[beginLineStart..beginLineEnd];
                if (!TryParseYBeginSize(beginLine, out var yBeginDeclaredSize))
                {
                    return new YEncArticleValidationResult(YEncArticleValidationStatus.InvalidMetadata, sectionsValidated);
                }

                var payloadStart = ArticleLineScanner.AdvancePastLineTerminator(articleBody, beginLineEnd);
                var isMultipart = false;
                long partBegin = 0;
                long partEnd = 0;

                if (payloadStart < articleBody.Length && articleBody[payloadStart..].StartsWith(YEncPart))
                {
                    isMultipart = true;
                    var partLineEnd = ArticleLineScanner.IndexOfCrLf(articleBody, payloadStart);
                    if (partLineEnd < 0)
                    {
                        return new YEncArticleValidationResult(YEncArticleValidationStatus.Truncated, sectionsValidated);
                    }

                    var partLine = articleBody[payloadStart..partLineEnd];
                    if (!TryParseYPartRange(partLine, out partBegin, out partEnd))
                    {
                        return new YEncArticleValidationResult(YEncArticleValidationStatus.InvalidMetadata, sectionsValidated);
                    }

                    if (partBegin <= 0 || partEnd < partBegin)
                    {
                        return new YEncArticleValidationResult(YEncArticleValidationStatus.InvalidMetadata, sectionsValidated);
                    }

                    if (yBeginDeclaredSize >= 0 && partEnd > yBeginDeclaredSize)
                    {
                        return new YEncArticleValidationResult(YEncArticleValidationStatus.InvalidMetadata, sectionsValidated);
                    }

                    payloadStart = ArticleLineScanner.AdvancePastLineTerminator(articleBody, partLineEnd);
                    sawMultipart = true;
                }

                if (!TryFindAndParseYEncEndLine(articleBody, payloadStart, isMultipart, out var endMetadata, out var endFailureStatus))
                {
                    return new YEncArticleValidationResult(endFailureStatus, sectionsValidated);
                }

                var encodedPayload = articleBody[payloadStart..endMetadata.LineStart];
                var decodeStatus = TryComputeDecodedCrc32AndLength(encodedPayload, out var computedCrc32, out var decodedByteCount);
                if (decodeStatus != YEncArticleValidationStatus.ValidSinglePart)
                {
                    return new YEncArticleValidationResult(decodeStatus, sectionsValidated);
                }

                if (decodedByteCount != endMetadata.DeclaredSize)
                {
                    return new YEncArticleValidationResult(YEncArticleValidationStatus.DecodedSizeMismatch, sectionsValidated);
                }

                if (isMultipart)
                {
                    var expectedPartSize = partEnd - partBegin + 1;
                    if (decodedByteCount != expectedPartSize)
                    {
                        return new YEncArticleValidationResult(YEncArticleValidationStatus.DecodedSizeMismatch, sectionsValidated);
                    }
                }
                else if (yBeginDeclaredSize >= 0 && yBeginDeclaredSize != endMetadata.DeclaredSize)
                {
                    return new YEncArticleValidationResult(YEncArticleValidationStatus.InvalidMetadata, sectionsValidated);
                }

                if (computedCrc32 != endMetadata.DeclaredCrc32)
                {
                    return new YEncArticleValidationResult(YEncArticleValidationStatus.CrcMismatch, sectionsValidated);
                }

                sectionsValidated++;
                position = endMetadata.NextOffset;
            }

            if (sectionsValidated == 0)
            {
                return YEncArticleValidationResult.ValidNonYEnc();
            }

            var successStatus = sawMultipart
                ? YEncArticleValidationStatus.ValidMultiPart
                : YEncArticleValidationStatus.ValidSinglePart;

            return new YEncArticleValidationResult(successStatus, sectionsValidated);
        }

        /// <summary>
        /// Reads <c> size=</c> from a line that starts with <see cref="YEncBegin"/>.
        /// </summary>
        /// <param name="beginLine">One <c>=ybegin</c> line without its terminator.</param>
        /// <param name="size">Parsed size when the method returns <see langword="true"/>; otherwise 0.</param>
        /// <returns><see langword="false"/> when the prefix or key is missing, the decimal is invalid, or the value is negative. Zero is accepted.</returns>
        private static bool TryParseYBeginSize(ReadOnlySpan<byte> beginLine, out long size)
        {
            if (!beginLine.StartsWith(YEncBegin))
            {
                size = 0;
                return false;
            }

            return TryParseDecimalValue(beginLine, YEncSizeKeyWithLeadingSpace, out size) && size >= 0;
        }

        /// <summary>
        /// Reads <c> begin=</c> and <c> end=</c> from a line that starts with <see cref="YEncPart"/>.
        /// </summary>
        /// <param name="partLine">One <c>=ypart</c> line without its terminator.</param>
        /// <param name="partBegin">Parsed begin value. Not range-checked here.</param>
        /// <param name="partEnd">Parsed end value. Not compared with <paramref name="partBegin"/> here.</param>
        /// <returns><see langword="false"/> when the prefix is missing or either decimal key fails. On that failure both outs are 0 if begin fails; end is 0 if only end fails.</returns>
        private static bool TryParseYPartRange(ReadOnlySpan<byte> partLine, out long partBegin, out long partEnd)
        {
            if (!partLine.StartsWith(YEncPart))
            {
                partBegin = 0;
                partEnd = 0;
                return false;
            }

            if (!TryParseDecimalValue(partLine, YEncPartBeginKeyWithLeadingSpace, out partBegin))
            {
                partEnd = 0;
                return false;
            }

            return TryParseDecimalValue(partLine, YEncPartEndKeyWithLeadingSpace, out partEnd);
        }

        /// <summary>
        /// Finds the next <c>=yend </c> line that carries size and CRC metadata.
        /// </summary>
        /// <param name="body">Article body bytes.</param>
        /// <param name="startOffset">First payload byte after <c>=ybegin</c> or <c>=ypart</c>.</param>
        /// <param name="isMultipart">When <see langword="true"/>, <c>pcrc32</c> is accepted and preferred over a later <c>crc32</c>.</param>
        /// <param name="metadata">End-line location and declared size and CRC on success.</param>
        /// <param name="failureStatus">
        /// <see cref="YEncArticleValidationStatus.ValidSinglePart"/> on success, including multipart sections.
        /// <see cref="YEncArticleValidationStatus.Truncated"/> when no terminator or no candidate remains.
        /// <see cref="YEncArticleValidationStatus.InvalidMetadata"/> when a line starts with <see cref="YEncEnd"/> but is not printable metadata or its tokens do not parse.
        /// Lines that merely contain the prefix and lack size, <c>pcrc32</c>, and <c>crc32</c> are skipped.
        /// </param>
        /// <returns><see langword="true"/> when metadata was parsed. Payload for the section is <c>[startOffset, metadata.LineStart)</c>.</returns>
        private static bool TryFindAndParseYEncEndLine(
            ReadOnlySpan<byte> body,
            int startOffset,
            bool isMultipart,
            out EndLineMetadata metadata,
            out YEncArticleValidationStatus failureStatus)
        {
            var searchOffset = startOffset;

            while (searchOffset < body.Length)
            {
                var candidateStart = ArticleLineScanner.FindLineStartingWith(body, searchOffset, YEncEnd);
                if (candidateStart < 0)
                {
                    metadata = default;
                    failureStatus = YEncArticleValidationStatus.Truncated;
                    return false;
                }

                var candidateEnd = ArticleLineScanner.IndexOfCrLf(body, candidateStart);
                var candidateContentEnd = candidateEnd >= 0 ? candidateEnd : body.Length;
                var candidateLine = body[candidateStart..candidateContentEnd];

                if (!IsLikelyYEncMetadataLine(candidateLine))
                {
                    if (candidateLine.StartsWith(YEncEnd))
                    {
                        metadata = default;
                        failureStatus = candidateEnd < 0
                            ? YEncArticleValidationStatus.Truncated
                            : YEncArticleValidationStatus.InvalidMetadata;
                        return false;
                    }

                    if (candidateEnd < 0)
                    {
                        metadata = default;
                        failureStatus = YEncArticleValidationStatus.Truncated;
                        return false;
                    }

                    searchOffset = ArticleLineScanner.AdvancePastLineTerminator(body, candidateEnd);
                    continue;
                }

                var hasPotentialYEndMetadata = candidateLine.IndexOf(YEncSizeKeyWithLeadingSpace) >= 0
                    || candidateLine.IndexOf(YEncPcrc32KeyWithLeadingSpace) >= 0
                    || candidateLine.IndexOf(YEncCrc32KeyWithLeadingSpace) >= 0;
                if (!hasPotentialYEndMetadata)
                {
                    if (candidateEnd < 0)
                    {
                        metadata = default;
                        failureStatus = YEncArticleValidationStatus.Truncated;
                        return false;
                    }

                    searchOffset = ArticleLineScanner.AdvancePastLineTerminator(body, candidateEnd);
                    continue;
                }

                if (!TryParseYEndMetadata(candidateLine, isMultipart, out var declaredSize, out var declaredCrc32))
                {
                    metadata = default;
                    failureStatus = YEncArticleValidationStatus.InvalidMetadata;
                    return false;
                }

                var nextOffset = candidateEnd >= 0
                    ? ArticleLineScanner.AdvancePastLineTerminator(body, candidateEnd)
                    : body.Length;

                metadata = new EndLineMetadata(candidateStart, nextOffset, declaredSize, declaredCrc32);
                failureStatus = YEncArticleValidationStatus.ValidSinglePart;
                return true;
            }

            metadata = default;
            failureStatus = YEncArticleValidationStatus.Truncated;
            return false;
        }

        /// <summary>
        /// Decodes yEnc payload lines into a streaming CRC and a decoded-byte count. Line endings are not decoded.
        /// </summary>
        /// <param name="encodedPayload">Bytes from after <c>=ybegin</c>/<c>=ypart</c> up to, but not including, the <c>=yend</c> line.</param>
        /// <param name="crc32">Finalized IEEE CRC-32 of the decoded bytes on success, the same value as <see cref="YEncCrc32.Compute"/>; 0 after an invalid escape.</param>
        /// <param name="decodedByteCount">Number of decoded bytes on success; 0 after an invalid escape.</param>
        /// <returns>
        /// <see cref="YEncArticleValidationStatus.ValidSinglePart"/> when every line decodes, including multipart sections.
        /// <see cref="YEncArticleValidationStatus.InvalidEscapeSequence"/> when <c>=</c> is the last byte of a line.
        /// CR-only bytes stay inside the line because <see cref="ArticleLineScanner"/> does not treat lone CR as a break.
        /// </returns>
        private static YEncArticleValidationStatus TryComputeDecodedCrc32AndLength(
            ReadOnlySpan<byte> encodedPayload,
            out uint crc32,
            out long decodedByteCount)
        {
            var sectionCrc = GetSectionCrc();
            Span<byte> decodedBatch = stackalloc byte[CrcBatchSize];
            var batchWriteIndex = 0;
            long decodedCount = 0;
            var lineStart = 0;

            while (lineStart < encodedPayload.Length)
            {
                var lineEnd = ArticleLineScanner.IndexOfCrLf(encodedPayload, lineStart);
                var isFinalLine = lineEnd < 0;
                var lineContentEnd = isFinalLine ? encodedPayload.Length : lineEnd;
                var line = encodedPayload[lineStart..lineContentEnd];

                for (var i = 0; i < line.Length; i++)
                {
                    var current = line[i];
                    byte decoded;

                    if (current == EscapeChar)
                    {
                        if (i + 1 >= line.Length)
                        {
                            crc32 = 0;
                            decodedByteCount = 0;
                            return YEncArticleValidationStatus.InvalidEscapeSequence;
                        }

                        decoded = unchecked((byte)(line[i + 1] - YEncOffset - YEncEscapedByteDelta));
                        i++;
                    }
                    else
                    {
                        decoded = unchecked((byte)(current - YEncOffset));
                    }

                    decodedBatch[batchWriteIndex++] = decoded;
                    decodedCount++;

                    if (batchWriteIndex == CrcBatchSize)
                    {
                        sectionCrc.Append(decodedBatch);
                        batchWriteIndex = 0;
                    }
                }

                lineStart = isFinalLine
                    ? encodedPayload.Length
                    : ArticleLineScanner.AdvancePastLineTerminator(encodedPayload, lineEnd);
            }

            if (batchWriteIndex > 0)
            {
                sectionCrc.Append(decodedBatch[..batchWriteIndex]);
            }

            crc32 = sectionCrc.GetCurrentHashAsUInt32();
            decodedByteCount = decodedCount;
            return YEncArticleValidationStatus.ValidSinglePart;
        }

        /// <summary>
        /// Returns this thread's <see cref="Crc32"/>, reset so the section starts at <c>0xFFFFFFFF</c>.
        /// </summary>
        /// <returns>The calling thread's accumulator. Later sections on this thread reuse it.</returns>
        /// <remarks>Called once per yEnc section. Batches within a section append without another reset.</remarks>
        private static Crc32 GetSectionCrc()
        {
            var crc = t_sectionCrc;
            if (crc is null)
            {
                crc = new Crc32();
                t_sectionCrc = crc;
            }

            crc.Reset();
            return crc;
        }

        /// <summary>
        /// Returns whether <paramref name="line"/> is a printable <c>=yend </c> line long enough to hold a key.
        /// </summary>
        /// <param name="line">Candidate line without its terminator.</param>
        /// <returns>
        /// <see langword="false"/> unless the line starts with <see cref="YEncEnd"/>, is at least eight bytes longer than that prefix,
        /// and every byte is in <c>0x20..0x7E</c>.
        /// </returns>
        private static bool IsLikelyYEncMetadataLine(ReadOnlySpan<byte> line)
        {
            if (!line.StartsWith(YEncEnd) || line.Length < YEncEnd.Length + 8)
            {
                return false;
            }

            for (var i = 0; i < line.Length; i++)
            {
                var b = line[i];
                if (b is < 0x20 or > 0x7E)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Reads the first decimal value for <paramref name="key"/> using <see cref="TryParseStrictDecimalValue"/>.
        /// </summary>
        /// <param name="line">Control line.</param>
        /// <param name="key">Key including its leading SP and trailing <c>=</c>.</param>
        /// <param name="value">Parsed value when the method returns <see langword="true"/>.</param>
        /// <returns>The result of <see cref="TryParseStrictDecimalValue"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryParseDecimalValue(ReadOnlySpan<byte> line, ReadOnlySpan<byte> key, out long value)
            => TryParseStrictDecimalValue(line, key, out value);

        /// <summary>
        /// Parses space-separated <c>key=value</c> tokens on an <c>=yend </c> line.
        /// </summary>
        /// <param name="line">End line without its terminator. Must start with <see cref="YEncEnd"/>.</param>
        /// <param name="isMultipart">
        /// When <see langword="true"/>, either <c>pcrc32</c> or <c>crc32</c> satisfies the CRC requirement, and a present <c>pcrc32</c> wins over <c>crc32</c>.
        /// When <see langword="false"/>, <c>crc32</c> is required and <c>pcrc32</c> does not set the returned CRC.
        /// </param>
        /// <param name="declaredSize"><c>size</c> value. Zero until a valid size token is seen.</param>
        /// <param name="declaredCrc32">CRC selected by the multipart rule. Zero until a selected CRC token is seen.</param>
        /// <returns>
        /// <see langword="false"/> for a missing prefix, a token without <c>=</c>, a repeated size or CRC key, a bad decimal or hex value,
        /// a key other than <c>size</c>, <c>pcrc32</c>, <c>crc32</c>, <c>part</c>, <c>line</c>, <c>name</c>, or <c>total</c>,
        /// a trailing SP, or a missing required size or CRC. <c>name</c> values are not otherwise validated.
        /// </returns>
        private static bool TryParseYEndMetadata(ReadOnlySpan<byte> line, bool isMultipart, out long declaredSize, out uint declaredCrc32)
        {
            declaredSize = 0;
            declaredCrc32 = 0;

            if (!line.StartsWith(YEncEnd))
            {
                return false;
            }

            var tokenStart = YEncEnd.Length;
            var sawSize = false;
            var sawCrc32 = false;
            var sawPcrc32 = false;

            while (tokenStart < line.Length)
            {
                var tokenEnd = line[tokenStart..].IndexOf((byte)' ');
                tokenEnd = tokenEnd < 0 ? line.Length : tokenStart + tokenEnd;

                var token = line[tokenStart..tokenEnd];
                var equalsIndex = token.IndexOf((byte)'=');
                if (equalsIndex <= 0 || equalsIndex == token.Length - 1)
                {
                    return false;
                }

                var key = token[..equalsIndex];
                var valueBytes = token[(equalsIndex + 1)..];

                if (key.SequenceEqual("size"u8))
                {
                    if (sawSize || !TryParseStrictDecimalBytes(valueBytes, out declaredSize))
                    {
                        return false;
                    }

                    sawSize = true;
                }
                else if (key.SequenceEqual("pcrc32"u8))
                {
                    if (sawPcrc32 || !HexUInt32Parser.TryParseHexUInt32(valueBytes, out var pcrc32))
                    {
                        return false;
                    }

                    if (isMultipart)
                    {
                        declaredCrc32 = pcrc32;
                    }

                    sawPcrc32 = true;
                }
                else if (key.SequenceEqual("crc32"u8))
                {
                    if (sawCrc32 || !HexUInt32Parser.TryParseHexUInt32(valueBytes, out var crc32))
                    {
                        return false;
                    }

                    if (!isMultipart || !sawPcrc32)
                    {
                        declaredCrc32 = crc32;
                    }

                    sawCrc32 = true;
                }
                else if (!key.SequenceEqual("part"u8) && !key.SequenceEqual("line"u8) && !key.SequenceEqual("name"u8) && !key.SequenceEqual("total"u8))
                {
                    return false;
                }

                if (tokenEnd == line.Length)
                {
                    break;
                }

                tokenStart = tokenEnd + 1;
                if (tokenStart == line.Length)
                {
                    return false;
                }
            }

            return sawSize && (isMultipart
                ? sawPcrc32 || sawCrc32
                : sawCrc32);
        }

        /// <summary>
        /// Parses the decimal run immediately after the first occurrence of <paramref name="key"/>.
        /// </summary>
        /// <param name="line">Control line.</param>
        /// <param name="key">Search needle, including its leading SP and trailing <c>=</c>.</param>
        /// <param name="value">Parsed non-negative integer when the method returns <see langword="true"/>. Not cleared on overflow failure.</param>
        /// <returns>
        /// <see langword="false"/> when the key is missing, no digit follows it, the value would exceed <see cref="long.MaxValue"/>,
        /// or the run stops on a byte other than SP or the end of the line.
        /// </returns>
        private static bool TryParseStrictDecimalValue(ReadOnlySpan<byte> line, ReadOnlySpan<byte> key, out long value)
        {
            value = 0;

            var keyIndex = line.IndexOf(key);
            if (keyIndex < 0)
            {
                return false;
            }

            var digitIndex = keyIndex + key.Length;
            if (digitIndex >= line.Length)
            {
                return false;
            }

            var i = digitIndex;
            var hasDigits = false;

            for (; i < line.Length; i++)
            {
                var digit = line[i] - (byte)'0';
                if ((uint)digit > 9)
                {
                    break;
                }

                if (value > ((long.MaxValue - digit) / 10))
                {
                    return false;
                }

                value = (value * 10) + digit;
                hasDigits = true;
            }

            return hasDigits && (i >= line.Length || line[i] == (byte)' ');
        }

        /// <summary>
        /// Parses a token that is entirely ASCII digits.
        /// </summary>
        /// <param name="valueBytes">Bytes after <c>=</c>. Empty is rejected.</param>
        /// <param name="value">Parsed value on success; 0 on failure, including overflow and a non-digit.</param>
        /// <returns><see langword="false"/> when any byte is not <c>0-9</c> or the value would exceed <see cref="long.MaxValue"/>.</returns>
        private static bool TryParseStrictDecimalBytes(ReadOnlySpan<byte> valueBytes, out long value)
        {
            value = 0;

            if (valueBytes.IsEmpty)
            {
                return false;
            }

            for (var i = 0; i < valueBytes.Length; i++)
            {
                var digit = valueBytes[i] - (byte)'0';
                if ((uint)digit > 9)
                {
                    value = 0;
                    return false;
                }

                if (value > ((long.MaxValue - digit) / 10))
                {
                    value = 0;
                    return false;
                }

                value = (value * 10) + digit;
            }

            return true;
        }

        /// <summary>
        /// Location and declared trailer values of one accepted <c>=yend</c> line.
        /// </summary>
        /// <param name="LineStart">Index of the <c>=yend</c> line. Payload ends at this index.</param>
        /// <param name="NextOffset">Index just after the line terminator, or the body length when the line has none.</param>
        /// <param name="DeclaredSize"><c>size=</c> value from the trailer.</param>
        /// <param name="DeclaredCrc32"><c>pcrc32</c> when a multipart trailer supplied it; otherwise <c>crc32</c>.</param>
        private readonly record struct EndLineMetadata(
            int LineStart,
            int NextOffset,
            long DeclaredSize,
            uint DeclaredCrc32);
    }
}
