using System.Buffers;
using System.Text;

namespace VectorNNTP.BackFiller.Nntp;

/// <summary>Byte-oriented NNTP line and status helpers.</summary>
internal static class NntpProtocolIo
{
    internal static readonly byte[] Crlf = [(byte)'\r', (byte)'\n'];
    internal static readonly byte[] ArticlePrefix = "ARTICLE "u8.ToArray();
    internal static readonly byte[] AuthInfoUserPrefix = "AUTHINFO USER "u8.ToArray();
    internal static readonly byte[] AuthInfoPassPrefix = "AUTHINFO PASS "u8.ToArray();

    internal static bool TryParseStatus(ReadOnlySpan<byte> line, out int code, out string text)
    {
        code = 0;
        text = string.Empty;
        if (line.Length < 3
            || !IsDigit(line[0])
            || !IsDigit(line[1])
            || !IsDigit(line[2]))
        {
            return false;
        }

        code = ((line[0] - (byte)'0') * 100) + ((line[1] - (byte)'0') * 10) + (line[2] - (byte)'0');
        if (line.Length == 3)
        {
            return true;
        }

        var start = 3;
        if (line[3] == (byte)' ')
        {
            start = 4;
        }

        text = start < line.Length
            ? Encoding.ASCII.GetString(line[start..])
            : string.Empty;
        return true;
    }

    internal static bool TryEncodeAscii(string value, out byte[] bytes)
    {
        bytes = [];
        foreach (var ch in value)
        {
            if (ch > 127)
            {
                return false;
            }
        }

        bytes = Encoding.ASCII.GetBytes(value);
        return true;
    }

    internal static bool HasHeaderBodySeparator(ReadOnlySpan<byte> article)
    {
        var crlfcrlf = "\r\n\r\n"u8;
        var lflf = "\n\n"u8;
        return article.IndexOf(crlfcrlf) >= 0 || article.IndexOf(lflf) >= 0;
    }

    private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';
}

/// <summary>Reads CRLF lines and destuffed ARTICLE payloads from a stream.</summary>
internal sealed class NntpStreamReader
{
    private readonly Stream _stream;
    private readonly byte[] _buffer;
    private int _offset;
    private int _count;

    internal NntpStreamReader(Stream stream, int receiveBufferBytes)
    {
        _stream = stream;
        _buffer = new byte[Math.Max(1024, receiveBufferBytes)];
    }

    internal async Task<byte[]?> ReadLineAsync(int maxBytes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var builder = new ArrayBufferWriter<byte>(256);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (true)
        {
            if (_count == 0)
            {
                _offset = 0;
                _count = await _stream.ReadAsync(_buffer.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
                if (_count == 0)
                {
                    return builder.WrittenCount == 0 ? null : throw new EndOfStreamException("NNTP connection closed mid-line.");
                }
            }

            var span = _buffer.AsSpan(_offset, _count);
            var newline = span.IndexOf((byte)'\n');
            if (newline < 0)
            {
                if (builder.WrittenCount + span.Length > maxBytes)
                {
                    throw new InvalidOperationException("NNTP status line exceeded maximum length.");
                }

                builder.Write(span);
                _count = 0;
                continue;
            }

            var lineEnd = newline;
            if (lineEnd > 0 && span[lineEnd - 1] == (byte)'\r')
            {
                lineEnd--;
            }

            if (builder.WrittenCount + lineEnd > maxBytes)
            {
                throw new InvalidOperationException("NNTP status line exceeded maximum length.");
            }

            builder.Write(span[..lineEnd]);
            var consumed = newline + 1;
            _offset += consumed;
            _count -= consumed;
            return builder.WrittenSpan.ToArray();
        }
    }

    internal async Task<byte[]> ReadArticlePayloadAsync(
        int maxBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var builder = new ArrayBufferWriter<byte>(4096);
        var atLineStart = true;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (true)
        {
            if (_count == 0)
            {
                _offset = 0;
                _count = await _stream.ReadAsync(_buffer.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
                if (_count == 0)
                {
                    throw new EndOfStreamException("NNTP article response ended before terminator line.");
                }
            }

            var span = _buffer.AsSpan(_offset, _count);
            var index = 0;
            var needMore = false;
            while (index < span.Length)
            {
                var current = span[index];
                if (atLineStart && current == (byte)'.')
                {
                    if (index + 1 >= span.Length
                        || (span[index + 1] == (byte)'\r' && index + 2 >= span.Length))
                    {
                        needMore = true;
                        break;
                    }

                    var next = span[index + 1];
                    if (next == (byte)'.')
                    {
                        Append(builder, (byte)'.', maxBytes);
                        atLineStart = false;
                        index += 2;
                        continue;
                    }

                    if (next == (byte)'\n')
                    {
                        Consume(index + 2);
                        return builder.WrittenSpan.ToArray();
                    }

                    if (next == (byte)'\r')
                    {
                        if (span[index + 2] == (byte)'\n')
                        {
                            Consume(index + 3);
                            return builder.WrittenSpan.ToArray();
                        }

                        Append(builder, (byte)'.', maxBytes);
                        atLineStart = false;
                        index += 1;
                        continue;
                    }

                    Append(builder, (byte)'.', maxBytes);
                    atLineStart = false;
                    index += 1;
                    continue;
                }

                Append(builder, current, maxBytes);
                atLineStart = current is (byte)'\r' or (byte)'\n';
                index++;
            }

            Consume(index);
            if (needMore)
            {
                await ReadMorePreservingLeftoverAsync(timeoutCts.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task ReadMorePreservingLeftoverAsync(CancellationToken cancellationToken)
    {
        if (_offset > 0 && _count > 0)
        {
            Buffer.BlockCopy(_buffer, _offset, _buffer, 0, _count);
            _offset = 0;
        }
        else if (_count == 0)
        {
            _offset = 0;
        }

        var additional = await _stream.ReadAsync(_buffer.AsMemory(_count), cancellationToken).ConfigureAwait(false);
        if (additional == 0)
        {
            throw new EndOfStreamException("NNTP article response ended before terminator line.");
        }

        _count += additional;
    }

    private void Consume(int count)
    {
        _offset += count;
        _count -= count;
    }

    private static void Append(ArrayBufferWriter<byte> builder, byte value, int maxBytes)
    {
        if (builder.WrittenCount >= maxBytes)
        {
            throw new InvalidOperationException("NNTP article exceeded MaxArticleBytes.");
        }

        builder.GetSpan(1)[0] = value;
        builder.Advance(1);
    }
}
