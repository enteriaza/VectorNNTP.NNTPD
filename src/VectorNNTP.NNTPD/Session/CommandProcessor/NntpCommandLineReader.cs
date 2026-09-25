using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace VectorNNTP.NNTPD.Session.CommandProcessor;

/// <summary>Reads CRLF-delimited NNTP command lines from a <see cref="PipeReader"/>.</summary>
public static class NntpCommandLineReader
{
    private static readonly byte[] Crlf = "\r\n"u8.ToArray();

    /// <summary>
    /// Reads the next command line (without trailing CRLF). Returns <see langword="null"/> on EOF.
    /// </summary>
    public static async ValueTask<string?> ReadLineAsync(
        PipeReader reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);

        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (TryReadLine(ref buffer, out var line))
            {
                reader.AdvanceTo(buffer.Start, buffer.Start);
                return line;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted)
            {
                return null;
            }
        }
    }

    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out string? line)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (!reader.TryReadTo(out ReadOnlySequence<byte> lineBytes, Crlf))
        {
            line = null;
            return false;
        }

        line = Encoding.ASCII.GetString(lineBytes);
        buffer = buffer.Slice(reader.Position);
        return true;
    }

    /// <summary>
    /// Reads the next command line into <paramref name="scratch"/> (without CRLF).
    /// Returns the number of bytes written, or <c>-1</c> on EOF.
    /// </summary>
    public static async ValueTask<int> ReadLineBytesAsync(
        PipeReader reader,
        byte[] scratch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(scratch);

        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (TryCopyLine(ref buffer, scratch, out var length))
            {
                reader.AdvanceTo(buffer.Start, buffer.Start);
                return length;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted)
            {
                return -1;
            }
        }
    }

    private static bool TryCopyLine(ref ReadOnlySequence<byte> buffer, byte[] scratch, out int length)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (!reader.TryReadTo(out ReadOnlySequence<byte> lineBytes, Crlf))
        {
            length = 0;
            return false;
        }

        length = checked((int)lineBytes.Length);
        if (length > scratch.Length)
        {
            length = scratch.Length;
        }

        lineBytes.Slice(0, length).CopyTo(scratch);
        buffer = buffer.Slice(reader.Position);
        return true;
    }
}
