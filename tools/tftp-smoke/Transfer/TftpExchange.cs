using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using VectorNNTP.TftpSmoke.Protocol;
using VectorNNTP.TftpSmoke.Transport;

namespace VectorNNTP.TftpSmoke.Transfer;

/// <summary>Result of the RRQ or WRQ handshake.</summary>
internal sealed class HandshakeResult
{
    public TftpTransferStatus Status { get; init; }

    public ushort Opcode { get; init; }

    public ushort Block { get; init; }

    public ushort ErrorCode { get; init; }

    public string ErrorMessage { get; init; } = "";

    public IReadOnlyList<TftpOption> Options { get; init; } = [];

    public byte[] InitialData { get; init; } = [];

    public bool HasInitialData { get; init; }
}

/// <summary>Result of receiving a payload, before the final ACK is sent.</summary>
internal sealed class ReceiveOutcome
{
    public TftpTransferStatus Status { get; init; }

    public string ErrorMessage { get; init; } = "";

    public ushort ErrorCode { get; init; }

    public long PayloadBytes { get; init; }

    public ushort FinalBlock { get; init; }

    public bool Completed { get; init; }
}

/// <summary>
/// One TFTP transfer identifier. The sender retransmits on the acknowledgement timer.
/// The receiver retransmits its handshake until the first DATA, then ACKs duplicates instead of running its own data timer.
/// </summary>
internal sealed class TftpExchange : IAsyncDisposable
{
    private readonly IDatagramTransport _transport;
    private readonly TftpTiming _timing;
    private readonly TransferMeter _meter;
    private readonly byte[] _receiveBuffer = new byte[65536];
    private readonly byte[] _ackBuffer = new byte[4];
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private IPEndPoint _remote;
    private IPEndPoint? _lockedRemote;
    private readonly TransferDiagnostics? _diagnostics;
    private byte[]? _handshake;
    private int _handshakeActive;
    private int _disposed;

    public TftpExchange(
        IDatagramTransport transport,
        IPEndPoint remote,
        TftpTiming timing,
        TransferMeter meter,
        bool remoteIsTransferId,
        TransferDiagnostics? diagnostics = null)
    {
        _transport = transport;
        _remote = remote;
        _timing = timing;
        _meter = meter;
        _diagnostics = diagnostics;
        if (remoteIsTransferId)
        {
            _lockedRemote = remote;
        }
    }

    public TransferMeter Meter => _meter;

    public async Task<HandshakeResult> SendRequestAndWaitAsync(byte[] request, CancellationToken cancellationToken)
    {
        var consecutive = 0;
        while (true)
        {
            await SendAsync(request, _remote, cancellationToken).ConfigureAwait(false);
            var wait = await WaitAsync(cancellationToken).ConfigureAwait(false);
            if (wait.Kind == IncomingKind.Cancelled)
            {
                return new HandshakeResult { Status = TftpTransferStatus.Cancelled, ErrorMessage = "cancelled" };
            }

            if (wait.Kind == IncomingKind.Timeout)
            {
                consecutive++;
                _meter.Timeout();
                _meter.Retry();
                _meter.CountHandshakeTimeout();
                if (consecutive > _timing.MaxTimeouts)
                {
                    return new HandshakeResult { Status = TftpTransferStatus.Failed, ErrorMessage = "timed out waiting for a response" };
                }

                continue;
            }

            if (wait.Kind != IncomingKind.Packet || wait.Remote is null)
            {
                return new HandshakeResult { Status = TftpTransferStatus.Failed, ErrorMessage = wait.Error };
            }

            _remote = wait.Remote;
            _lockedRemote = wait.Remote;
            var parsed = CopyHandshake(_receiveBuffer.AsSpan(0, wait.Count), out var handshakeError);
            if (parsed is not { } copy)
            {
                await SendErrorAsync(_remote, TftpErrorCode.IllegalOperation, handshakeError, cancellationToken).ConfigureAwait(false);
                return new HandshakeResult { Status = TftpTransferStatus.Failed, ErrorMessage = handshakeError };
            }

            if (copy.Unexpected)
            {
                await SendErrorAsync(_remote, TftpErrorCode.IllegalOperation, "unexpected response to the request", cancellationToken).ConfigureAwait(false);
                return new HandshakeResult { Status = TftpTransferStatus.Failed, ErrorMessage = "unexpected response to the request" };
            }

            return copy.Result;
        }
    }

    public void ArmHandshake(byte[] packet)
    {
        _handshake = packet;
        Volatile.Write(ref _handshakeActive, 1);
    }

    public async Task SendArmedHandshakeAsync(CancellationToken cancellationToken)
    {
        var packet = _handshake;
        if (packet is null || Volatile.Read(ref _handshakeActive) == 0)
        {
            return;
        }

        await SendAsync(packet, _remote, cancellationToken).ConfigureAwait(false);
    }

    public async Task RetransmitHandshakeAsync(CancellationToken cancellationToken)
    {
        if (_handshake is null || Volatile.Read(ref _handshakeActive) == 0)
        {
            return;
        }

        await SendArmedHandshakeAsync(cancellationToken).ConfigureAwait(false);
        _meter.Retry();
    }

    public async Task<TftpTransferResult> SendBlocksAsync(
        ReadOnlyMemory<byte> payload,
        int blockSize,
        int windowSize,
        long startedTimestamp,
        CancellationToken cancellationToken)
    {
        try
        {
            var packet = new byte[TftpLimits.DataHeaderLength + blockSize];
            long windowStart = 1;
            long sentEnd = 0;
            long finalIndex = -1;
            var consecutive = 0;
            ushort? retransmittedForDuplicate = null;
            while (true)
            {
                if (windowStart > sentEnd)
                {
                    var sent = 0;
                    var burstStart = _diagnostics is null ? 0 : Stopwatch.GetTimestamp();
                    while (sent < windowSize)
                    {
                        var index = sentEnd + 1;
                        var encoded = EncodeBlock(payload, blockSize, index, packet, out var length);
                        await SendAsync(packet.AsMemory(0, encoded), _remote, cancellationToken).ConfigureAwait(false);
                        _meter.CountDataSent();
                        sentEnd = index;
                        sent++;
                        if (length < blockSize)
                        {
                            finalIndex = index;
                            break;
                        }
                    }

                    if (_diagnostics is not null)
                    {
                        _diagnostics.RecordWindowSend(Stopwatch.GetTimestamp() - burstStart, sent);
                    }
                }

                var waitStart = _diagnostics is null ? 0 : Stopwatch.GetTimestamp();
                var wait = await WaitAsync(cancellationToken).ConfigureAwait(false);
                if (wait.Kind == IncomingKind.Cancelled)
                {
                    return Finish(TftpTransferStatus.Cancelled, "cancelled", 0, payload.Length, blockSize, windowSize, startedTimestamp);
                }

                if (wait.Kind == IncomingKind.Timeout)
                {
                    consecutive++;
                    _meter.Timeout();
                    _meter.Retry();
                    retransmittedForDuplicate = null;
                    if (consecutive > _timing.MaxTimeouts)
                    {
                        return Finish(TftpTransferStatus.Failed, "timed out waiting for an acknowledgement", 0, payload.Length, blockSize, windowSize, startedTimestamp);
                    }

                    await RetransmitAsync(payload, blockSize, windowStart, sentEnd, packet, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (wait.Kind != IncomingKind.Packet)
                {
                    return Finish(TftpTransferStatus.Failed, wait.Error, 0, payload.Length, blockSize, windowSize, startedTimestamp);
                }

                if (!TryCopyControl(_receiveBuffer.AsSpan(0, wait.Count), out var opcode, out var ackBlock, out var errorCode, out var errorMessage, out var decodeError))
                {
                    await SendErrorAsync(_remote, TftpErrorCode.IllegalOperation, decodeError, cancellationToken).ConfigureAwait(false);
                    return Finish(TftpTransferStatus.Failed, decodeError, TftpErrorCode.IllegalOperation, payload.Length, blockSize, windowSize, startedTimestamp);
                }

                if (opcode == TftpOpcode.OptionAcknowledgement || (opcode == TftpOpcode.Acknowledgement && ackBlock == 0 && windowStart == 1))
                {
                    continue;
                }

                if (opcode == TftpOpcode.Error)
                {
                    return Finish(TftpTransferStatus.Failed, errorMessage, errorCode, payload.Length, blockSize, windowSize, startedTimestamp);
                }

                if (opcode != TftpOpcode.Acknowledgement)
                {
                    await SendErrorAsync(_remote, TftpErrorCode.IllegalOperation, "expected an acknowledgement", cancellationToken).ConfigureAwait(false);
                    return Finish(TftpTransferStatus.Failed, "expected an acknowledgement", TftpErrorCode.IllegalOperation, payload.Length, blockSize, windowSize, startedTimestamp);
                }

                _meter.CountAckReceived();
                var resolved = ResolveAck(ackBlock, windowStart, sentEnd);
                if (resolved is null)
                {
                    continue;
                }

                if (resolved.Value < windowStart)
                {
                    if (retransmittedForDuplicate == ackBlock)
                    {
                        continue;
                    }

                    retransmittedForDuplicate = ackBlock;
                    consecutive++;
                    _meter.Retry();
                    if (consecutive > _timing.MaxTimeouts)
                    {
                        return Finish(TftpTransferStatus.Failed, "no acknowledgement progress", 0, payload.Length, blockSize, windowSize, startedTimestamp);
                    }

                    await RetransmitAsync(payload, blockSize, windowStart, sentEnd, packet, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                consecutive = 0;
                retransmittedForDuplicate = null;
                if (_diagnostics is not null)
                {
                    _diagnostics.RecordAckWait(Stopwatch.GetTimestamp() - waitStart);
                }

                windowStart = resolved.Value + 1;
                if (finalIndex >= 0 && resolved.Value == finalIndex)
                {
                    return Finish(TftpTransferStatus.Succeeded, "", 0, payload.Length, blockSize, windowSize, startedTimestamp);
                }

                if (windowStart <= sentEnd)
                {
                    await RetransmitAsync(payload, blockSize, windowStart, sentEnd, packet, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Finish(TftpTransferStatus.Cancelled, "cancelled", 0, payload.Length, blockSize, windowSize, startedTimestamp);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Finish(TftpTransferStatus.Cancelled, "cancelled", 0, payload.Length, blockSize, windowSize, startedTimestamp);
            }

            return Finish(TftpTransferStatus.Failed, ex.Message, 0, payload.Length, blockSize, windowSize, startedTimestamp);
        }
    }

    public async Task<bool> WaitForBlockAckAsync(ushort block, ReadOnlyMemory<byte> retransmit, CancellationToken cancellationToken)
    {
        var consecutive = 0;
        while (true)
        {
            var wait = await WaitAsync(cancellationToken).ConfigureAwait(false);
            if (wait.Kind == IncomingKind.Cancelled)
            {
                return false;
            }

            if (wait.Kind == IncomingKind.Timeout)
            {
                consecutive++;
                _meter.Timeout();
                _meter.Retry();
                _meter.CountHandshakeTimeout();
                if (consecutive > _timing.MaxTimeouts)
                {
                    return false;
                }

                await SendAsync(retransmit, _remote, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (wait.Kind != IncomingKind.Packet)
            {
                return false;
            }

            if (!TryCopyControl(_receiveBuffer.AsSpan(0, wait.Count), out var opcode, out var ackBlock, out _, out _, out _))
            {
                return false;
            }

            if (opcode == TftpOpcode.Error)
            {
                return false;
            }

            if (opcode == TftpOpcode.Acknowledgement && ackBlock == block)
            {
                Volatile.Write(ref _handshakeActive, 0);
                return true;
            }
        }
    }

    public async Task<ReceiveOutcome> ReceiveBlocksAsync(
        int blockSize,
        int windowSize,
        long maxBytes,
        Stream destination,
        IncrementalHash hash,
        ReadOnlyMemory<byte> initialData,
        ushort initialBlock,
        bool hasInitial,
        CancellationToken cancellationToken)
    {
        var ahead = new Dictionary<long, HeldBlock>();
        try
        {
            long nextIndex = 1;
            long windowEnd = windowSize;
            long written = 0;
            var consecutive = 0;
            var seenData = false;
            if (hasInitial)
            {
                var initial = AcceptData(initialBlock, initialData.Span, blockSize, windowSize, maxBytes, destination, hash, ahead, _meter, _diagnostics, ref nextIndex, ref windowEnd, ref written, out var initialFailure);
                seenData = true;
                Volatile.Write(ref _handshakeActive, 0);
                if (initialFailure is not null)
                {
                    await SendErrorAsync(_remote, initialFailure.Value.Code, initialFailure.Value.Message, cancellationToken).ConfigureAwait(false);
                    return Failed(initialFailure.Value.Message, initialFailure.Value.Code, written);
                }

                if (initial.Ack)
                {
                    await SendAckAsync(initial.AckBlock, cancellationToken).ConfigureAwait(false);
                }

                if (initial.Completed)
                {
                    return Completed(written, initial.FinalBlock);
                }
            }

            while (true)
            {
                var wait = await WaitAsync(cancellationToken).ConfigureAwait(false);
                if (wait.Kind == IncomingKind.Cancelled)
                {
                    return new ReceiveOutcome { Status = TftpTransferStatus.Cancelled, ErrorMessage = "cancelled", PayloadBytes = written };
                }

                if (wait.Kind == IncomingKind.Timeout)
                {
                    consecutive++;
                    _meter.Timeout();
                    if (!seenData)
                    {
                        _meter.CountHandshakeTimeout();
                        await RetransmitHandshakeAsync(cancellationToken).ConfigureAwait(false);
                    }

                    if (consecutive > _timing.MaxTimeouts)
                    {
                        return Failed(seenData ? "timed out waiting for data" : "timed out waiting for the first data block", 0, written);
                    }

                    continue;
                }

                if (wait.Kind != IncomingKind.Packet)
                {
                    return Failed(wait.Error, 0, written);
                }

                var decision = TakeData(
                    _receiveBuffer.AsSpan(0, wait.Count),
                    blockSize,
                    windowSize,
                    maxBytes,
                    destination,
                    hash,
                    ahead,
                    _meter,
                    _diagnostics,
                    ref nextIndex,
                    ref windowEnd,
                    ref written,
                    out var takeKind,
                    out var takeError,
                    out var takeCode);
                if (takeKind == TakeKind.Malformed)
                {
                    await SendErrorAsync(_remote, TftpErrorCode.IllegalOperation, takeError, cancellationToken).ConfigureAwait(false);
                    return Failed(takeError, TftpErrorCode.IllegalOperation, written);
                }

                if (takeKind == TakeKind.PeerError)
                {
                    return Failed(takeError, takeCode, written);
                }

                if (takeKind == TakeKind.Ignore)
                {
                    continue;
                }

                if (takeKind == TakeKind.ProtocolFailure)
                {
                    await SendErrorAsync(_remote, takeCode, takeError, cancellationToken).ConfigureAwait(false);
                    return Failed(takeError, takeCode, written);
                }

                seenData = true;
                consecutive = 0;
                Volatile.Write(ref _handshakeActive, 0);
                if (decision.Ack)
                {
                    await SendAckAsync(decision.AckBlock, cancellationToken).ConfigureAwait(false);
                }

                if (decision.Completed)
                {
                    return Completed(written, decision.FinalBlock);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ReceiveOutcome { Status = TftpTransferStatus.Cancelled, ErrorMessage = "cancelled" };
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ReceiveOutcome { Status = TftpTransferStatus.Cancelled, ErrorMessage = "cancelled" };
            }

            return Failed(ex.Message, 0, 0);
        }
        finally
        {
            foreach (var held in ahead.Values)
            {
                held.Release();
            }
        }
    }

    public Task SendAckAsync(ushort block, CancellationToken cancellationToken)
    {
        _meter.CountAckSent();
        return SendFixedAsync(_ackBuffer, TftpCodec.EncodeAck(_ackBuffer, block), cancellationToken);
    }

    public async Task SendTransferErrorAsync(ushort code, string message, CancellationToken cancellationToken)
    {
        var buffer = new byte[512];
        var length = TftpCodec.EncodeError(buffer, code, message);
        await SendAsync(buffer.AsMemory(0, length), _remote, cancellationToken).ConfigureAwait(false);
    }

    public async Task LingerAsync(ushort finalBlock, CancellationToken cancellationToken)
    {
        if (_timing.FinalAckLinger <= TimeSpan.Zero)
        {
            return;
        }

        var until = Stopwatch.GetTimestamp() + (long)(_timing.FinalAckLinger.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < until)
        {
            var remainingTicks = until - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                break;
            }

            var remaining = TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(remaining);
            try
            {
                var received = await _transport.ReceiveAsync(_receiveBuffer, timeout.Token).ConfigureAwait(false);
                _meter.Received(received.Count);
                if (!received.Remote.Equals(_remote))
                {
                    await SendErrorAsync(received.Remote, TftpErrorCode.UnknownTransferId, "unknown transfer id", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!TryCopyControl(_receiveBuffer.AsSpan(0, received.Count), out var opcode, out var block, out _, out _, out _)
                    || opcode != TftpOpcode.Data
                    || block != finalBlock)
                {
                    continue;
                }

                await SendAckAsync(finalBlock, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (IsTransport(ex))
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _sendLock.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RetransmitAsync(
        ReadOnlyMemory<byte> payload,
        int blockSize,
        long from,
        long to,
        byte[] packet,
        CancellationToken cancellationToken)
    {
        for (var index = from; index <= to; index++)
        {
            var encoded = EncodeBlock(payload, blockSize, index, packet, out _);
            await SendAsync(packet.AsMemory(0, encoded), _remote, cancellationToken).ConfigureAwait(false);
            _meter.CountDataRetransmitted();
        }
    }

    private async Task<ReceiveWait> WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var setupStart = _diagnostics is null ? 0 : Stopwatch.GetTimestamp();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timing.AcknowledgementTimeout);
            if (_diagnostics is not null)
            {
                _diagnostics.AddTimerSetup(Stopwatch.GetTimestamp() - setupStart);
            }

            DatagramReceive received;
            var receiveStart = _diagnostics is null ? 0 : Stopwatch.GetTimestamp();
            try
            {
                received = await _transport.ReceiveAsync(_receiveBuffer, timeout.Token).ConfigureAwait(false);
                if (_diagnostics is not null)
                {
                    _diagnostics.AddSocketReceive(Stopwatch.GetTimestamp() - receiveStart);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (_diagnostics is not null)
                {
                    _diagnostics.AddSocketReceive(Stopwatch.GetTimestamp() - receiveStart);
                }

                return new ReceiveWait(IncomingKind.Cancelled, 0, null, "cancelled");
            }
            catch (OperationCanceledException)
            {
                if (_diagnostics is not null)
                {
                    _diagnostics.AddSocketReceive(Stopwatch.GetTimestamp() - receiveStart);
                }

                return new ReceiveWait(IncomingKind.Timeout, 0, null, "timeout");
            }
            catch (Exception ex) when (IsTransport(ex))
            {
                if (_diagnostics is not null)
                {
                    _diagnostics.AddSocketReceive(Stopwatch.GetTimestamp() - receiveStart);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return new ReceiveWait(IncomingKind.Cancelled, 0, null, "cancelled");
                }

                return new ReceiveWait(IncomingKind.IoFailure, 0, null, ex.Message);
            }

            _meter.Received(received.Count);
            if (_lockedRemote is not null && !received.Remote.Equals(_lockedRemote))
            {
                await SendErrorAsync(received.Remote, TftpErrorCode.UnknownTransferId, "unknown transfer id", cancellationToken).ConfigureAwait(false);
                continue;
            }

            return new ReceiveWait(IncomingKind.Packet, received.Count, received.Remote, "");
        }
    }

    private async Task SendFixedAsync(byte[] buffer, int length, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sendStart = _diagnostics is null ? 0 : Stopwatch.GetTimestamp();
            await _transport.SendToAsync(buffer.AsMemory(0, length), _remote, cancellationToken).ConfigureAwait(false);
            if (_diagnostics is not null)
            {
                _diagnostics.AddSocketSend(Stopwatch.GetTimestamp() - sendStart);
            }

            _meter.Sent(length);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint remote, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sendStart = _diagnostics is null ? 0 : Stopwatch.GetTimestamp();
            await _transport.SendToAsync(datagram, remote, cancellationToken).ConfigureAwait(false);
            if (_diagnostics is not null)
            {
                _diagnostics.AddSocketSend(Stopwatch.GetTimestamp() - sendStart);
            }

            _meter.Sent(datagram.Length);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendErrorAsync(IPEndPoint remote, ushort code, string message, CancellationToken cancellationToken)
    {
        var buffer = new byte[512];
        var length = TftpCodec.EncodeError(buffer, code, message);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _transport.SendToAsync(buffer.AsMemory(0, length), remote, cancellationToken).ConfigureAwait(false);
            _meter.Sent(length);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private TftpTransferResult Finish(
        TftpTransferStatus status,
        string error,
        ushort errorCode,
        long payloadBytes,
        int blockSize,
        int windowSize,
        long startedTimestamp)
    {
        return new TftpTransferResult
        {
            Status = status,
            ErrorMessage = error,
            ErrorCode = errorCode,
            Retries = _meter.Retries,
            Timeouts = _meter.Timeouts,
            DatagramsSent = _meter.DatagramsSent,
            DatagramsReceived = _meter.DatagramsReceived,
            WireBytesSent = _meter.WireBytesSent,
            WireBytesReceived = _meter.WireBytesReceived,
            DataDatagramsSent = _meter.DataDatagramsSent,
            DataDatagramsRetransmitted = _meter.DataDatagramsRetransmitted,
            AckDatagramsSent = _meter.AckDatagramsSent,
            AckDatagramsReceived = _meter.AckDatagramsReceived,
            HandshakeTimeouts = _meter.HandshakeTimeouts,
            PayloadBytes = status == TftpTransferStatus.Succeeded ? payloadBytes : 0,
            Elapsed = Stopwatch.GetElapsedTime(startedTimestamp),
            BlockSize = blockSize,
            WindowSize = windowSize,
        };
    }

    private static int EncodeBlock(ReadOnlyMemory<byte> payload, int blockSize, long index, byte[] packet, out int dataLength)
    {
        var data = BlockData(payload, blockSize, index);
        dataLength = data.Length;
        return TftpCodec.EncodeData(packet, (ushort)index, data);
    }

    private static HandshakeCopy? CopyHandshake(ReadOnlySpan<byte> source, out string error)
    {
        if (!TftpCodec.TryRead(source, out var view, out error))
        {
            return null;
        }

        if (view.Opcode == TftpOpcode.Error)
        {
            return new HandshakeCopy
            {
                Result = new HandshakeResult
                {
                    Status = TftpTransferStatus.Failed,
                    ErrorCode = view.ErrorCode,
                    ErrorMessage = view.ErrorMessage,
                },
            };
        }

        if (view.Opcode == TftpOpcode.OptionAcknowledgement)
        {
            return new HandshakeCopy
            {
                Result = new HandshakeResult
                {
                    Status = TftpTransferStatus.Succeeded,
                    Opcode = view.Opcode,
                    Options = view.Options.ToArray(),
                },
            };
        }

        if (view.Opcode == TftpOpcode.Acknowledgement && view.Block == 0)
        {
            return new HandshakeCopy
            {
                Result = new HandshakeResult { Status = TftpTransferStatus.Succeeded, Opcode = view.Opcode, Block = 0 },
            };
        }

        if (view.Opcode == TftpOpcode.Data)
        {
            return new HandshakeCopy
            {
                Result = new HandshakeResult
                {
                    Status = TftpTransferStatus.Succeeded,
                    Opcode = view.Opcode,
                    Block = view.Block,
                    HasInitialData = true,
                    InitialData = view.Data.ToArray(),
                },
            };
        }

        return new HandshakeCopy { Unexpected = true };
    }

    private static bool TryCopyControl(
        ReadOnlySpan<byte> source,
        out ushort opcode,
        out ushort block,
        out ushort errorCode,
        out string errorMessage,
        out string decodeError)
    {
        opcode = 0;
        block = 0;
        errorCode = 0;
        errorMessage = "";
        if (!TftpCodec.TryRead(source, out var view, out decodeError))
        {
            return false;
        }

        opcode = view.Opcode;
        block = view.Block;
        errorCode = view.ErrorCode;
        errorMessage = view.ErrorMessage ?? "";
        return true;
    }

    private static DataDecision TakeData(
        ReadOnlySpan<byte> source,
        int blockSize,
        int windowSize,
        long maxBytes,
        Stream destination,
        IncrementalHash hash,
        Dictionary<long, HeldBlock> ahead,
        TransferMeter meter,
        TransferDiagnostics? diagnostics,
        ref long nextIndex,
        ref long windowEnd,
        ref long written,
        out TakeKind kind,
        out string error,
        out ushort code)
    {
        kind = TakeKind.Data;
        error = "";
        code = 0;
        if (!TftpCodec.TryRead(source, out var view, out var decodeError))
        {
            kind = TakeKind.Malformed;
            error = decodeError;
            return default;
        }

        if (view.Opcode == TftpOpcode.Error)
        {
            kind = TakeKind.PeerError;
            error = view.ErrorMessage ?? "";
            code = view.ErrorCode;
            return default;
        }

        if (view.Opcode != TftpOpcode.Data)
        {
            kind = TakeKind.Ignore;
            return default;
        }

        var decision = AcceptData(view.Block, view.Data, blockSize, windowSize, maxBytes, destination, hash, ahead, meter, diagnostics, ref nextIndex, ref windowEnd, ref written, out var failure);
        if (failure is not null)
        {
            kind = TakeKind.ProtocolFailure;
            error = failure.Value.Message;
            code = failure.Value.Code;
        }

        return decision;
    }

    private static DataDecision AcceptData(
        ushort wire,
        ReadOnlySpan<byte> data,
        int blockSize,
        int windowSize,
        long maxBytes,
        Stream destination,
        IncrementalHash hash,
        Dictionary<long, HeldBlock> ahead,
        TransferMeter meter,
        TransferDiagnostics? diagnostics,
        ref long nextIndex,
        ref long windowEnd,
        ref long written,
        out ProtocolFailure? failure)
    {
        failure = null;
        if (data.Length > blockSize)
        {
            failure = new ProtocolFailure(TftpErrorCode.IllegalOperation, "DATA exceeds the negotiated block size");
            return default;
        }

        var matched = MatchIndex(wire, nextIndex, windowSize);
        if (matched is null)
        {
            meter.CountOutOfWindow();
            return default;
        }

        var index = matched.Value;
        if (index < nextIndex)
        {
            meter.CountDuplicateData();
            return nextIndex > 1
                ? new DataDecision(true, (ushort)(nextIndex - 1), false, 0)
                : default;
        }

        if (index > nextIndex)
        {
            if (!ahead.ContainsKey(index))
            {
                ahead.Add(index, HeldBlock.Copy(data));
                meter.CountDataReceived();
            }
            else
            {
                meter.CountDuplicateData();
            }

            return nextIndex > 1
                ? new DataDecision(true, (ushort)(nextIndex - 1), false, 0)
                : default;
        }

        meter.CountDataReceived();
        var decision = WriteInOrder(index, data, blockSize, windowSize, maxBytes, destination, hash, diagnostics, ref nextIndex, ref windowEnd, ref written, out failure);
        if (failure is not null || decision.Completed)
        {
            return decision;
        }

        while (ahead.Remove(nextIndex, out var held))
        {
            var heldSpan = held.Data;
            decision = WriteInOrder(nextIndex, heldSpan, blockSize, windowSize, maxBytes, destination, hash, diagnostics, ref nextIndex, ref windowEnd, ref written, out failure);
            held.Release();
            if (failure is not null || decision.Completed)
            {
                return decision;
            }
        }

        return decision;
    }

    private static DataDecision WriteInOrder(
        long index,
        ReadOnlySpan<byte> data,
        int blockSize,
        int windowSize,
        long maxBytes,
        Stream destination,
        IncrementalHash hash,
        TransferDiagnostics? diagnostics,
        ref long nextIndex,
        ref long windowEnd,
        ref long written,
        out ProtocolFailure? failure)
    {
        failure = null;
        if (written + data.Length > maxBytes)
        {
            failure = new ProtocolFailure(TftpErrorCode.DiskFull, "article exceeds the configured maximum");
            return default;
        }

        if (data.Length > 0)
        {
            var writeStart = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
            destination.Write(data);
            if (diagnostics is not null)
            {
                diagnostics.AddFileWrite(Stopwatch.GetTimestamp() - writeStart);
            }

            var hashStart = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
            hash.AppendData(data);
            if (diagnostics is not null)
            {
                diagnostics.AddHash(Stopwatch.GetTimestamp() - hashStart);
            }

            written += data.Length;
        }

        var final = data.Length < blockSize;
        nextIndex = index + 1;
        if (final)
        {
            return new DataDecision(false, 0, true, (ushort)index);
        }

        var contiguous = nextIndex - 1;
        if (contiguous == windowEnd)
        {
            windowEnd += windowSize;
            return new DataDecision(true, (ushort)contiguous, false, 0);
        }

        return default;
    }

    private static long? MatchIndex(ushort wire, long nextIndex, int windowSize)
    {
        var low = Math.Max(1, nextIndex - windowSize);
        var high = nextIndex + windowSize;
        long? found = null;
        for (var index = low; index < high; index++)
        {
            if ((ushort)index != wire)
            {
                continue;
            }

            if (found is not null)
            {
                return null;
            }

            found = index;
        }

        return found;
    }

    private static long? ResolveAck(ushort wire, long windowStart, long sentEnd)
    {
        for (var index = windowStart; index <= sentEnd; index++)
        {
            if ((ushort)index == wire)
            {
                return index;
            }
        }

        if (windowStart > 1 && (ushort)(windowStart - 1) == wire)
        {
            return windowStart - 1;
        }

        return null;
    }

    private static ReadOnlySpan<byte> BlockData(ReadOnlyMemory<byte> payload, int blockSize, long index)
    {
        var offset = (index - 1) * blockSize;
        if (offset >= payload.Length)
        {
            return [];
        }

        var length = (int)Math.Min(blockSize, payload.Length - offset);
        return payload.Span.Slice((int)offset, length);
    }

    private static ReceiveOutcome Completed(long written, ushort finalBlock)
    {
        return new ReceiveOutcome
        {
            Status = TftpTransferStatus.Succeeded,
            Completed = true,
            PayloadBytes = written,
            FinalBlock = finalBlock,
        };
    }

    private static ReceiveOutcome Failed(string message, ushort code, long written)
    {
        return new ReceiveOutcome
        {
            Status = TftpTransferStatus.Failed,
            ErrorMessage = message,
            ErrorCode = code,
            PayloadBytes = written,
        };
    }

    private static bool IsTransport(Exception exception)
    {
        return exception is SocketException or IOException or ObjectDisposedException or InvalidOperationException;
    }

    private readonly record struct ReceiveWait(IncomingKind Kind, int Count, IPEndPoint? Remote, string Error);

    private readonly record struct DataDecision(bool Ack, ushort AckBlock, bool Completed, ushort FinalBlock);

    private readonly record struct ProtocolFailure(ushort Code, string Message);

    private enum IncomingKind
    {
        Packet,
        Timeout,
        Cancelled,
        IoFailure,
    }

    private enum TakeKind
    {
        Data,
        Ignore,
        Malformed,
        PeerError,
        ProtocolFailure,
    }

    private readonly record struct HandshakeCopy(HandshakeResult Result, bool Unexpected);

    private sealed class HeldBlock
    {
        private readonly byte[] _buffer;
        private readonly int _length;

        private HeldBlock(byte[] buffer, int length)
        {
            _buffer = buffer;
            _length = length;
        }

        public ReadOnlySpan<byte> Data => _buffer.AsSpan(0, _length);

        public static HeldBlock Copy(ReadOnlySpan<byte> data)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(data.Length);
            data.CopyTo(buffer);
            return new HeldBlock(buffer, data.Length);
        }

        public void Release() => ArrayPool<byte>.Shared.Return(_buffer);
    }
}
