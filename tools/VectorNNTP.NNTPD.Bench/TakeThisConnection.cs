using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
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
    private readonly bool _collectTiming;
    private readonly int _senderDepth;
    private readonly ConcurrentDictionary<long, SendMark> _sendMarks = new();
    private readonly ConcurrentBag<TakeThisClientTimingSample> _timingSamples = [];
    private int _activeSends;
    private int _maxActiveSends;
    private int _maxAwaiting239;
    private long _sendCalls;
    private long _sendBytesReturned;
    private int _minSendBytes = int.MaxValue;
    private int _maxSendBytes;

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
        TimeSpan warmup,
        bool collectTiming = false,
        int senderDepth = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(senderDepth, 1);
        _id = id;
        _host = host;
        _port = port;
        _article = article;
        _pipelineDepth = pipelineDepth;
        _duration = duration;
        _warmup = warmup;
        _collectTiming = collectTiming;
        _senderDepth = senderDepth;
    }

    private long _sent;
    private long _accepted239;
    private long _rejected439;
    private long _bytesSent;

    public long Sent => Volatile.Read(ref _sent);
    public long Accepted239 => Volatile.Read(ref _accepted239);
    public long Rejected439 => Volatile.Read(ref _rejected439);
    public long ProtocolErrors { get; private set; }
    public long ConnectionErrors { get; private set; }
    public long Temporary400 { get; private set; }
    public long BytesSent => Volatile.Read(ref _bytesSent);
    private int _maxOutstanding;

    public int MaxOutstanding => _maxOutstanding;
    public int MaxActiveSends => _maxActiveSends;
    public int MaxAwaiting239 => _maxAwaiting239;
    public int SenderDepth => _senderDepth;
    public long SendCalls => _sendCalls;
    public long SendBytesReturned => _sendBytesReturned;
    public int MinSendBytes => _minSendBytes == int.MaxValue ? 0 : _minSendBytes;
    public int MaxSendBytes => _maxSendBytes;
    public double MeasureElapsedSeconds { get; private set; }
    public int WireCommandBytes { get; private set; }
    public Exception? Fault { get; private set; }
    public TakeThisClientTimingSample[]? TimingSamples { get; private set; }

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

            var measureSw = Stopwatch.StartNew();
            await RunWindowAsync(_duration, record: true, cancellationToken).ConfigureAwait(false);
            measureSw.Stop();
            MeasureElapsedSeconds = measureSw.Elapsed.TotalSeconds;
            if (_collectTiming)
            {
                TimingSamples = _timingSamples.ToArray();
            }
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
        Volatile.Write(ref _sent, 0);
        Volatile.Write(ref _accepted239, 0);
        Volatile.Write(ref _rejected439, 0);
        Temporary400 = 0;
        Volatile.Write(ref _bytesSent, 0);
        _maxOutstanding = 0;
        _maxActiveSends = 0;
        _maxAwaiting239 = 0;
        _sendCalls = 0;
        _sendBytesReturned = 0;
        _minSendBytes = int.MaxValue;
        _maxSendBytes = 0;
        _sendMarks.Clear();

        while (_timingSamples.TryTake(out _))
        {
        }
    }

    public async Task RunWindowAsync(TimeSpan duration, bool record, CancellationToken cancellationToken)
    {
        if (_socket is null || _stream is null)
        {
            throw new InvalidOperationException($"Connection {_id} is not connected.");
        }

        var sharedCommand = new TakeThisCommandBuffer(_id);
        var sharedBuffers = TakeThisWireSend.CreateBuffers(sharedCommand.Segment, _article);
        WireCommandBytes = sharedCommand.Length;
        using var sendWindowCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        sendWindowCts.CancelAfter(duration);
        using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var gate = new SemaphoreSlim(_pipelineDepth, _pipelineDepth);
        var sendGate = _senderDepth == 1 ? null : new SemaphoreSlim(_senderDepth, _senderDepth);
        var outstanding = 0;
        int ReadOutstanding() => Volatile.Read(ref outstanding);
        void CompleteOutstanding() => Interlocked.Decrement(ref outstanding);
        var sequence = 0L;
        var pendingSends = new ConcurrentBag<Task>();

        var receiveTask = ReceiveLoopAsync(
            gate,
            CompleteOutstanding,
            ReadOutstanding,
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

                if (sendGate is not null)
                {
                    try
                    {
                        await sendGate.WaitAsync(sendWindowCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (sendWindowCts.IsCancellationRequested)
                    {
                        gate.Release();
                        break;
                    }
                }

                var seq = Interlocked.Increment(ref sequence);
                TakeThisCommandBuffer command;
                ArraySegment<byte>[] sendBuffers;
                if (sendGate is null)
                {
                    command = sharedCommand;
                    command.SetSequence(seq);
                    sendBuffers = sharedBuffers;
                }
                else
                {
                    command = new TakeThisCommandBuffer(_id);
                    command.SetSequence(seq);
                    sendBuffers = TakeThisWireSend.CreateBuffers(command.Segment, _article);
                }

                var inFlight = Interlocked.Increment(ref outstanding);
                UpdateMax(ref _maxOutstanding, inFlight);

                if (sendGate is null)
                {
                    try
                    {
                        await SendOneAsync(
                                seq,
                                command,
                                sendBuffers,
                                inFlight,
                                ReadOutstanding,
                                record,
                                sendWindowCts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (sendWindowCts.IsCancellationRequested)
                    {
                        CompleteOutstanding();
                        ReleaseGate(gate);
                        break;
                    }
                    catch
                    {
                        CompleteOutstanding();
                        ReleaseGate(gate);
                        throw;
                    }
                }
                else
                {
                    pendingSends.Add(SendConcurrentAsync(
                        seq,
                        command,
                        sendBuffers,
                        inFlight,
                        ReadOutstanding,
                        record,
                        CompleteOutstanding,
                        gate,
                        sendGate,
                        sendWindowCts.Token));
                }
            }

            if (!pendingSends.IsEmpty)
            {
                try
                {
                    await Task.WhenAll(pendingSends).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
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

    private static long ToMicroseconds(long start, long end) =>
        (long)Stopwatch.GetElapsedTime(start, end).TotalMicroseconds;

    private static void UpdateMax(ref int target, int candidate)
    {
        var current = target;
        while (candidate > current)
        {
            var original = Interlocked.CompareExchange(ref target, candidate, current);
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
        Func<int> readOutstanding,
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
                        Interlocked.Increment(ref _accepted239);
                        if (_collectTiming && TryTakeSendMark(line, out var mark))
                        {
                            var t239 = Stopwatch.GetTimestamp();
                            _timingSamples.Add(new TakeThisClientTimingSample(
                                ToMicroseconds(mark.SendStart, mark.SendEnd),
                                ToMicroseconds(mark.SendEnd, t239),
                                ToMicroseconds(mark.SendStart, t239),
                                mark.OutstandingAtSend,
                                readOutstanding())
                            {
                                SendCalls = mark.SendCalls,
                                FirstSendBytes = mark.FirstSendBytes,
                                MinSendBytes = mark.MinSendBytes,
                                MaxSendBytes = mark.MaxSendBytes,
                                TotalSendBytes = mark.TotalSendBytes,
                                ActiveSendsAtStart = mark.ActiveSendsAtStart,
                                Awaiting239AtSendStart = mark.Awaiting239AtSendStart,
                            });
                        }
                    }

                    completeOutstanding();
                    ReleaseGate(gate);
                }
                else if (line.StartsWith("439 ", StringComparison.Ordinal))
                {
                    if (record)
                    {
                        Interlocked.Increment(ref _rejected439);
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

    private async Task SendConcurrentAsync(
        long sequence,
        TakeThisCommandBuffer command,
        ArraySegment<byte>[] sendBuffers,
        int inFlight,
        Func<int> readOutstanding,
        bool record,
        Action completeOutstanding,
        SemaphoreSlim pipelineGate,
        SemaphoreSlim sendGate,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendOneAsync(sequence, command, sendBuffers, inFlight, readOutstanding, record, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completeOutstanding();
            ReleaseGate(pipelineGate);
        }
        catch (Exception ex)
        {
            completeOutstanding();
            ReleaseGate(pipelineGate);
            Fault ??= ex;
            ConnectionErrors++;
        }
        finally
        {
            ReleaseGate(sendGate);
        }
    }

    private async Task SendOneAsync(
        long sequence,
        TakeThisCommandBuffer command,
        ArraySegment<byte>[] sendBuffers,
        int inFlight,
        Func<int> readOutstanding,
        bool record,
        CancellationToken cancellationToken)
    {
        var sendStart = 0L;
        if (record && _collectTiming)
        {
            sendStart = Stopwatch.GetTimestamp();
        }

        var stats = await SendCommandAndArticleAsync(command, sendBuffers, cancellationToken)
            .ConfigureAwait(false);
        var sendEnd = record && _collectTiming ? Stopwatch.GetTimestamp() : 0L;
        if (record)
        {
            Interlocked.Increment(ref _sent);
            Interlocked.Add(ref _bytesSent, command.Length + _article.Length);
            var awaiting = Math.Max(0, readOutstanding() - Volatile.Read(ref _activeSends));
            UpdateMax(ref _maxAwaiting239, awaiting);
            if (_collectTiming)
            {
                _sendMarks[sequence] = new SendMark(
                    sendStart,
                    sendEnd,
                    inFlight,
                    stats.SendCalls,
                    stats.FirstSendBytes,
                    stats.MinSendBytes,
                    stats.MaxSendBytes,
                    stats.TotalSendBytes,
                    stats.ActiveSendsAtStart,
                    Math.Max(0, inFlight - stats.ActiveSendsAtStart));
            }
        }
    }

    private async Task<SendStats> SendCommandAndArticleAsync(
        TakeThisCommandBuffer command,
        ArraySegment<byte>[] sendBuffers,
        CancellationToken cancellationToken)
    {
        TakeThisWireSend.Bind(sendBuffers, command.Segment, _article);
        var remaining = command.Length + _article.Length;
        var calls = 0;
        var first = 0;
        var min = int.MaxValue;
        var max = 0;
        var total = 0;
        var activeAtStart = Interlocked.Increment(ref _activeSends);
        UpdateMax(ref _maxActiveSends, activeAtStart);
        try
        {
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sent = await _socket!.SendAsync(sendBuffers, SocketFlags.None).ConfigureAwait(false);
                if (sent <= 0)
                {
                    throw new EndOfStreamException($"Connection {_id}: vectored send returned {sent}.");
                }

                calls++;
                if (calls == 1)
                {
                    first = sent;
                }

                if (sent < min)
                {
                    min = sent;
                }

                if (sent > max)
                {
                    max = sent;
                }

                total += sent;
                Interlocked.Increment(ref _sendCalls);
                Interlocked.Add(ref _sendBytesReturned, sent);
                UpdateMax(ref _maxSendBytes, sent);
                UpdateMin(ref _minSendBytes, sent);
                remaining = TakeThisWireSend.Advance(sendBuffers, sent);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeSends);
        }

        return new SendStats(calls, first, min == int.MaxValue ? 0 : min, max, total, activeAtStart);
    }

    private bool TryTakeSendMark(string line, out SendMark mark)
    {
        if (TryParseSequence(line, out var sequence) && _sendMarks.TryRemove(sequence, out mark))
        {
            return true;
        }

        mark = default;
        return false;
    }

    private static bool TryParseSequence(string line, out long sequence)
    {
        sequence = 0;
        var start = line.IndexOf("<bench-", StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        var digits = start + "<bench-00-".Length;
        if (digits + TakeThisCommandBuffer.SequenceWidth > line.Length)
        {
            return false;
        }

        var value = 0L;
        for (var i = 0; i < TakeThisCommandBuffer.SequenceWidth; i++)
        {
            var ch = line[digits + i];
            if (ch is < '0' or > '9')
            {
                return false;
            }

            value = (value * 10) + (ch - '0');
        }

        sequence = value;
        return true;
    }

    private static void UpdateMin(ref int target, int candidate)
    {
        var current = target;
        while (candidate < current)
        {
            var original = Interlocked.CompareExchange(ref target, candidate, current);
            if (original == current)
            {
                break;
            }

            current = original;
        }
    }

    private readonly record struct SendMark(
        long SendStart,
        long SendEnd,
        int OutstandingAtSend,
        int SendCalls,
        int FirstSendBytes,
        int MinSendBytes,
        int MaxSendBytes,
        int TotalSendBytes,
        int ActiveSendsAtStart,
        int Awaiting239AtSendStart);

    private readonly record struct SendStats(
        int SendCalls,
        int FirstSendBytes,
        int MinSendBytes,
        int MaxSendBytes,
        int TotalSendBytes,
        int ActiveSendsAtStart);

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
