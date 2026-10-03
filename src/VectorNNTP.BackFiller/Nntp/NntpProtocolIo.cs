using System.Buffers;
using System.Text;

namespace VectorNNTP.BackFiller.Nntp;

/// <summary>
/// Byte-oriented helpers for NNTP status lines, ASCII command fields, capability labels, and dot-stuffed lines.
/// </summary>
internal static class NntpProtocolIo
{
    /// <summary>CRLF sequence appended to outbound NNTP commands.</summary>
    internal static readonly byte[] Crlf = [(byte)'\r', (byte)'\n'];

    /// <summary>Pre-encoded <c>DATE</c> command, including the terminating CRLF.</summary>
    internal static readonly byte[] DateCommand = "DATE\r\n"u8.ToArray();

    /// <summary>
    /// Pre-encoded <c>ARTICLE </c> prefix. The caller appends the message-id bytes and <see cref="Crlf"/>.
    /// </summary>
    internal static readonly byte[] ArticlePrefix = "ARTICLE "u8.ToArray();

    /// <summary>
    /// Pre-encoded <c>AUTHINFO USER </c> prefix. The caller appends the username and <see cref="Crlf"/>.
    /// </summary>
    internal static readonly byte[] AuthInfoUserPrefix = "AUTHINFO USER "u8.ToArray();

    /// <summary>
    /// Pre-encoded <c>AUTHINFO PASS </c> prefix. The caller appends the password and <see cref="Crlf"/>.
    /// </summary>
    internal static readonly byte[] AuthInfoPassPrefix = "AUTHINFO PASS "u8.ToArray();

    /// <summary>Pre-encoded <c>CAPABILITIES</c> command, including the terminating CRLF.</summary>
    internal static readonly byte[] CapabilitiesCommand = "CAPABILITIES\r\n"u8.ToArray();

    /// <summary>Pre-encoded <c>STARTTLS</c> command, including the terminating CRLF.</summary>
    internal static readonly byte[] StartTlsCommand = "STARTTLS\r\n"u8.ToArray();

    /// <summary>ASCII label <c>STARTTLS</c> passed to <see cref="CapabilityLabelEquals"/>.</summary>
    internal static readonly byte[] StartTlsCapability = "STARTTLS"u8.ToArray();

    /// <summary>
    /// Local ceiling on CAPABILITIES body lines read before the session fails closed.
    /// RFC 3977 §5.2 defines the command, not this count.
    /// </summary>
    internal const int MaxCapabilityLines = 256;

    /// <summary>
    /// Parses one status line into a three-digit code and the remainder of the line.
    /// </summary>
    /// <param name="line">Status line with the LF or CRLF already removed.</param>
    /// <param name="code">Parsed code when the line begins with three ASCII digits; otherwise 0.</param>
    /// <param name="text">
    /// ASCII decoding of the bytes after the code. One leading space is skipped.
    /// Empty when the line is only the code, or when that space is the last byte.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="line"/> begins with three ASCII digits.
    /// A separator other than space is left in <paramref name="text"/>.
    /// Otherwise <see langword="false"/>, <paramref name="code"/> is 0, and <paramref name="text"/> is empty.
    /// </returns>
    /// <remarks>
    /// The code is not restricted to 100–599. Bytes above 127 are decoded with <see cref="Encoding.ASCII"/> replacement.
    /// </remarks>
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

    /// <summary>Encodes <paramref name="value"/> as ASCII when every character is in the 0–127 range.</summary>
    /// <param name="value">Text to encode. CR and LF are accepted.</param>
    /// <param name="bytes">
    /// Encoded bytes when the method returns <see langword="true"/>; otherwise an empty array.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when any character is above 127. <paramref name="bytes"/> is then empty and nothing is encoded.
    /// </returns>
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

    /// <summary>
    /// Returns whether the first capability token on <paramref name="line"/> equals
    /// <paramref name="label"/> using ASCII case-insensitive comparison.
    /// </summary>
    /// <param name="line">One capability line with its line ending removed. Leading spaces are skipped.</param>
    /// <param name="label">Expected label. Compared as a whole token; later capability arguments are ignored.</param>
    /// <returns>
    /// <see langword="false"/> when no token remains or the token length differs.
    /// Only ASCII <c>a</c>–<c>z</c> are folded.
    /// </returns>
    internal static bool CapabilityLabelEquals(ReadOnlySpan<byte> line, ReadOnlySpan<byte> label)
    {
        var start = 0;
        while (start < line.Length && line[start] == (byte)' ')
        {
            start++;
        }

        if (start >= line.Length)
        {
            return false;
        }

        var end = start;
        while (end < line.Length && line[end] != (byte)' ')
        {
            end++;
        }

        var token = line[start..end];
        if (token.Length != label.Length)
        {
            return false;
        }

        for (var i = 0; i < token.Length; i++)
        {
            if (ToAsciiUpper(token[i]) != ToAsciiUpper(label[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns whether <paramref name="line"/> is a multiline terminator: a single <c>.</c>.</summary>
    /// <param name="line">One response line with LF or CRLF already removed.</param>
    /// <returns><see langword="true"/> only when the span length is 1 and that byte is <c>.</c>.</returns>
    internal static bool IsMultilineTerminator(ReadOnlySpan<byte> line) =>
        line.Length == 1 && line[0] == (byte)'.';

    /// <summary>Drops one leading dot from a line that begins with <c>..</c>. Other lines are unchanged.</summary>
    /// <param name="line">One multiline body line without its line ending.</param>
    /// <returns>A span aliasing <paramref name="line"/>. No copy is made.</returns>
    /// <remarks>Used for capability lines. ARTICLE payload destuffing is done by <see cref="NntpStreamReader"/>.</remarks>
    internal static ReadOnlySpan<byte> DestuffDotLine(ReadOnlySpan<byte> line) =>
        line.Length >= 2 && line[0] == (byte)'.' && line[1] == (byte)'.'
            ? line[1..]
            : line;

    /// <summary>Folds ASCII <c>a</c>–<c>z</c> to uppercase. Every other byte is returned unchanged.</summary>
    /// <param name="value">Byte to fold.</param>
    /// <returns>The folded byte.</returns>
    private static byte ToAsciiUpper(byte value) =>
        value is >= (byte)'a' and <= (byte)'z' ? (byte)(value - 32) : value;

    /// <summary>
    /// Returns whether <paramref name="article"/> contains <c>CRLF CRLF</c> or <c>LF LF</c>.
    /// </summary>
    /// <param name="article">Destuffed ARTICLE payload.</param>
    /// <returns>
    /// <see langword="true"/> when either sequence occurs anywhere in the payload.
    /// The search does not otherwise validate article syntax.
    /// </returns>
    internal static bool HasHeaderBodySeparator(ReadOnlySpan<byte> article)
    {
        var crlfcrlf = "\r\n\r\n"u8;
        var lflf = "\n\n"u8;
        return article.IndexOf(crlfcrlf) >= 0 || article.IndexOf(lflf) >= 0;
    }

    /// <summary>Returns whether <paramref name="value"/> is an ASCII digit <c>0</c>–<c>9</c>.</summary>
    /// <param name="value">Byte to test.</param>
    /// <returns><see langword="true"/> for ASCII digits.</returns>
    private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';
}

/// <summary>
/// Buffers one NNTP stream and reads LF-delimited lines and destuffed ARTICLE payloads.
/// A CR immediately before LF is stripped. The article terminator line is not part of the payload.
/// </summary>
internal sealed class NntpStreamReader
{
    /// <summary>Transport this reader pulls from. The reader does not own or dispose it.</summary>
    private readonly Stream _stream;

    /// <summary>Receive buffer. Its length is at least 1024 and at least the size requested at construction.</summary>
    private readonly byte[] _buffer;

    /// <summary>Index of the first unread byte in <see cref="_buffer"/>.</summary>
    private int _offset;

    /// <summary>Count of unread bytes beginning at <see cref="_offset"/>.</summary>
    private int _count;

    /// <summary>Creates a reader over an already connected stream.</summary>
    /// <param name="stream">Transport to read. Ownership stays with the caller.</param>
    /// <param name="receiveBufferBytes">Requested buffer size. Values below 1024 are raised to 1024.</param>
    internal NntpStreamReader(Stream stream, int receiveBufferBytes)
    {
        _stream = stream;
        _buffer = new byte[Math.Max(1024, receiveBufferBytes)];
    }

    /// <summary>Gets unread bytes already taken from the socket but not yet consumed as a line.</summary>
    internal int BufferedByteCount => _count;

    /// <summary>Reads one line, excluding the LF delimiter and a CR immediately before that LF.</summary>
    /// <param name="maxBytes">Maximum accepted line length, excluding the delimiter.</param>
    /// <param name="timeout">Budget for the whole read. Linked with <paramref name="cancellationToken"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The line bytes, or <see langword="null"/> when the stream ends before any byte of a new line.
    /// </returns>
    /// <exception cref="EndOfStreamException">The stream ends after one or more bytes of the line were buffered.</exception>
    /// <exception cref="InvalidOperationException">The line exceeds <paramref name="maxBytes"/>.</exception>
    /// <remarks>
    /// Caller cancellation and the timeout both surface as <see cref="OperationCanceledException"/> from the stream read.
    /// This method does not distinguish those causes. A bare LF is a delimiter; CRLF is not required.
    /// </remarks>
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

    /// <summary>
    /// Reads a multiline ARTICLE payload, undoing dot-stuffing and stopping before the terminating <c>.</c> line.
    /// </summary>
    /// <param name="maxBytes">Maximum destuffed payload length. The byte that would exceed it is not stored.</param>
    /// <param name="timeout">Budget for the whole payload. Linked with <paramref name="cancellationToken"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The destuffed payload, excluding the terminator line.</returns>
    /// <exception cref="EndOfStreamException">The stream ends before a terminator line is recognized.</exception>
    /// <exception cref="InvalidOperationException">The destuffed payload reaches <paramref name="maxBytes"/>.</exception>
    /// <remarks>
    /// A line that begins with <c>..</c> stores one <c>.</c>. A line whose first byte is <c>.</c> and whose next byte is LF, or CR LF, ends the payload.
    /// A leading <c>.</c> followed by any other byte is stored, including the dot.
    /// When the buffer ends before a leading dot can be classified, the unread tail is kept and another read is issued.
    /// Caller cancellation and the timeout both surface as <see cref="OperationCanceledException"/>.
    /// </remarks>
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

    /// <summary>Moves any unread tail to the front of the buffer and reads at least one more byte.</summary>
    /// <param name="cancellationToken">Cancels the additional read. The caller passes the timeout-linked token.</param>
    /// <exception cref="EndOfStreamException">The additional read returns no bytes.</exception>
    /// <remarks>
    /// The tail is compacted only when <see cref="_offset"/> is greater than zero.
    /// The follow-up read starts at the current unread count.
    /// </remarks>
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

    /// <summary>Drops <paramref name="count"/> bytes from the front of the unread window.</summary>
    /// <param name="count">Bytes already parsed. The caller must not pass more than <see cref="_count"/>.</param>
    private void Consume(int count)
    {
        _offset += count;
        _count -= count;
    }

    /// <summary>Appends one destuffed payload byte, or throws when the payload is already at the ceiling.</summary>
    /// <param name="builder">Payload accumulator.</param>
    /// <param name="value">Byte to store.</param>
    /// <param name="maxBytes">Length that <paramref name="builder"/> must stay below.</param>
    /// <exception cref="InvalidOperationException"><paramref name="builder"/> already contains <paramref name="maxBytes"/> bytes.</exception>
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
