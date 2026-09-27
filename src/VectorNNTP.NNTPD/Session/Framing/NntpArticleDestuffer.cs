using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace VectorNNTP.NNTPD.Session.Framing;

/// <summary>
/// Dot-unstuffs NNTP multiline article lines into an owned buffer (RFC 3977 §3.1.1).
/// </summary>
/// <remarks>
/// Shared by <see cref="NntpMultilineDataReader"/> and the continuous TAKETHIS scanner so destuff
/// semantics stay identical. Pipe sequences must not be retained after <c>AdvanceTo</c>; callers
/// copy into <see cref="ArrayBufferWriter{T}"/> before releasing Pipe memory.
/// </remarks>
public static class NntpArticleDestuffer
{
    /// <summary>
    /// Appends one destuffed content line plus CRLF, or marks <paramref name="exceeded"/> when
    /// the destuffed article would exceed <paramref name="maxArticleBytes"/>.
    /// </summary>
    public static void AppendUnstuffedLine(
        ArrayBufferWriter<byte> output,
        ReadOnlySequence<byte> lineBytes,
        int maxArticleBytes,
        ref bool exceeded)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (exceeded)
        {
            return;
        }

        var length = (int)lineBytes.Length;
        var leadingDot = NntpDelimiterSearch.FirstByte(lineBytes) == (byte)'.';
        var contentLength = leadingDot ? length - 1 : length;
        var needed = contentLength + 2;
        if (output.WrittenCount + needed > maxArticleBytes)
        {
            exceeded = true;
            return;
        }

        var span = output.GetSpan(needed);
        if (lineBytes.IsSingleSegment)
        {
            var src = lineBytes.FirstSpan;
            if (leadingDot)
            {
                src = src[1..];
            }

            src.CopyTo(span);
        }
        else
        {
            var rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                lineBytes.CopyTo(rented);
                var src = leadingDot ? rented.AsSpan(1, contentLength) : rented.AsSpan(0, contentLength);
                src.CopyTo(span);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        span[contentLength] = (byte)'\r';
        span[contentLength + 1] = (byte)'\n';
        output.Advance(needed);
    }

    /// <summary>
    /// Appends one destuffed content line plus CRLF from a contiguous span.
    /// </summary>
    public static void AppendUnstuffedLine(
        ArrayBufferWriter<byte> output,
        ReadOnlySpan<byte> lineBytes,
        int maxArticleBytes,
        ref bool exceeded)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (exceeded)
        {
            return;
        }

        var leadingDot = lineBytes.Length > 0 && lineBytes[0] == (byte)'.';
        var content = leadingDot ? lineBytes[1..] : lineBytes;
        var needed = content.Length + 2;
        if (output.WrittenCount + needed > maxArticleBytes)
        {
            exceeded = true;
            return;
        }

        var span = output.GetSpan(needed);
        content.CopyTo(span);
        span[content.Length] = (byte)'\r';
        span[content.Length + 1] = (byte)'\n';
        output.Advance(needed);
    }

    /// <summary>
    /// Completes a destuffed article: <see cref="NntpMultilineReadStatus.TooLarge"/> with an empty
    /// payload, or <see cref="NntpMultilineReadStatus.Completed"/> with an owned copy.
    /// </summary>
    public static NntpMultilineReadResult Complete(ArrayBufferWriter<byte> output, bool exceeded)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (exceeded)
        {
            return new NntpMultilineReadResult(NntpMultilineReadStatus.TooLarge, ReadOnlyMemory<byte>.Empty);
        }

        return new NntpMultilineReadResult(
            NntpMultilineReadStatus.Completed,
            output.WrittenMemory.ToArray());
    }

    /// <summary>
    /// Destuffs terminator-omitted stuffed wire into one owned destuffed article buffer.
    /// </summary>
    /// <remarks>
    /// Uses the same line destuff as <see cref="AppendUnstuffedLine(ArrayBufferWriter{byte}, ReadOnlySpan{byte}, int, ref bool)"/>.
    /// Does not classify, parse, or rewrite Date/Path. A line without a CRLF terminator is
    /// ignored, matching <see cref="VectorNNTP.NNTPD.ArticleIngestion.IhaveArticleInterpreter"/>.
    /// </remarks>
    /// <param name="stuffedWire">Stuffed article bytes without the NNTP terminator.</param>
    /// <param name="maxArticleBytes">Destuffed size ceiling.</param>
    /// <param name="destuffed">Owned destuffed bytes when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when destuff completed within the limit.</returns>
    public static bool TryDestuffStuffedWire(
        ReadOnlySpan<byte> stuffedWire,
        int maxArticleBytes,
        [NotNullWhen(true)] out byte[]? destuffed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxArticleBytes, 1);
        var output = new ArrayBufferWriter<byte>(Math.Min(Math.Max(stuffedWire.Length, 1), maxArticleBytes));
        var exceeded = false;
        var offset = 0;
        while (offset < stuffedWire.Length && !exceeded)
        {
            var remaining = stuffedWire[offset..];
            var crlf = remaining.IndexOf(NntpDelimiterSearch.Crlf);
            if (crlf < 0)
            {
                break;
            }

            AppendUnstuffedLine(output, remaining[..crlf], maxArticleBytes, ref exceeded);
            offset += crlf + 2;
        }

        if (exceeded)
        {
            destuffed = null;
            return false;
        }

        destuffed = output.WrittenCount == 0
            ? []
            : output.WrittenMemory.ToArray();
        return true;
    }
}
