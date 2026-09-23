using System.Buffers;
using System.Net.Sockets;
using System.Text;

namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// One independent TAKETHIS TCP connection: greeting, MODE STREAM, pipelined send, 239/439 receive.
/// </summary>
internal sealed class TakeThisConnection : IAsyncDisposable
{
    private const int ReceiveBufferSize = 1024 * 1024;
    private const int SendBufferSize = 256 * 1024;
    private const int DrainSeconds = 2;

    private readonly int _id;
    private readonly string _host;
    private readonly int _port;
    private readonly byte[] _article;
    private readonly int _pipelineDepth;
    private readonly TimeSpan _duration;
    private readonly TimeSpan _warmup;

    private Socket? _socket;
    private NetworkStream? _stream;
    private byte[]? _recvBuffer;
    private int _recvLen;
    private int _recvPos;

    public TakeThisConnection(
        int id,
        string host,
        int port,
        byte[] article,
        int pipelineDepth,
        TimeSpan duration,
        TimeSpan warmup)
    {
        _id = id;
        _host = host;
        _port = port;
        _article = article;
        _pipelineDepth = pipelineDepth;
        _duration = duration;
        _warmup = warmup;
    }

    public long Sent { get; private set; }
    public long Accepted239 { get; private set; }
    public long Rejected439 { get; private set; }
    public long ProtocolErrors { get; private set; }
    public long ConnectionErrors { get; private set; }
    public long Temporary400 { get; private set; }
    public long BytesSent { get; private set; }
    private int _maxOutstanding;

    public int MaxOutstanding => _maxOutstanding;
    public int WireCommandBytes { get; private set; }
    public Exception? Fault { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ConnectAndNegotiateAsync(cancellationToken).ConfigureAwait(false);

            if (_warmup > TimeSpan.Zero)
            {
                await RunWindowAsync(_warmup, record: false, cancellationToken).ConfigureAwait(false);
                ResetMeasureCounters();
            }

            await RunWindowAsync(_duration, record: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or EndOfStreamException or ObjectDisposedException)
        {
            ConnectionErrors++;
            Fault = ex;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ProtocolErrors++;
            Fault = ex;
        }
    }

    public async Task ConnectAndNegotiateAsync(CancellationToken cancellationToken)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            ReceiveBufferSize = ReceiveBufferSize,
            SendBufferSize = SendBufferSize,
        };
        await _socket.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);
        _stream = new NetworkStream(_socket, ownsSocket: true);
        _recvBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        _recvLen = 0;
        _recvPos = 0;

        var greeting = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (greeting is null ||
            !(greeting.StartsWith("200 ", StringComparison.Ordinal) ||
              greeting.StartsWith("201 ", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Connection {_id}: unexpected greeting '{greeting}'.");
        }

        await WriteFlushAsync("MODE STREAM\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        var streamReply = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (streamReply is null || !streamReply.StartsWith("203 ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Connection {_id}: MODE STREAM failed: '{streamReply}'.");
        }
    }

    public void ResetMeasureCounters()
    {
        Sent = 0;
        Accepted239 = 0;
        Rejected439 = 0;
        Temporary400 = 0;
        BytesSent = 0;
        _maxOutstanding = 0;
    }

    public async Task RunWindowAsync(TimeSpan duration, bool record, CancellationToken cancellationToken)
    {
        if (_socket is null || _stream is null)
        {
            throw new InvalidOperationException($"Connection {_id} is not connected.");
        }

        var command = new TakeThisCommandBuffer(_id);
        var sendBuffers = TakeThisWireSend.CreateBuffers(command.Segment, _article);
        WireCommandBytes = command.Length;
        using var sendWindowCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        sendWindowCts.CancelAfter(duration);
        using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var gate = new SemaphoreSlim(_pipelineDepth, _pipelineDepth);
        var outstanding = 0;
        var sequence = 0L;

        var receiveTask = ReceiveLoopAsync(
            gate,
            () => Interlocked.Decrement(ref outstanding),
            record,
            receiveCts.Token);

        try
        {
            while (!sendWindowCts.IsCancellationRequested)
            {
                try
                {
                    await gate.WaitAsync(sendWindowCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (sendWindowCts.IsCancellationRequested)
                {
                    break;
                }

                if (sendWindowCts.IsCancellationRequested)
                {
                    gate.Release();
                    break;
                }

                sequence++;
                command.SetSequence(sequence);

                var inFlight = Interlocked.Increment(ref outstanding);
                UpdateMaxOutstanding(inFlight);

                try
                {
                    await SendCommandAndArticleAsync(command, sendBuffers, sendWindowCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (sendWindowCts.IsCancellationRequested)
                {
                    Interlocked.Decrement(ref outstanding);
                    ReleaseGate(gate);
                    break;
                }
                catch
                {
                    Interlocked.Decrement(ref outstanding);
                    ReleaseGate(gate);
                    throw;
                }

                if (record)
                {
                    Sent++;
                    BytesSent += command.Length + _article.Length;
                }
            }

            using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(DrainSeconds));
            while (Volatile.Read(ref outstanding) > 0 && !drainCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(5, drainCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            await receiveCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Receive loop stopped after the drain window.
            }
        }
    }

    private void UpdateMaxOutstanding(int inFlight)
    {
        var current = _maxOutstanding;
        while (inFlight > current)
        {
            var original = Interlocked.CompareExchange(ref _maxOutstanding, inFlight, current);
            if (original == current)
            {
                break;
            }

            current = original;
        }
    }

    private async Task ReceiveLoopAsync(
        SemaphoreSlim gate,
        Action completeOutstanding,
        bool record,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (line is null)
                {
                    return;
                }

                if (line.StartsWith("239 ", StringComparison.Ordinal))
                {
                    if (record)
                    {
                        Accepted239++;
                    }

                    completeOutstanding();
                    ReleaseGate(gate);
                }
                else if (line.StartsWith("439 ", StringComparison.Ordinal))
                {
                    if (record)
                    {
                        Rejected439++;
                    }

                    completeOutstanding();
                    ReleaseGate(gate);
                }
                else if (line.StartsWith("400 ", StringComparison.Ordinal))
                {
                    Temporary400++;
                    ConnectionErrors++;
                    return;
                }
                else
                {
                    ProtocolErrors++;
                    completeOutstanding();
                    ReleaseGate(gate);
                }
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or EndOfStreamException)
        {
            ConnectionErrors++;
            Fault ??= ex;
        }
    }

    private void ReleaseGate(SemaphoreSlim gate)
    {
        try
        {
            gate.Release();
        }
        catch (SemaphoreFullException)
        {
            ProtocolErrors++;
        }
    }

    private async Task SendCommandAndArticleAsync(
        TakeThisCommandBuffer command,
        ArraySegment<byte>[] sendBuffers,
        CancellationToken cancellationToken)
    {
        TakeThisWireSend.Bind(sendBuffers, command.Segment, _article);
        var remaining = command.Length + _article.Length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sent = await _socket!.SendAsync(sendBuffers, SocketFlags.None).ConfigureAwait(false);
            if (sent <= 0)
            {
                throw new EndOfStreamException($"Connection {_id}: vectored send returned {sent}.");
            }

            remaining = TakeThisWireSend.Advance(sendBuffers, sent);
        }
    }

    private async Task WriteFlushAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _stream!.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var sb = new StringBuilder(64);
        while (true)
        {
            if (_recvPos >= _recvLen)
            {
                await FillRecvAsync(cancellationToken).ConfigureAwait(false);
            }

            var b = _recvBuffer![_recvPos++];
            if (b == (byte)'\n')
            {
                if (sb.Length > 0 && sb[^1] == '\r')
                {
                    sb.Length--;
                }

                return sb.ToString();
            }

            sb.Append((char)b);
            if (sb.Length > 8 * 1024)
            {
                throw new InvalidOperationException($"Connection {_id}: line too long.");
            }
        }
    }

    private async Task FillRecvAsync(CancellationToken cancellationToken)
    {
        if (_recvPos > 0 && _recvPos == _recvLen)
        {
            _recvPos = 0;
            _recvLen = 0;
        }
        else if (_recvPos > 0 && _recvPos < _recvLen)
        {
            var remaining = _recvLen - _recvPos;
            Buffer.BlockCopy(_recvBuffer!, _recvPos, _recvBuffer!, 0, remaining);
            _recvPos = 0;
            _recvLen = remaining;
        }

        var read = await _stream!.ReadAsync(
            _recvBuffer.AsMemory(_recvLen, _recvBuffer!.Length - _recvLen),
            cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            throw new EndOfStreamException($"Connection {_id}: closed while reading NNTP responses.");
        }

        _recvLen += read;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_stream is not null)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // best-effort
        }

        if (_recvBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_recvBuffer);
            _recvBuffer = null;
        }

        _stream = null;
        _socket = null;
    }
}
