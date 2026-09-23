namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Prepares the two-buffer TAKETHIS wire image: command line then immutable article.
/// Used for a single vectored <c>Socket.Send</c> without copying the article.
/// </summary>
internal static class TakeThisWireSend
{
    public const int BufferCount = 2;

    public static ArraySegment<byte>[] CreateBuffers(ArraySegment<byte> command, byte[] article)
    {
        ArgumentNullException.ThrowIfNull(article);
        var buffers = new ArraySegment<byte>[BufferCount];
        Bind(buffers, command, article);
        return buffers;
    }

    public static void Bind(ArraySegment<byte>[] buffers, ArraySegment<byte> command, byte[] article)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        ArgumentNullException.ThrowIfNull(article);
        if (buffers.Length != BufferCount)
        {
            throw new ArgumentException($"Vectored send requires exactly {BufferCount} buffers.", nameof(buffers));
        }

        buffers[0] = command;
        buffers[1] = new ArraySegment<byte>(article);
    }

    public static int TotalBytes(ReadOnlySpan<ArraySegment<byte>> buffers)
    {
        var total = 0;
        foreach (var buffer in buffers)
        {
            total += buffer.Count;
        }

        return total;
    }

    public static byte[] Concatenate(ReadOnlySpan<ArraySegment<byte>> buffers)
    {
        var result = new byte[TotalBytes(buffers)];
        var offset = 0;
        foreach (var buffer in buffers)
        {
            buffer.AsSpan().CopyTo(result.AsSpan(offset));
            offset += buffer.Count;
        }

        return result;
    }

    /// <summary>Advance the vectored view after a partial send. Returns remaining bytes.</summary>
    public static int Advance(ArraySegment<byte>[] buffers, int sent)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        ArgumentOutOfRangeException.ThrowIfNegative(sent);
        if (buffers.Length != BufferCount)
        {
            throw new ArgumentException($"Vectored send requires exactly {BufferCount} buffers.", nameof(buffers));
        }

        var remaining = TotalBytes(buffers) - sent;
        if (sent == 0)
        {
            return remaining;
        }

        var first = buffers[0];
        if (sent < first.Count)
        {
            buffers[0] = new ArraySegment<byte>(first.Array!, first.Offset + sent, first.Count - sent);
            return remaining;
        }

        sent -= first.Count;
        var second = buffers[1];
        if (sent > second.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(sent), "Partial send exceeded the vectored payload.");
        }

        buffers[0] = new ArraySegment<byte>(second.Array!, second.Offset + sent, second.Count - sent);
        buffers[1] = default;
        return remaining;
    }
}
