using System.Text;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Fixed-length TAKETHIS command line with in-place unique Message-ID digits.
/// </summary>
/// <remarks>
/// Layout: <c>TAKETHIS &lt;tIIIIIIIIII-CC-SSSSSSSSSSSS@vectornntp.local&gt;CRLF</c>.
/// The instance prefix is unique per command-buffer object so warmup, measure,
/// later runs, and the previous <c>bench-CC-SSSSSSSSSSSS</c> History keys cannot
/// collide. Sequence digits change on the hot path.
/// </remarks>
internal sealed class TakeThisCommandBuffer
{
    public const int InstanceWidth = 10;
    public const int ConnectionIdWidth = 2;
    public const int SequenceWidth = 12;
    public const int MaxConnections = 100;

    private const int InstanceOffset = 11;
    private const int ConnectionIdOffset = 22;
    private const int SequenceOffset = 25;

    private static long _nextInstance = TakeThisCommandBufferSeed.Next();

    private readonly byte[] _command;

    public TakeThisCommandBuffer(int connectionId, long? instance = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(connectionId);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(connectionId, MaxConnections);

        var resolved = instance ?? Interlocked.Increment(ref _nextInstance);
        ArgumentOutOfRangeException.ThrowIfNegative(resolved);

        _command = Encoding.ASCII.GetBytes(
            $"TAKETHIS <t{resolved:D10}-{connectionId:D2}-{0:D12}@vectornntp.local>\r\n");
        WriteDigits(_command.AsSpan(InstanceOffset, InstanceWidth), resolved, InstanceWidth);
        WriteDigits(_command.AsSpan(ConnectionIdOffset, ConnectionIdWidth), connectionId, ConnectionIdWidth);
        SetSequence(0);
        Instance = resolved;
    }

    public long Instance { get; }

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
        var start = "TAKETHIS ".Length;
        var end = _command.Length - 2;
        return Encoding.ASCII.GetString(_command, start, end - start);
    }

    public static string FormatMessageId(int connectionId, long sequence, long instance)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(connectionId);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(connectionId, MaxConnections);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfNegative(instance);
        return $"<t{instance:D10}-{connectionId:D2}-{sequence:D12}@vectornntp.local>";
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
