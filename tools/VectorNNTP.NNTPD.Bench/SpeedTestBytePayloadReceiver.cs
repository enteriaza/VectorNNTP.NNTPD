using System.Diagnostics;
using System.Net.Sockets;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Default SPEEDTEST payload drain: reusable buffer, discard, bulk terminator search.
/// Does not retain the payload and does not allocate per line.
/// </summary>
internal static class SpeedTestBytePayloadReceiver
{
    public const int BufferBytes = SpeedTestRawPayloadReceiver.BufferBytes;

    /// <summary>Bytes that may be a prefix of <c>\r\n.\r\n</c> and must stay uncounted.</summary>
    public const int Lookbehind = 4;

    public static async Task<SpeedTestRawReceiveResult> DrainAsync(
        Socket socket,
        Memory<byte> buffer,
        SpeedTestSocketControlReader? control,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (buffer.Length == 0)
        {
            throw new ArgumentException("Byte receive buffer must be non-empty.", nameof(buffer));
        }

        var started = Stopwatch.GetTimestamp();
        var scanner = new SpeedTestTerminatorScanner();
        var receiveCalls = 0;
        var prefixBytes = 0;

        if (control is { LeftoverBytes: > 0 })
        {
            var leftover = control.LeftoverBytes;
            var consumed = scanner.Feed(control.Unread);
            if (scanner.Completed)
            {
                control.DiscardUnread(consumed);
                return new SpeedTestRawReceiveResult(
                    scanner.PayloadBytes,
                    Stopwatch.GetElapsedTime(started),
                    receiveCalls,
                    (int)scanner.PayloadBytes);
            }

            prefixBytes = leftover - scanner.HeldBytes;
            control.DiscardUnread(leftover);
        }

        while (!scanner.Completed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var n = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            receiveCalls++;
            if (n == 0)
            {
                throw new IOException("SPEEDTEST payload ended without terminator.");
            }

            var consumed = scanner.Feed(buffer.Span[..n]);
            if (!scanner.Completed)
            {
                continue;
            }

            if (control is not null && consumed < n)
            {
                control.SetUnread(buffer.Span[consumed..n]);
            }
        }

        return new SpeedTestRawReceiveResult(
            scanner.PayloadBytes,
            Stopwatch.GetElapsedTime(started),
            receiveCalls,
            prefixBytes);
    }

    /// <summary>
    /// Locates a leading <c>.\r\n</c> or <c>\r\n.\r\n</c>. Payload excludes the terminating
    /// <c>.\r\n</c> and includes the last content line's CRLF.
    /// </summary>
    internal static bool TryFindTerminator(
        ReadOnlySpan<byte> span,
        bool atStart,
        out int payloadBytes,
        out int consumedBytes)
    {
        payloadBytes = 0;
        consumedBytes = 0;
        if (atStart && span.StartsWith(".\r\n"u8))
        {
            consumedBytes = 3;
            return true;
        }

        var index = span.IndexOf("\r\n.\r\n"u8);
        if (index < 0)
        {
            return false;
        }

        payloadBytes = index + 2;
        consumedBytes = index + 5;
        return true;
    }
}

/// <summary>
/// Bulk <c>IndexOf</c> terminator search with a 4-octet lookbehind across feeds.
/// </summary>
internal struct SpeedTestTerminatorScanner
{
    private byte _h0;
    private byte _h1;
    private byte _h2;
    private byte _h3;
    private int _holdLen;
    private bool _atStart;

    public SpeedTestTerminatorScanner()
    {
        _atStart = true;
    }

    public long PayloadBytes { get; private set; }

    public bool Completed { get; private set; }

    public int HeldBytes => _holdLen;

    /// <summary>
    /// Feeds <paramref name="span"/>. Returns the count of octets through the terminator
    /// when <see cref="Completed"/>, otherwise <c>-1</c>.
    /// </summary>
    public int Feed(ReadOnlySpan<byte> span)
    {
        if (Completed)
        {
            return 0;
        }

        if (_holdLen > 0 && span.Length > 0)
        {
            Span<byte> window = stackalloc byte[8];
            WriteHold(window);
            var take = Math.Min(Lookbehind, span.Length);
            span[..take].CopyTo(window[_holdLen..]);
            if (SpeedTestBytePayloadReceiver.TryFindTerminator(
                    window[..(_holdLen + take)],
                    _atStart,
                    out var overlapPayload,
                    out var overlapConsumed))
            {
                PayloadBytes += overlapPayload;
                Completed = true;
                _atStart = false;
                var fromSpan = overlapConsumed - _holdLen;
                _holdLen = 0;
                return fromSpan;
            }

            _atStart = false;
        }

        if (SpeedTestBytePayloadReceiver.TryFindTerminator(
                span,
                _atStart && _holdLen == 0,
                out var payloadBytes,
                out var consumedBytes))
        {
            PayloadBytes += _holdLen;
            PayloadBytes += payloadBytes;
            Completed = true;
            _holdLen = 0;
            _atStart = false;
            return consumedBytes;
        }

        var total = _holdLen + span.Length;
        if (total <= Lookbehind)
        {
            AppendHold(span);
            return -1;
        }

        PayloadBytes += total - Lookbehind;
        SetHoldToTail(_holdLen, span);
        _atStart = false;
        return -1;
    }

    private const int Lookbehind = SpeedTestBytePayloadReceiver.Lookbehind;

    private void WriteHold(Span<byte> destination)
    {
        if (_holdLen > 0)
        {
            destination[0] = _h0;
        }

        if (_holdLen > 1)
        {
            destination[1] = _h1;
        }

        if (_holdLen > 2)
        {
            destination[2] = _h2;
        }

        if (_holdLen > 3)
        {
            destination[3] = _h3;
        }
    }

    private void AppendHold(ReadOnlySpan<byte> span)
    {
        for (var i = 0; i < span.Length; i++)
        {
            SetHoldByte(_holdLen + i, span[i]);
        }

        _holdLen += span.Length;
    }

    private void SetHoldToTail(int previousHold, ReadOnlySpan<byte> span)
    {
        Span<byte> combined = stackalloc byte[previousHold + Math.Min(span.Length, Lookbehind)];
        WriteHold(combined);
        var copy = Math.Min(span.Length, Lookbehind);
        span[^copy..].CopyTo(combined[previousHold..]);
        var total = previousHold + copy;
        var start = total - Lookbehind;
        _h0 = combined[start];
        _h1 = combined[start + 1];
        _h2 = combined[start + 2];
        _h3 = combined[start + 3];
        _holdLen = Lookbehind;
    }

    private void SetHoldByte(int index, byte value)
    {
        switch (index)
        {
            case 0:
                _h0 = value;
                break;
            case 1:
                _h1 = value;
                break;
            case 2:
                _h2 = value;
                break;
            case 3:
                _h3 = value;
                break;
        }
    }
}
