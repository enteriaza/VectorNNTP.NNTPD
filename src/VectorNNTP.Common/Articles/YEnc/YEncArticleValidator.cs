using System.Runtime.CompilerServices;

namespace VectorNNTP.Common.Articles.YEnc;

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
/// </remarks>
public static class YEncArticleValidator
{
    private const int YEncOffset = 42;
    private const int YEncEscapedByteDelta = 64;
    private const int CrcBatchSize = 512;
    private const byte EscapeChar = (byte)'=';

    private static ReadOnlySpan<byte> YEncBegin => "=ybegin "u8;

    private static ReadOnlySpan<byte> YEncPart => "=ypart "u8;

    private static ReadOnlySpan<byte> YEncEnd => "=yend "u8;

    private static ReadOnlySpan<byte> YEncPcrc32KeyWithLeadingSpace => " pcrc32="u8;

    private static ReadOnlySpan<byte> YEncCrc32KeyWithLeadingSpace => " crc32="u8;

    private static ReadOnlySpan<byte> YEncSizeKeyWithLeadingSpace => " size="u8;

    private static ReadOnlySpan<byte> YEncPartBeginKeyWithLeadingSpace => " begin="u8;

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
    public static YEncArticleValidationResult Validate(ReadOnlySpan<byte> articleBody)
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

    private static bool TryParseYBeginSize(ReadOnlySpan<byte> beginLine, out long size)
    {
        if (!beginLine.StartsWith(YEncBegin))
        {
            size = 0;
            return false;
        }

        return TryParseDecimalValue(beginLine, YEncSizeKeyWithLeadingSpace, out size) && size >= 0;
    }

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

    private static YEncArticleValidationStatus TryComputeDecodedCrc32AndLength(
        ReadOnlySpan<byte> encodedPayload,
        out uint crc32,
        out long decodedByteCount)
    {
        var crcAccumulator = YEncCrc32.InitialAccumulator;
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
                    crcAccumulator = YEncCrc32.Update(crcAccumulator, decodedBatch);
                    batchWriteIndex = 0;
                }
            }

            lineStart = isFinalLine
                ? encodedPayload.Length
                : ArticleLineScanner.AdvancePastLineTerminator(encodedPayload, lineEnd);
        }

        if (batchWriteIndex > 0)
        {
            crcAccumulator = YEncCrc32.Update(crcAccumulator, decodedBatch[..batchWriteIndex]);
        }

        crc32 = YEncCrc32.Finalize(crcAccumulator);
        decodedByteCount = decodedCount;
        return YEncArticleValidationStatus.ValidSinglePart;
    }

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseDecimalValue(ReadOnlySpan<byte> line, ReadOnlySpan<byte> key, out long value)
        => TryParseStrictDecimalValue(line, key, out value);

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

    private readonly record struct EndLineMetadata(
        int LineStart,
        int NextOffset,
        long DeclaredSize,
        uint DeclaredCrc32);
}
