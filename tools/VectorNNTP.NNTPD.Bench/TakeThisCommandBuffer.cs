using System.Text;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Fixed-length TAKETHIS command line with in-place unique Message-ID digits.
/// </summary>
/// <remarks>
/// Layout: <c>TAKETHIS &lt;bench-CC-SSSSSSSSSSSS@vectornntp.local&gt;CRLF</c>.
/// Only the 12 sequence digits change on the hot path.
/// </remarks>
internal sealed class TakeThisCommandBuffer
{
    public const int ConnectionIdWidth = 2;
    public const int SequenceWidth = 12;
    public const int MaxConnections = 100;

    private const int ConnectionIdOffset = 16;
    private const int SequenceOffset = 19;

    private readonly byte[] _command;

    public TakeThisCommandBuffer(int connectionId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(connectionId);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(connectionId, MaxConnections);

        _command = Encoding.ASCII.GetBytes(
            $"TAKETHIS <bench-{connectionId:D2}-{0:D12}@vectornntp.local>\r\n");
        WriteDigits(_command.AsSpan(ConnectionIdOffset, ConnectionIdWidth), connectionId, ConnectionIdWidth);
        SetSequence(0);
    }

    public ReadOnlyMemory<byte> Buffer => _command;

    /// <summary>Same storage as <see cref="Buffer"/>, for vectored socket send without a copy.</summary>
    public ArraySegment<byte> Segment => new(_command);

    public int Length => _command.Length;

    public void SetSequence(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        WriteDigits(_command.AsSpan(SequenceOffset, SequenceWidth), sequence, SequenceWidth);
    }

    public string CurrentMessageId()
    {
        // "<mid>" sits between "TAKETHIS " and CRLF.
        var start = "TAKETHIS ".Length;
        var end = _command.Length - 2;
        return Encoding.ASCII.GetString(_command, start, end - start);
    }

    public static string FormatMessageId(int connectionId, long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(connectionId);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(connectionId, MaxConnections);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        return $"<bench-{connectionId:D2}-{sequence:D12}@vectornntp.local>";
    }

    internal static void WriteDigits(Span<byte> destination, long value, int width)
    {
        for (var i = width - 1; i >= 0; i--)
        {
            destination[i] = (byte)('0' + (value % 10));
            value /= 10;
        }
    }
}
