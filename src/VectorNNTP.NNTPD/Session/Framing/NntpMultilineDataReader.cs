using System.Buffers;
using System.IO.Pipelines;

namespace VectorNNTP.NNTPD.Session.Framing;

/// <summary>Result of reading one NNTP multiline data block from a <see cref="PipeReader"/>.</summary>
public enum NntpMultilineReadStatus
{
    /// <summary>Complete article consumed (terminator seen); payload is unstuffed article bytes.</summary>
    Completed = 0,

    /// <summary>Peer disconnected before the terminating dot line; nothing should be enqueued.</summary>
    Incomplete = 1,

    /// <summary>
    /// Article exceeded the configured size limit; remaining bytes through the terminator were discarded.
    /// </summary>
    TooLarge = 2,
}

/// <summary>Outcome of <see cref="NntpMultilineDataReader.ReadArticleAsync"/>.</summary>
/// <param name="Status">Read status.</param>
/// <param name="Payload">Unstuffed article bytes when <see cref="NntpMultilineReadStatus.Completed"/>; otherwise empty.</param>
public readonly record struct NntpMultilineReadResult(
    NntpMultilineReadStatus Status,
    ReadOnlyMemory<byte> Payload);

/// <summary>
/// Reads an NNTP multiline data block (RFC 3977 §3.1.1) with dot-unstuffing.
/// </summary>
/// <remarks>
/// Terminator is a line containing only <c>.</c> (i.e. <c>.CRLF</c>).
/// A leading doubled dot on a content line is reduced to a single leading dot.
/// The terminator line is not included in the payload.
/// </remarks>
public static class NntpMultilineDataReader
{
    private static readonly byte[] Crlf = "\r\n"u8.ToArray();

    /// <summary>
    /// Reads one multiline article from <paramref name="reader"/> up to <paramref name="maxArticleBytes"/>.
    /// </summary>
    public static async ValueTask<NntpMultilineReadResult> ReadArticleAsync(
        PipeReader reader,
        int maxArticleBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxArticleBytes, 1);

        var output = new ArrayBufferWriter<byte>(Math.Min(maxArticleBytes, 64 * 1024));
        var exceeded = false;

        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            while (TryReadLine(ref buffer, out var lineBytes))
            {
                if (IsTerminator(lineBytes))
                {
                    reader.AdvanceTo(buffer.Start);
                    if (exceeded)
                    {
                        return new NntpMultilineReadResult(NntpMultilineReadStatus.TooLarge, ReadOnlyMemory<byte>.Empty);
                    }

                    return new NntpMultilineReadResult(
                        NntpMultilineReadStatus.Completed,
                        output.WrittenMemory.ToArray());
                }

                AppendUnstuffedLine(output, lineBytes, maxArticleBytes, ref exceeded);
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted)
            {
                return new NntpMultilineReadResult(NntpMultilineReadStatus.Incomplete, ReadOnlyMemory<byte>.Empty);
            }
        }
    }

    private static void AppendUnstuffedLine(
        ArrayBufferWriter<byte> output,
        ReadOnlySequence<byte> lineBytes,
        int maxArticleBytes,
        ref bool exceeded)
    {
        if (exceeded)
        {
            return;
        }

        var length = (int)lineBytes.Length;
        var leadingDot = !lineBytes.IsEmpty && lineBytes.First.Span[0] == (byte)'.';
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

    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> lineBytes)
    {
        var seqReader = new SequenceReader<byte>(buffer);
        if (!seqReader.TryReadTo(out lineBytes, Crlf))
        {
            lineBytes = default;
            return false;
        }

        buffer = buffer.Slice(seqReader.Position);
        return true;
    }

    private static bool IsTerminator(ReadOnlySequence<byte> lineBytes)
    {
        if (lineBytes.Length != 1)
        {
            return false;
        }

        return lineBytes.First.Span[0] == (byte)'.';
    }
}
