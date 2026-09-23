using System.Buffers;

namespace VectorNNTP.NNTPD.Session.Framing;

/// <summary>
/// Runtime-vectorized delimiter search for NNTP CRLF framing and multiline terminators.
/// </summary>
/// <remarks>
/// Uses runtime <c>ReadOnlySpan&lt;byte&gt;.IndexOf</c> so hardware acceleration is provided
/// by the runtime. A scalar fallback is implicit when vectorization is unavailable.
/// Correctness does not depend on SIMD.
/// </remarks>
public static class NntpDelimiterSearch
{
    /// <summary>CRLF sequence that terminates an NNTP command or article line.</summary>
    public static ReadOnlySpan<byte> Crlf => "\r\n"u8;

    /// <summary>Five-byte article terminator delimiter after a preceding content line.</summary>
    public static ReadOnlySpan<byte> FiveByteTerminator => "\r\n.\r\n"u8;

    /// <summary>Empty-article terminator at the start of a multiline block.</summary>
    public static ReadOnlySpan<byte> EmptyTerminator => ".\r\n"u8;

    /// <summary>Finds the first CRLF in <paramref name="span"/>, or -1.</summary>
    public static int IndexOfCrlf(ReadOnlySpan<byte> span) => span.IndexOf(Crlf);

    /// <summary>Finds the first <c>\r\n.\r\n</c> in <paramref name="span"/>, or -1.</summary>
    public static int IndexOfFiveByteTerminator(ReadOnlySpan<byte> span) => span.IndexOf(FiveByteTerminator);

    /// <summary>
    /// Reads one CRLF-terminated line from <paramref name="buffer"/> (CRLF not included).
    /// </summary>
    /// <returns><see langword="true"/> when a complete line was found.</returns>
    public static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> lineBytes)
    {
        if (buffer.IsSingleSegment)
        {
            var span = buffer.FirstSpan;
            var crlf = IndexOfCrlf(span);
            if (crlf < 0)
            {
                lineBytes = default;
                return false;
            }

            lineBytes = buffer.Slice(0, crlf);
            buffer = buffer.Slice(crlf + 2);
            return true;
        }

        var seqReader = new SequenceReader<byte>(buffer);
        if (!seqReader.TryReadTo(out lineBytes, Crlf))
        {
            lineBytes = default;
            return false;
        }

        buffer = buffer.Slice(seqReader.Position);
        return true;
    }

    /// <summary>Returns whether <paramref name="lineBytes"/> is the NNTP multiline terminator line.</summary>
    public static bool IsTerminatorLine(ReadOnlySequence<byte> lineBytes)
    {
        if (lineBytes.Length != 1)
        {
            return false;
        }

        return FirstByte(lineBytes) == (byte)'.';
    }

    /// <summary>Returns whether <paramref name="line"/> is the NNTP multiline terminator line.</summary>
    public static bool IsTerminatorLine(ReadOnlySpan<byte> line) =>
        line.Length == 1 && line[0] == (byte)'.';

    /// <summary>Returns the first byte of <paramref name="sequence"/>, or -1 when empty.</summary>
    public static int FirstByte(ReadOnlySequence<byte> sequence)
    {
        if (sequence.IsEmpty)
        {
            return -1;
        }

        var first = sequence.FirstSpan;
        if (!first.IsEmpty)
        {
            return first[0];
        }

        foreach (var segment in sequence)
        {
            if (!segment.IsEmpty)
            {
                return segment.Span[0];
            }
        }

        return -1;
    }
}
