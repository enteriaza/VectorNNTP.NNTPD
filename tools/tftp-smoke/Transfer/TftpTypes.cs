using System.Net;

namespace VectorNNTP.TftpSmoke.Transfer;

/// <summary>Outcome of one RRQ or WRQ exchange.</summary>
public enum TftpTransferStatus
{
    /// <summary>The final DATA block was acknowledged and, for a write, the receiver published the file.</summary>
    Succeeded,

    /// <summary>The transfer stopped because of an ERROR packet, a timeout budget, or a local I/O failure.</summary>
    Failed,

    /// <summary>The caller's cancellation token fired.</summary>
    Cancelled,
}

/// <summary>Retransmission budget for one exchange. Both peers use the same values in the benchmark.</summary>
public sealed class TftpTiming
{
    /// <summary>How long to wait for the next expected ACK or DATA packet before retransmitting.</summary>
    public TimeSpan AcknowledgementTimeout { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Consecutive timeouts, or non-progress retransmissions, before the transfer fails.</summary>
    public int MaxTimeouts { get; init; } = 6;

    /// <summary>
    /// How long the receiver keeps its transfer identifier open after the final ACK so a lost ACK can be recovered.
    /// This does not hold the server's transfer-concurrency slot.
    /// </summary>
    public TimeSpan FinalAckLinger { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>Packet and retry counters for one transfer. Updates are safe from the listen loop and the session loop.</summary>
public sealed class TransferMeter
{
    private int _retries;
    private int _timeouts;
    private long _datagramsSent;
    private long _datagramsReceived;
    private long _wireBytesSent;
    private long _wireBytesReceived;
    private int _dataSent;
    private int _dataRetransmitted;
    private int _ackSent;
    private int _dataReceived;
    private int _duplicateData;
    private int _outOfWindow;
    private int _ackReceived;
    private int _handshakeTimeouts;

    /// <summary>Retransmissions after the first send of a packet or window.</summary>
    public int Retries => Volatile.Read(ref _retries);

    /// <summary>Times the acknowledgement timer fired.</summary>
    public int Timeouts => Volatile.Read(ref _timeouts);

    /// <summary>Datagrams handed to the socket.</summary>
    public long DatagramsSent => Volatile.Read(ref _datagramsSent);

    /// <summary>Datagrams taken from the socket, including those later rejected.</summary>
    public long DatagramsReceived => Volatile.Read(ref _datagramsReceived);

    /// <summary>TFTP datagram payload bytes sent. IP and UDP headers are not included.</summary>
    public long WireBytesSent => Volatile.Read(ref _wireBytesSent);

    /// <summary>TFTP datagram payload bytes received. IP and UDP headers are not included.</summary>
    public long WireBytesReceived => Volatile.Read(ref _wireBytesReceived);

    /// <summary>Records one transmitted datagram.</summary>
    public void Sent(int bytes)
    {
        Interlocked.Increment(ref _datagramsSent);
        Interlocked.Add(ref _wireBytesSent, bytes);
    }

    /// <summary>Records one received datagram.</summary>
    public void Received(int bytes)
    {
        Interlocked.Increment(ref _datagramsReceived);
        Interlocked.Add(ref _wireBytesReceived, bytes);
    }

    /// <summary>Records a retransmission.</summary>
    public void Retry() => Interlocked.Increment(ref _retries);

    /// <summary>Records an acknowledgement timeout.</summary>
    public void Timeout() => Interlocked.Increment(ref _timeouts);

    /// <summary>First transmission of a DATA datagram.</summary>
    public int DataDatagramsSent => Volatile.Read(ref _dataSent);

    /// <summary>DATA datagrams sent again after a timeout or a duplicate ACK.</summary>
    public int DataDatagramsRetransmitted => Volatile.Read(ref _dataRetransmitted);

    /// <summary>ACK datagrams sent by this side.</summary>
    public int AckDatagramsSent => Volatile.Read(ref _ackSent);

    /// <summary>DATA datagrams accepted into the receive window, including those held ahead of a gap.</summary>
    public int DataDatagramsReceived => Volatile.Read(ref _dataReceived);

    /// <summary>DATA datagrams that repeated a block already received.</summary>
    public int DuplicateDataDatagrams => Volatile.Read(ref _duplicateData);

    /// <summary>DATA datagrams whose block number matched no index in the current receive window.</summary>
    public int OutOfWindowDatagrams => Volatile.Read(ref _outOfWindow);

    /// <summary>ACK datagrams observed by the sender.</summary>
    public int AckDatagramsReceived => Volatile.Read(ref _ackReceived);

    /// <summary>Records the first send of one DATA datagram.</summary>
    public void CountDataSent() => Interlocked.Increment(ref _dataSent);

    /// <summary>Records one retransmitted DATA datagram.</summary>
    public void CountDataRetransmitted() => Interlocked.Increment(ref _dataRetransmitted);

    /// <summary>Records one ACK datagram sent.</summary>
    public void CountAckSent() => Interlocked.Increment(ref _ackSent);

    /// <summary>Records one new DATA datagram taken into the receive window.</summary>
    public void CountDataReceived() => Interlocked.Increment(ref _dataReceived);

    /// <summary>Records one duplicate DATA datagram.</summary>
    public void CountDuplicateData() => Interlocked.Increment(ref _duplicateData);

    /// <summary>Records one DATA datagram outside the receive window.</summary>
    public void CountOutOfWindow() => Interlocked.Increment(ref _outOfWindow);

    /// <summary>Records one ACK datagram received.</summary>
    public void CountAckReceived() => Interlocked.Increment(ref _ackReceived);

    /// <summary>Times the handshake timer fired before the first DATA or OACK. These are not DATA retransmissions.</summary>
    public int HandshakeTimeouts => Volatile.Read(ref _handshakeTimeouts);

    /// <summary>Records one handshake timeout.</summary>
    public void CountHandshakeTimeout() => Interlocked.Increment(ref _handshakeTimeouts);
}

/// <summary>Final observation of one client transfer.</summary>
public sealed class TftpTransferResult
{
    /// <summary>Whether the transfer completed, failed, or was cancelled.</summary>
    public required TftpTransferStatus Status { get; init; }

    /// <summary>Empty when <see cref="Status"/> is <see cref="TftpTransferStatus.Succeeded"/>.</summary>
    public string ErrorMessage { get; init; } = "";

    /// <summary>RFC 1350 or RFC 2347 error code when the peer sent ERROR. Zero otherwise.</summary>
    public ushort ErrorCode { get; init; }

    /// <summary>Retransmissions counted by this side.</summary>
    public int Retries { get; init; }

    /// <summary>Acknowledgement timeouts counted by this side.</summary>
    public int Timeouts { get; init; }

    /// <summary>Datagrams sent by this side.</summary>
    public long DatagramsSent { get; init; }

    /// <summary>Datagrams received by this side.</summary>
    public long DatagramsReceived { get; init; }

    /// <summary>TFTP payload bytes sent by this side, excluding IP and UDP headers.</summary>
    public long WireBytesSent { get; init; }

    /// <summary>TFTP payload bytes received by this side, excluding IP and UDP headers.</summary>
    public long WireBytesReceived { get; init; }

    /// <summary>Article bytes, excluding TFTP headers. Zero for a failed transfer that delivered nothing.</summary>
    public long PayloadBytes { get; init; }

    /// <summary>Monotonic time from the first request byte until success, failure, or cancellation.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>SHA-256 of the received article bytes. Empty for an upload, which does not see the receiver's file.</summary>
    public byte[] Sha256 { get; init; } = [];

    /// <summary>Negotiated DATA block size. Zero when negotiation did not complete.</summary>
    public int BlockSize { get; init; }

    /// <summary>Negotiated window size. Zero when negotiation did not complete.</summary>
    public int WindowSize { get; init; }

    /// <summary>First-transmission DATA datagrams from this side.</summary>
    public int DataDatagramsSent { get; init; }

    /// <summary>Retransmitted DATA datagrams from this side.</summary>
    public int DataDatagramsRetransmitted { get; init; }

    /// <summary>ACK datagrams sent by this side.</summary>
    public int AckDatagramsSent { get; init; }

    /// <summary>ACK datagrams received by this side.</summary>
    public int AckDatagramsReceived { get; init; }

    /// <summary>Handshake timeouts on this side. These did not retransmit DATA.</summary>
    public int HandshakeTimeouts { get; init; }
}

/// <summary>
/// A completed receive sitting in a temporary file. <see cref="Publish"/> moves it into the server root.
/// Disposing an unpublished article deletes the temporary file.
/// </summary>
public sealed class ReceivedArticle : IDisposable
{
    private int _disposed;

    internal ReceivedArticle(string fileName, string temporaryPath, string finalPath, long length, byte[] sha256)
    {
        FileName = fileName;
        TemporaryPath = temporaryPath;
        FinalPath = finalPath;
        Length = length;
        Sha256 = sha256;
    }

    /// <summary>Remote file name, a single path segment.</summary>
    public string FileName { get; }

    /// <summary>Temporary file that holds the received bytes. Invalid after a successful <see cref="Publish"/>.</summary>
    public string TemporaryPath { get; }

    /// <summary>Destination path used by <see cref="Publish"/>.</summary>
    public string FinalPath { get; }

    /// <summary>Received article length in bytes.</summary>
    public long Length { get; }

    /// <summary>SHA-256 of the bytes written to <see cref="TemporaryPath"/>.</summary>
    public byte[] Sha256 { get; }

    /// <summary>True after <see cref="Publish"/> moves the temporary file onto <see cref="FinalPath"/>.</summary>
    public bool Published { get; private set; }

    /// <summary>
    /// Marks the article accepted without moving a file.
    /// The memory-sink benchmark uses this after the incremental SHA-256 matches. It does not create durable storage.
    /// </summary>
    public void AcceptVerifiedHash()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Published = true;
    }

    /// <summary>
    /// Moves the temporary file onto <see cref="FinalPath"/> on the same volume.
    /// Fails if the destination already exists. This is not a durability guarantee.
    /// </summary>
    public void Publish()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Published)
        {
            return;
        }

        var directory = Path.GetDirectoryName(FinalPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Move(TemporaryPath, FinalPath, overwrite: false);
        Published = true;
    }

    /// <summary>Deletes the temporary file when the article was not published.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || Published || TemporaryPath.Length == 0)
        {
            return;
        }

        try
        {
            File.Delete(TemporaryPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Structured log lines. The benchmark does not log each packet.</summary>
public interface ITftpLog
{
    /// <summary>Writes one event. <paramref name="level"/> is info, warn, or debug.</summary>
    void Write(string level, string eventName, string details);
}

/// <summary>Discards log events.</summary>
public sealed class NullTftpLog : ITftpLog
{
    /// <summary>Shared silent log.</summary>
    public static readonly NullTftpLog Instance = new();

    /// <inheritdoc />
    public void Write(string level, string eventName, string details)
    {
    }
}

/// <summary>Writes one line per event to stdout. Debug lines require <see cref="Verbose"/>.</summary>
public sealed class ConsoleTftpLog : ITftpLog
{
    /// <summary>When false, debug events are dropped.</summary>
    public bool Verbose { get; init; }

    /// <inheritdoc />
    public void Write(string level, string eventName, string details)
    {
        if (!Verbose && string.Equals(level, "debug", StringComparison.Ordinal))
        {
            return;
        }

        Console.WriteLine($"{level} event={eventName} {details}");
    }
}

/// <summary>Client or server counters rolled up for a process.</summary>
public sealed class TftpEndpointCounters
{
    private long _wireBytesSent;
    private long _wireBytesReceived;
    private long _datagramsSent;
    private long _datagramsReceived;
    private long _retries;
    private long _timeouts;
    private long _dataSent;
    private long _dataRetransmitted;
    private long _ackSent;
    private long _dataReceived;
    private long _duplicateData;
    private long _outOfWindow;
    private long _ackReceived;
    private long _handshakeTimeouts;
    private int _outstanding;
    private int _maxOutstanding;
    private long _lingerSkipped;
    private long _accepted;
    private long _completed;
    private long _failed;

    /// <summary>TFTP payload bytes sent.</summary>
    public long WireBytesSent => Volatile.Read(ref _wireBytesSent);

    /// <summary>TFTP payload bytes received.</summary>
    public long WireBytesReceived => Volatile.Read(ref _wireBytesReceived);

    /// <summary>Datagrams sent.</summary>
    public long DatagramsSent => Volatile.Read(ref _datagramsSent);

    /// <summary>Datagrams received.</summary>
    public long DatagramsReceived => Volatile.Read(ref _datagramsReceived);

    /// <summary>Retransmissions.</summary>
    public long Retries => Volatile.Read(ref _retries);

    /// <summary>Acknowledgement timeouts.</summary>
    public long Timeouts => Volatile.Read(ref _timeouts);

    /// <summary>First-transmission DATA datagrams.</summary>
    public long DataDatagramsSent => Volatile.Read(ref _dataSent);

    /// <summary>Retransmitted DATA datagrams.</summary>
    public long DataDatagramsRetransmitted => Volatile.Read(ref _dataRetransmitted);

    /// <summary>ACK datagrams sent.</summary>
    public long AckDatagramsSent => Volatile.Read(ref _ackSent);

    /// <summary>DATA datagrams accepted into a receive window.</summary>
    public long DataDatagramsReceived => Volatile.Read(ref _dataReceived);

    /// <summary>Duplicate DATA datagrams.</summary>
    public long DuplicateDataDatagrams => Volatile.Read(ref _duplicateData);

    /// <summary>DATA datagrams outside the receive window.</summary>
    public long OutOfWindowDatagrams => Volatile.Read(ref _outOfWindow);

    /// <summary>ACK datagrams received.</summary>
    public long AckDatagramsReceived => Volatile.Read(ref _ackReceived);

    /// <summary>Handshake timeouts. These are not DATA retransmissions.</summary>
    public long HandshakeTimeouts => Volatile.Read(ref _handshakeTimeouts);

    /// <summary>Highest number of transfers inside the data phase at once.</summary>
    public int MaxOutstanding => Volatile.Read(ref _maxOutstanding);

    /// <summary>Final-ACK linger skipped because the open-exchange cap was reached.</summary>
    public long LingerSkipped => Volatile.Read(ref _lingerSkipped);

    /// <summary>WRQ transfers that entered the data phase.</summary>
    public long AcceptedWrites => Volatile.Read(ref _accepted);

    /// <summary>WRQ transfers that published a file.</summary>
    public long CompletedWrites => Volatile.Read(ref _completed);

    /// <summary>Transfers that failed or were cancelled.</summary>
    public long FailedTransfers => Volatile.Read(ref _failed);

    /// <summary>Adds one finished transfer's meter.</summary>
    public void Add(TransferMeter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);
        Interlocked.Add(ref _wireBytesSent, meter.WireBytesSent);
        Interlocked.Add(ref _wireBytesReceived, meter.WireBytesReceived);
        Interlocked.Add(ref _datagramsSent, meter.DatagramsSent);
        Interlocked.Add(ref _datagramsReceived, meter.DatagramsReceived);
        Interlocked.Add(ref _retries, meter.Retries);
        Interlocked.Add(ref _timeouts, meter.Timeouts);
        Interlocked.Add(ref _dataSent, meter.DataDatagramsSent);
        Interlocked.Add(ref _dataRetransmitted, meter.DataDatagramsRetransmitted);
        Interlocked.Add(ref _ackSent, meter.AckDatagramsSent);
        Interlocked.Add(ref _dataReceived, meter.DataDatagramsReceived);
        Interlocked.Add(ref _duplicateData, meter.DuplicateDataDatagrams);
        Interlocked.Add(ref _outOfWindow, meter.OutOfWindowDatagrams);
        Interlocked.Add(ref _ackReceived, meter.AckDatagramsReceived);
        Interlocked.Add(ref _handshakeTimeouts, meter.HandshakeTimeouts);
    }

    /// <summary>Records a transfer entering the data phase.</summary>
    public void Enter()
    {
        var now = Interlocked.Increment(ref _outstanding);
        int previous;
        do
        {
            previous = Volatile.Read(ref _maxOutstanding);
            if (now <= previous)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _maxOutstanding, now, previous) != previous);
    }

    /// <summary>Records a transfer leaving the data phase.</summary>
    public void Exit() => Interlocked.Decrement(ref _outstanding);

    /// <summary>Records a skipped linger.</summary>
    public void SkipLinger() => Interlocked.Increment(ref _lingerSkipped);

    /// <summary>Records an accepted write.</summary>
    public void AcceptWrite() => Interlocked.Increment(ref _accepted);

    /// <summary>Records a published write.</summary>
    public void CompleteWrite() => Interlocked.Increment(ref _completed);

    /// <summary>Records a failed transfer.</summary>
    public void Fail() => Interlocked.Increment(ref _failed);
}

internal readonly record struct RequestKey(IPAddress Address, int Port, ushort Opcode, string FileName);
