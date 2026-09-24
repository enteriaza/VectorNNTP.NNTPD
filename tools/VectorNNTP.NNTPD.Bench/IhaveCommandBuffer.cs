using System.Text;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Fixed-length IHAVE command line with in-place unique Message-ID digits.
/// </summary>
/// <remarks>
/// Layout: <c>IHAVE &lt;iIIIIIIIIII-CC-SSSSSSSSSSSS@vectornntp.local&gt;CRLF</c>.
/// The instance prefix is unique per connection object so warmup, measure, and
/// later runs do not collide in production HistoryDB. Sequence digits change
/// on the hot path.
/// </remarks>
internal sealed class IhaveCommandBuffer
{
    public const int InstanceWidth = 10;
    public const int ConnectionIdWidth = 2;
    public const int SequenceWidth = 12;
    public const int MaxConnections = 100;

    private const int InstanceOffset = 8;
    private const int ConnectionIdOffset = 19;
    private const int SequenceOffset = 22;

    private static long _nextInstance = DateTime.UtcNow.Ticks % 10_000_000_000L;

    private readonly byte[] _command;

    public IhaveCommandBuffer(int connectionId, long? instance = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(connectionId);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(connectionId, MaxConnections);

        var resolved = instance ?? Interlocked.Increment(ref _nextInstance);
        ArgumentOutOfRangeException.ThrowIfNegative(resolved);

        _command = Encoding.ASCII.GetBytes(
            $"IHAVE <i{resolved:D10}-{connectionId:D2}-{0:D12}@vectornntp.local>\r\n");
        TakeThisCommandBuffer.WriteDigits(_command.AsSpan(InstanceOffset, InstanceWidth), resolved, InstanceWidth);
        TakeThisCommandBuffer.WriteDigits(
            _command.AsSpan(ConnectionIdOffset, ConnectionIdWidth),
            connectionId,
            ConnectionIdWidth);
        SetSequence(0);
        Instance = resolved;
    }

    public long Instance { get; }

    public ReadOnlyMemory<byte> Buffer => _command;

    public ArraySegment<byte> Segment => new(_command);

    public int Length => _command.Length;

    public void SetSequence(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        TakeThisCommandBuffer.WriteDigits(_command.AsSpan(SequenceOffset, SequenceWidth), sequence, SequenceWidth);
    }

    public string CurrentMessageId()
    {
        var start = "IHAVE ".Length;
        var end = _command.Length - 2;
        return Encoding.ASCII.GetString(_command, start, end - start);
    }

    public static string FormatMessageId(int connectionId, long sequence, long instance = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(connectionId);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(connectionId, MaxConnections);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfNegative(instance);
        return $"<i{instance:D10}-{connectionId:D2}-{sequence:D12}@vectornntp.local>";
    }
}
