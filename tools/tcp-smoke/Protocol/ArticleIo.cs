using System.Security.Cryptography;

namespace VectorNNTP.TcpSmoke.Protocol;

/// <summary>Exact reads and writes for the article stream. One socket write is not one socket read.</summary>
public static class ArticleIo
{
    /// <summary>
    /// Reads <paramref name="buffer"/> completely.
    /// Returns false when the peer closes before the first byte. Throws when the peer closes after a partial read.
    /// </summary>
    public static async ValueTask<bool> ReadExactOrCleanEndAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var read = 0;
        while (read < buffer.Length)
        {
            int count;
            try
            {
                count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("The operation timed out.");
            }

            if (count == 0)
            {
                if (read == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("The peer closed before a complete header or payload arrived.");
            }

            read += count;
        }

        return true;
    }

    /// <summary>Reads and hashes exactly <paramref name="length"/> bytes. Matching bytes are written to <paramref name="destination"/> when it is not null.</summary>
    public static async ValueTask ReadPayloadAsync(
        Stream stream,
        uint length,
        byte[] buffer,
        IncrementalHash hash,
        Stream? destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(hash);
        if (buffer.Length == 0)
        {
            throw new ArgumentException("Read buffer is empty.", nameof(buffer));
        }

        var remaining = (long)length;
        while (remaining > 0)
        {
            var slice = (int)Math.Min(buffer.Length, remaining);
            var count = await stream.ReadAsync(buffer.AsMemory(0, slice), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("The peer closed before the declared payload ended.");
            }

            hash.AppendData(buffer.AsSpan(0, count));
            if (destination is not null)
            {
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }

            remaining -= count;
        }
    }

    /// <summary>Writes one header.</summary>
    public static ValueTask WriteHeaderAsync(Stream stream, ArticleHeader header, CancellationToken cancellationToken)
    {
        var buffer = new byte[ArticleProtocol.HeaderLength];
        header.Write(buffer);
        return stream.WriteAsync(buffer, cancellationToken);
    }
}
