using System.Buffers;
using System.Threading.Channels;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.Common.Transport.ArticleTransfer;

namespace VectorNNTP.BackFiller.Listener;

/// <summary>
/// One VATP server session over an established cache listener transport.
/// </summary>
public sealed class VatpListenerSession : IAsyncDisposable
{
    private const int MaxWriteProgressChunkBytes = 32 * 1024;

    private readonly ICacheListenerTransport _transport;
    private readonly IArticleRetentionAuthority _retention;
    private readonly BackFillerListenerRuntimeOptions _listener;
    private readonly ILogger _logger;
    private readonly ArticleTransferLimits _limits;
    private readonly Channel<OutboundItem> _outbound;
    private readonly ArticleTransferReadyRing _readyRing = new();
    private readonly Dictionary<uint, SendStream> _streams = new();
    private readonly object _streamGate = new();
    private readonly int _maxQueuedFoundPayloadBytes;
    private long _reservedFoundPayloadBytes;
    private uint _maxFramePayload = VatpProtocol.DefaultMaxFramePayload;
    private bool _clientHelloComplete;
    private bool _serverHelloSent;
    private CancellationTokenSource? _runCts;
    private int _disposed;

    /// <summary>Initializes a VATP session over an established transport.</summary>
    public VatpListenerSession(
        ICacheListenerTransport transport,
        IArticleRetentionAuthority retention,
        BackFillerListenerRuntimeOptions listener,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(retention);
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(logger);
        _transport = transport;
        _retention = retention;
        _listener = listener;
        _logger = logger;
        _limits = ArticleTransferLimits.Default;
        _maxQueuedFoundPayloadBytes = listener.MaxQueuedFoundPayloadBytes;
        _outbound = Channel.CreateBounded<OutboundItem>(new BoundedChannelOptions(_limits.MaxStreamsPerConnection * 4)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>Runs until peer close, cancellation, or fatal protocol error.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCts = linked;
        var token = linked.Token;
        var writer = RunWriterAsync(token);
        try
        {
            await RunReadLoopAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _outbound.Writer.TryComplete();
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }

            DisposeAllStreams();
            VatpListenerLogMessages.ConnectionClosed(_logger);
            linked.Dispose();
            _runCts = null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            _runCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _outbound.Writer.TryComplete();
        DisposeAllStreams();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RunReadLoopAsync(CancellationToken cancellationToken)
    {
        var readBuffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        var parseBuffer = ArrayPool<byte>.Shared.Rent(VatpProtocol.HeaderLengthBytes + (int)VatpProtocol.DefaultMaxFramePayload);
        var buffered = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = await _transport.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return;
                }

                if (bytesRead == 0)
                {
                    return;
                }

                var maxAccumulation = VatpProtocol.HeaderLengthBytes + (int)_maxFramePayload;
                if (buffered > maxAccumulation - bytesRead)
                {
                    return;
                }

                var required = buffered + bytesRead;
                if (required > parseBuffer.Length)
                {
                    var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(parseBuffer.Length * 2, required));
                    parseBuffer.AsSpan(0, buffered).CopyTo(bigger);
                    ArrayPool<byte>.Shared.Return(parseBuffer);
                    parseBuffer = bigger;
                }

                readBuffer.AsSpan(0, bytesRead).CopyTo(parseBuffer.AsSpan(buffered));
                buffered += bytesRead;

                var consumed = 0;
                while (consumed < buffered)
                {
                    var candidate = new ReadOnlySequence<byte>(parseBuffer, consumed, buffered - consumed);
                    var parsed = VatpFrameParser.ParseOneFrame(in candidate, _maxFramePayload);
                    if (parsed.Status == VatpFrameParseStatus.Incomplete)
                    {
                        break;
                    }

                    if (parsed.Status == VatpFrameParseStatus.Invalid)
                    {
                        await HandleInvalidFrameAsync(parsed, cancellationToken).ConfigureAwait(false);
                        if (parsed.ConsumedBytes <= 0)
                        {
                            return;
                        }

                        consumed += checked((int)parsed.ConsumedBytes);
                        if (IsFatalParseError(parsed.Error))
                        {
                            return;
                        }

                        continue;
                    }

                    consumed += checked((int)parsed.ConsumedBytes);
                    if (!await HandleFrameAsync(parsed.Frame!.Value, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }
                }

                if (consumed > 0)
                {
                    parseBuffer.AsSpan(consumed, buffered - consumed).CopyTo(parseBuffer);
                    buffered -= consumed;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
            ArrayPool<byte>.Shared.Return(parseBuffer);
        }
    }

    private static bool IsFatalParseError(VatpErrorCode error) =>
        error is VatpErrorCode.UnsupportedVersion
            or VatpErrorCode.InvalidHeaderLength
            or VatpErrorCode.FrameTooLarge
            or VatpErrorCode.InvalidHello
            or VatpErrorCode.InvalidMaxFramePayload;

    private async Task HandleInvalidFrameAsync(VatpFrameParseResult parsed, CancellationToken cancellationToken)
    {
        if (IsFatalParseError(parsed.Error))
        {
            await QueueFailAsync(VatpProtocol.ConnectionStreamId, parsed.Error, cancellationToken).ConfigureAwait(false);
            return;
        }

        await QueueFailAsync(VatpProtocol.ConnectionStreamId, parsed.Error, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HandleFrameAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Header.Type)
        {
            case VatpFrameType.Hello:
                return await HandleHelloAsync(frame, cancellationToken).ConfigureAwait(false);
            case VatpFrameType.Open:
                return await HandleOpenAsync(frame, cancellationToken).ConfigureAwait(false);
            case VatpFrameType.Window:
                await HandleWindowAsync(frame, cancellationToken).ConfigureAwait(false);
                return true;
            case VatpFrameType.Cancel:
                await HandleCancelAsync(frame, cancellationToken).ConfigureAwait(false);
                return true;
            default:
                await QueueFailAsync(
                    frame.Header.StreamId == VatpProtocol.ConnectionStreamId
                        ? VatpProtocol.ConnectionStreamId
                        : frame.Header.StreamId,
                    VatpErrorCode.InvalidFrameType,
                    cancellationToken).ConfigureAwait(false);
                return true;
        }
    }

    private async Task<bool> HandleHelloAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
    {
        if (frame.Header.StreamId != VatpProtocol.ConnectionStreamId)
        {
            await QueueFailAsync(VatpProtocol.ConnectionStreamId, VatpErrorCode.InvalidStreamId, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        if (_clientHelloComplete)
        {
            await QueueFailAsync(VatpProtocol.ConnectionStreamId, VatpErrorCode.InvalidHello, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        if (!VatpHello.TryDecode(frame.Payload, out var hello, out var error))
        {
            await QueueFailAsync(VatpProtocol.ConnectionStreamId, error, cancellationToken).ConfigureAwait(false);
            return false;
        }

        _maxFramePayload = Math.Min(hello.MaxFramePayload, _limits.DefaultMaxFramePayload);
        _clientHelloComplete = true;
        VatpListenerLogMessages.ClientHelloAccepted(_logger, _maxFramePayload);

        if (!_serverHelloSent)
        {
            _serverHelloSent = true;
            await QueueFrameAsync(VatpFrameEncoder.EncodeHello(_maxFramePayload), cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<bool> HandleOpenAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
    {
        var streamId = frame.Header.StreamId;
        if (!_clientHelloComplete)
        {
            await QueueFailAsync(streamId, VatpErrorCode.InvalidStateTransition, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (!VatpOpenPayload.TryDecode(frame.Payload, out var open, out var decodeError))
        {
            await QueueFailAsync(streamId, decodeError, cancellationToken).ConfigureAwait(false);
            return true;
        }

        bool rejectTable;
        lock (_streamGate)
        {
            rejectTable = _streams.ContainsKey(streamId) || _streams.Count >= _limits.MaxStreamsPerConnection;
        }

        if (rejectTable)
        {
            await QueueFailAsync(streamId, VatpErrorCode.StreamTableError, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var openResult = _retention.TryOpenTransfer(open.RequestId, open.ArticleId);
        if (openResult.Kind != VatpOpenKind.Opened || openResult.Lease is null)
        {
            openResult.Dispose();
            VatpListenerLogMessages.OpenRejected(_logger, streamId, open.RequestId);
            await QueueFailAsync(streamId, VatpErrorCode.OpenRejected, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var lease = openResult.Lease;
        var record = lease.Record;
        var meta = ArticleCanonicalTransferMeta.FromRecord(in record, lease.SelectedDateHeaderName);
        var artSize = record.ArtSize;
        if (!TryReserveFoundBytes(artSize))
        {
            lease.Dispose();
            await QueueFailAsync(streamId, VatpErrorCode.OpenRejected, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var metaBytes = VatpMetaCodec.Encode(in meta);
        var stream = new SendStream(
            streamId,
            lease,
            metaBytes,
            record.ArtData,
            artSize,
            new ArticleTransferWindow(_limits.InitialStreamWindowBytes, _limits.MaxStreamCreditBytes),
            reservedBytes: artSize);

        lock (_streamGate)
        {
            _streams[streamId] = stream;
        }

        VatpListenerLogMessages.OpenAccepted(_logger, streamId, open.RequestId);
        await QueueFrameAsync(VatpFrameEncoder.EncodeMeta(streamId, metaBytes), cancellationToken).ConfigureAwait(false);
        SignalDataSchedule();
        return true;
    }

    private async Task HandleWindowAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
    {
        var streamId = frame.Header.StreamId;
        if (frame.Payload.Length != VatpProtocol.WindowPayloadLength)
        {
            await QueueFailAsync(streamId, VatpErrorCode.InvalidFrameLength, cancellationToken).ConfigureAwait(false);
            return;
        }

        Span<byte> windowBytes = stackalloc byte[VatpProtocol.WindowPayloadLength];
        if (frame.Payload.IsSingleSegment)
        {
            frame.Payload.FirstSpan.CopyTo(windowBytes);
        }
        else
        {
            frame.Payload.CopyTo(windowBytes);
        }

        if (!VatpControlPayload.TryDecodeWindow(windowBytes, out var addCredit))
        {
            await QueueFailAsync(streamId, VatpErrorCode.InvalidFrameLength, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (addCredit == 0)
        {
            return;
        }

        var unknownStream = false;
        lock (_streamGate)
        {
            if (!_streams.TryGetValue(streamId, out var stream) || stream.IsTerminal)
            {
                unknownStream = true;
            }
            else
            {
                _ = stream.SendWindow.Add(addCredit);
                if (stream.Phase == SendStreamPhase.SendingData && stream.SentBytes < stream.ArtSize)
                {
                    _readyRing.Enqueue(streamId);
                }
            }
        }

        if (unknownStream)
        {
            await QueueFailAsync(streamId, VatpErrorCode.UnknownStream, cancellationToken).ConfigureAwait(false);
            return;
        }

        SignalDataSchedule();
    }

    private async Task HandleCancelAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
    {
        var streamId = frame.Header.StreamId;
        SendStream? stream;
        lock (_streamGate)
        {
            if (!_streams.TryGetValue(streamId, out stream))
            {
                stream = null;
            }
        }

        if (stream is null)
        {
            await QueueFailAsync(streamId, VatpErrorCode.UnknownStream, cancellationToken).ConfigureAwait(false);
            return;
        }

        RemoveStream(stream, cancelled: true);
        VatpListenerLogMessages.TransferCancelled(_logger, streamId);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task RunWriterAsync(CancellationToken cancellationToken)
    {
        var waitToRead = _outbound.Reader.WaitToReadAsync(cancellationToken);
        while (true)
        {
            if (!await waitToRead.ConfigureAwait(false))
            {
                break;
            }

            while (_outbound.Reader.TryRead(out var item))
            {
                if (item.Kind == OutboundItemKind.ScheduleData)
                {
                    continue;
                }

                await WriteEncodedFrameAsync(item.Frame!.Value, cancellationToken).ConfigureAwait(false);
                HandlePostWrite(item.Frame!.Value);
            }

            while (TryTakeDataFrame(out var dataFrame, out var completedStreamId))
            {
                await WriteEncodedFrameAsync(dataFrame, cancellationToken).ConfigureAwait(false);
                if (completedStreamId is { } streamId)
                {
                    await WriteEncodedFrameAsync(VatpFrameEncoder.EncodeEnd(streamId), cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            waitToRead = _outbound.Reader.WaitToReadAsync(cancellationToken);
        }

        while (TryTakeDataFrame(out var trailing, out var trailingCompleted))
        {
            await WriteEncodedFrameAsync(trailing, cancellationToken).ConfigureAwait(false);
            if (trailingCompleted is { } streamId)
            {
                await WriteEncodedFrameAsync(VatpFrameEncoder.EncodeEnd(streamId), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private void HandlePostWrite(in VatpFrameEncoder.EncodedFrame frame)
    {
        var header = VatpFrameHeader.ReadFrom(frame.Header.Span);
        if (header.Type != VatpFrameType.Meta)
        {
            return;
        }

        lock (_streamGate)
        {
            if (!_streams.TryGetValue(header.StreamId, out var stream))
            {
                return;
            }

            stream.Phase = SendStreamPhase.SendingData;
            if (stream.SentBytes < stream.ArtSize && stream.SendWindow.HasCredit)
            {
                _readyRing.Enqueue(header.StreamId);
            }
        }

        SignalDataSchedule();
    }

    private bool TryTakeDataFrame(out VatpFrameEncoder.EncodedFrame encoded, out uint? completedStreamId)
    {
        encoded = default;
        completedStreamId = null;
        lock (_streamGate)
        {
            var attempts = _readyRing.Count;
            while (attempts-- > 0 && _readyRing.TryTakeNext(out var streamId))
            {
                if (!_streams.TryGetValue(streamId, out var stream)
                    || stream.IsTerminal
                    || stream.Phase != SendStreamPhase.SendingData)
                {
                    continue;
                }

                var remaining = stream.ArtSize - stream.SentBytes;
                if (remaining <= 0)
                {
                    CompleteStreamLocked(stream);
                    continue;
                }

                if (!stream.SendWindow.HasCredit)
                {
                    // Zero-credit streams stay off the ring until WINDOW resumes them.
                    continue;
                }

                var length = ArticleTransferReadyRing.ComputeDataPayloadLength(
                    remaining,
                    stream.SendWindow.Credit,
                    _maxFramePayload);
                if (length <= 0)
                {
                    continue;
                }

                if (!stream.SendWindow.TryConsume(length))
                {
                    continue;
                }

                var fin = length == remaining;
                var payload = stream.ArtData.Slice(stream.SentBytes, length);
                stream.SentBytes += length;
                encoded = VatpFrameEncoder.EncodeData(streamId, payload, fin);
                if (stream.SentBytes < stream.ArtSize)
                {
                    _readyRing.Enqueue(streamId);
                }
                else
                {
                    CompleteStreamLocked(stream);
                    completedStreamId = streamId;
                }

                return true;
            }
        }

        return false;
    }

    private void CompleteStreamLocked(SendStream stream)
    {
        _readyRing.Remove(stream.StreamId);
        stream.Phase = SendStreamPhase.Completed;
        VatpListenerLogMessages.TransferCompleted(_logger, stream.StreamId);
        ReleaseFoundBytes(stream.ReservedBytes);
        stream.Lease.Dispose();
        _streams.Remove(stream.StreamId);
    }

    private void RemoveStream(SendStream stream, bool cancelled)
    {
        lock (_streamGate)
        {
            RemoveStreamLocked(stream, cancelled);
        }
    }

    private void RemoveStreamLocked(SendStream stream, bool cancelled)
    {
        if (!_streams.Remove(stream.StreamId))
        {
            return;
        }

        _readyRing.Remove(stream.StreamId);
        stream.Phase = cancelled ? SendStreamPhase.Cancelled : SendStreamPhase.Failed;
        ReleaseFoundBytes(stream.ReservedBytes);
        stream.Lease.Dispose();
    }

    private void DisposeAllStreams()
    {
        SendStream[] streams;
        lock (_streamGate)
        {
            streams = [.. _streams.Values];
            _streams.Clear();
            _readyRing.Clear();
        }

        foreach (var stream in streams)
        {
            ReleaseFoundBytes(stream.ReservedBytes);
            stream.Lease.Dispose();
        }
    }

    private async Task QueueFrameAsync(VatpFrameEncoder.EncodedFrame frame, CancellationToken cancellationToken) =>
        await _outbound.Writer.WriteAsync(new OutboundItem(OutboundItemKind.Frame, frame), cancellationToken).ConfigureAwait(false);

    private async Task QueueFailAsync(uint streamId, VatpErrorCode error, CancellationToken cancellationToken)
    {
        if (streamId != VatpProtocol.ConnectionStreamId)
        {
            VatpListenerLogMessages.TransferFailed(_logger, streamId, (ushort)error);
            lock (_streamGate)
            {
                if (_streams.TryGetValue(streamId, out var stream))
                {
                    RemoveStreamLocked(stream, cancelled: false);
                }
            }
        }

        await QueueFrameAsync(VatpFrameEncoder.EncodeFail(streamId, error), cancellationToken).ConfigureAwait(false);
    }

    private void SignalDataSchedule() =>
        _ = _outbound.Writer.TryWrite(new OutboundItem(OutboundItemKind.ScheduleData, null));

    private async Task WriteEncodedFrameAsync(VatpFrameEncoder.EncodedFrame frame, CancellationToken cancellationToken)
    {
        await WriteFullyAsync(frame.Header, cancellationToken).ConfigureAwait(false);
        if (!frame.Payload.IsEmpty)
        {
            await WriteFullyAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteFullyAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var written = 0;
        while (written < payload.Length)
        {
            var chunk = Math.Min(payload.Length - written, MaxWriteProgressChunkBytes);
            var accepted = await _transport.WriteAsync(payload.Slice(written, chunk), cancellationToken).ConfigureAwait(false);
            if (accepted <= 0)
            {
                throw new IOException("Transport returned zero accepted bytes.");
            }

            written += accepted;
        }
    }

    private bool TryReserveFoundBytes(int payloadBytes)
    {
        while (true)
        {
            var current = Volatile.Read(ref _reservedFoundPayloadBytes);
            if (current > _maxQueuedFoundPayloadBytes - payloadBytes)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _reservedFoundPayloadBytes, current + payloadBytes, current) == current)
            {
                return true;
            }
        }
    }

    private void ReleaseFoundBytes(int payloadBytes) =>
        Interlocked.Add(ref _reservedFoundPayloadBytes, -payloadBytes);

    private enum OutboundItemKind
    {
        Frame,
        ScheduleData,
    }

    private readonly record struct OutboundItem(OutboundItemKind Kind, VatpFrameEncoder.EncodedFrame? Frame);

    private enum SendStreamPhase
    {
        PendingMeta,
        SendingData,
        Completed,
        Cancelled,
        Failed,
    }

    private sealed class SendStream
    {
        public SendStream(
            uint streamId,
            VatpTransferLease lease,
            byte[] metaPayload,
            ReadOnlyMemory<byte> artData,
            int artSize,
            ArticleTransferWindow sendWindow,
            int reservedBytes)
        {
            StreamId = streamId;
            Lease = lease;
            MetaPayload = metaPayload;
            ArtData = artData;
            ArtSize = artSize;
            SendWindow = sendWindow;
            ReservedBytes = reservedBytes;
        }

        public uint StreamId { get; }

        public VatpTransferLease Lease { get; }

        public byte[] MetaPayload { get; }

        public ReadOnlyMemory<byte> ArtData { get; }

        public int ArtSize { get; }

        public int SentBytes { get; set; }

        public ArticleTransferWindow SendWindow { get; }

        public int ReservedBytes { get; }

        public SendStreamPhase Phase { get; set; } = SendStreamPhase.PendingMeta;

        public bool IsTerminal =>
            Phase is SendStreamPhase.Completed or SendStreamPhase.Cancelled or SendStreamPhase.Failed;
    }
}
