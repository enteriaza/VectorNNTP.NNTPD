using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace VectorNNTP.NNTPD.Session.Commands;

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
}
