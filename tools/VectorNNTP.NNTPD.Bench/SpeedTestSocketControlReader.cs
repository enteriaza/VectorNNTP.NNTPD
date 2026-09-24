using System.Net.Sockets;
using System.Text;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Reads NNTP control lines from a socket without a <see cref="StreamReader"/>.
/// Leftover bytes after a CRLF stay in the buffer so a later raw drain can count them.
/// </summary>
internal sealed class SpeedTestSocketControlReader
{
    private readonly Socket _socket;
    private readonly byte[] _buffer;
    private int _offset;
    private int _length;

    public SpeedTestSocketControlReader(Socket socket, int bufferBytes = SpeedTestRawPayloadReceiver.BufferBytes)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferBytes);
        _socket = socket;
        _buffer = new byte[bufferBytes];
    }

    public int LeftoverBytes => _length;

    /// <summary>Unread bytes already received past the last consumed control line.</summary>
    public ReadOnlySpan<byte> Unread => _buffer.AsSpan(_offset, _length);

    /// <summary>Drops the first <paramref name="count"/> unread bytes after they have been scanned as payload.</summary>
    public void DiscardUnread(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _length);
        if (count == 0)
        {
            return;
        }

        Advance(count);
    }

    /// <summary>Replaces unread bytes (bytes after a framed payload terminator that belong to <c>291</c>).</summary>
    public void SetUnread(ReadOnlySpan<byte> unread)
    {
        if (unread.Length > _buffer.Length)
        {
            throw new InvalidOperationException("SPEEDTEST leftover after terminator exceeds the reuse buffer.");
        }

        unread.CopyTo(_buffer);
        _offset = 0;
        _length = unread.Length;
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var span = _buffer.AsSpan(_offset, _length);
            var nl = span.IndexOf((byte)'\n');
            if (nl >= 0)
            {
                var end = nl;
                if (end > 0 && span[end - 1] == (byte)'\r')
                {
                    end--;
                }

                var line = Encoding.ASCII.GetString(span[..end]);
                Advance(nl + 1);
                return line;
            }

            Compact();
            if (_length == _buffer.Length)
            {
                throw new InvalidOperationException("SPEEDTEST control line exceeds the reuse buffer.");
            }

            var n = await _socket
                .ReceiveAsync(_buffer.AsMemory(_offset + _length), SocketFlags.None, cancellationToken)
                .ConfigureAwait(false);
            if (n == 0)
            {
                if (_length == 0)
                {
                    return null;
                }

                throw new IOException("SPEEDTEST control line ended without CRLF.");
            }

            _length += n;
        }
    }

    /// <summary>Counts leftover bytes toward a raw payload drain without copying them.</summary>
    public int ConsumeLeftover(long remaining)
    {
        if (remaining <= 0 || _length == 0)
        {
            return 0;
        }

        var take = (int)Math.Min(_length, remaining);
        Advance(take);
        return take;
    }

    private void Advance(int count)
    {
        _offset += count;
        _length -= count;
        if (_length == 0)
        {
            _offset = 0;
        }
    }

    private void Compact()
    {
        if (_offset == 0)
        {
            return;
        }

        if (_length > 0)
        {
            Buffer.BlockCopy(_buffer, _offset, _buffer, 0, _length);
        }

        _offset = 0;
    }
}
