using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Channels;
using VectorNNTP.BackFiller.Configuration;

namespace VectorNNTP.BackFiller.Listener;

/// <summary>
/// One connected Listener protocol session: parse loop, bounded RequestId table,
/// bounded handler concurrency, serialized writes, and ReceiptAck correlation.
/// </summary>
public sealed class CacheListenerSession : IAsyncDisposable
{
    private const int MaxWriteProgressChunkBytes = 32 * 1024;

    private readonly ICacheListenerTransport _transport;
    private readonly CacheListenerRetentionHandler _handler;
    private readonly Channel<OutboundDescriptor> _outbound;
    private readonly SemaphoreSlim _processingLimiter;
    private readonly ConcurrentDictionary<uint, RequestContext> _requests = new();
    private readonly ConcurrentDictionary<uint, Task> _processingTasks = new();
    private readonly ConcurrentDictionary<uint, CancellationTokenSource> _receiptTimeouts = new();
    private readonly object _stateGate = new();
    private readonly int _parserAccumulationMaxBytes;
    private readonly TimeSpan _receiptAckTimeout;
    private readonly int _maxQueuedFoundPayloadBytes;
    private long _reservedFoundPayloadBytes;
    private CancellationTokenSource? _runCts;
    private CacheListenerSessionState _state = CacheListenerSessionState.Running;
    private int _disposed;

    /// <summary>Initializes a session over an established transport.</summary>
    public CacheListenerSession(
        ICacheListenerTransport transport,
        CacheListenerRetentionHandler handler,
        BackFillerListenerRuntimeOptions listener)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(listener);
        _transport = transport;
        _handler = handler;
        _parserAccumulationMaxBytes = listener.ParserAccumulationMaxBytes;
        _receiptAckTimeout = listener.AwaitingReceiptAckTimeout;
        _maxQueuedFoundPayloadBytes = listener.MaxQueuedFoundPayloadBytes;
        _processingLimiter = new SemaphoreSlim(
            ListenerProtocol.MaxConcurrentProcessingRequests,
            ListenerProtocol.MaxConcurrentProcessingRequests);
        _outbound = Channel.CreateBounded<OutboundDescriptor>(new BoundedChannelOptions(ListenerProtocol.MaxOutboundResponses)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>Gets the session lifecycle state.</summary>
    public CacheListenerSessionState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    /// <summary>Gets outstanding RequestId count.</summary>
    public int OutstandingRequestCount => _requests.Count;

    /// <summary>Gets reserved Found payload bytes.</summary>
    public long ReservedFoundPayloadBytes => Volatile.Read(ref _reservedFoundPayloadBytes);

    /// <summary>Runs until peer close, cancellation, fatal protocol error, or shutdown.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_stateGate)
        {
            if (_runCts is not null || _state != CacheListenerSessionState.Running)
            {
                linked.Dispose();
                throw new InvalidOperationException("Session is already running.");
            }

            _runCts = linked;
        }

        var token = linked.Token;
        var writer = RunWriterAsync(token);
        var reader = RunReadLoopAsync(token);
        var first = await Task.WhenAny(reader, writer).ConfigureAwait(false);
        if (first.IsFaulted)
        {
            BeginForcedShutdown();
        }
        else if (State != CacheListenerSessionState.ForcedShutdown)
        {
            BeginGracefulShutdown();
        }

        try
        {
            await Task.WhenAll(Observe(reader, token), Observe(writer, token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }

        await ObserveProcessingAsync().ConfigureAwait(false);
        CancelReceiptTimeouts();
        TerminalizeAll();
        lock (_stateGate)
        {
            _state = CacheListenerSessionState.Completed;
            if (ReferenceEquals(_runCts, linked))
            {
                _runCts = null;
            }
        }

        linked.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        BeginForcedShutdown();
        _processingLimiter.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task Observe(Task task, CancellationToken token)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task RunReadLoopAsync(CancellationToken cancellationToken)
    {
        var readBuffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        var parseBuffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
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
                    BeginForcedShutdown();
                    return;
                }
                if (bytesRead == 0)
                {
                    break;
                }

                if (buffered > _parserAccumulationMaxBytes - bytesRead)
                {
                    BeginForcedShutdown();
                    return;
                }

                var required = buffered + bytesRead;
                if (required > parseBuffer.Length)
                {
                    var bigger = ArrayPool<byte>.Shared.Rent(
                        Math.Min(_parserAccumulationMaxBytes, Math.Max(parseBuffer.Length * 2, required)));
                    parseBuffer.AsSpan(0, buffered).CopyTo(bigger);
                    ArrayPool<byte>.Shared.Return(parseBuffer);
                    parseBuffer = bigger;
                }

                readBuffer.AsSpan(0, bytesRead).CopyTo(parseBuffer.AsSpan(buffered));
                buffered += bytesRead;

                var consumed = 0;
                while (consumed < buffered)
                {
                    var candidateLength = buffered - consumed;
                    if (candidateLength >= ListenerProtocol.HeaderLengthBytes)
                    {
                        var header = ListenerFrameHeader.ReadFrom(parseBuffer.AsSpan(consumed, ListenerProtocol.HeaderLengthBytes));
                        long declared = (long)ListenerProtocol.HeaderLengthBytes + header.PayloadLength;
                        if (declared > _parserAccumulationMaxBytes)
                        {
                            BeginForcedShutdown();
                            return;
                        }
                    }

                    var candidate = new ReadOnlySequence<byte>(parseBuffer, consumed, candidateLength);
                    var parsed = ListenerProtocolParser.ParseOneFrame(in candidate);
                    if (parsed.Status == ListenerFrameParseStatus.Incomplete)
                    {
                        break;
                    }

                    if (parsed.Status == ListenerFrameParseStatus.Invalid)
                    {
                        var fatal = await HandleInvalidAsync(parsed, parseBuffer, consumed, cancellationToken)
                            .ConfigureAwait(false);
                        consumed += checked((int)parsed.ConsumedBytes);
                        if (fatal)
                        {
                            return;
                        }

                        continue;
                    }

                    consumed += checked((int)parsed.ConsumedBytes);
                    await HandleFrameAsync(parsed.Frame!.Value, cancellationToken).ConfigureAwait(false);
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

    private async Task<bool> HandleInvalidAsync(
        ListenerFrameParseResult parsed,
        byte[] parseBuffer,
        int frameOffset,
        CancellationToken cancellationToken)
    {
        var errorCode = parsed.Error switch
        {
            ListenerFrameParseError.UnsupportedVersion => ListenerProtocolErrorCode.UnsupportedVersion,
            ListenerFrameParseError.UnsupportedOpcode => ListenerProtocolErrorCode.UnsupportedOpcode,
            ListenerFrameParseError.InvalidHeaderLength => ListenerProtocolErrorCode.InvalidHeaderLength,
            ListenerFrameParseError.InvalidReserved => ListenerProtocolErrorCode.InvalidFrameLength,
            ListenerFrameParseError.InvalidFrameLength => ListenerProtocolErrorCode.InvalidFrameLength,
            ListenerFrameParseError.InvalidRequestId => ListenerProtocolErrorCode.InvalidRequestId,
            ListenerFrameParseError.InvalidMessageIdMd5 => ListenerProtocolErrorCode.InvalidMessageIdMd5,
            _ => ListenerProtocolErrorCode.InvalidFrameLength,
        };

        var fatal = parsed.Error is ListenerFrameParseError.UnsupportedVersion
            or ListenerFrameParseError.InvalidHeaderLength
            or ListenerFrameParseError.InvalidFrameLength;
        if (fatal)
        {
            BeginForcedShutdown();
            return true;
        }

        uint requestId = parseBuffer.Length - frameOffset >= ListenerProtocol.HeaderLengthBytes
            ? ListenerFrameHeader.ReadFrom(parseBuffer.AsSpan(frameOffset, ListenerProtocol.HeaderLengthBytes)).RequestId
            : 0;
        await QueueErrorAsync(requestId, errorCode, terminalize: false, cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async Task HandleFrameAsync(ListenerParsedFrame frame, CancellationToken cancellationToken)
    {
        if (frame.Header.Opcode == ListenerOpcode.GetRequest)
        {
            await HandleGetRequestAsync(frame, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (frame.Header.Opcode == ListenerOpcode.GetReceiptAck)
        {
            await HandleReceiptAckAsync(frame, cancellationToken).ConfigureAwait(false);
            return;
        }

        await QueueErrorAsync(frame.Header.RequestId, ListenerProtocolErrorCode.UnsupportedOpcode, terminalize: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleGetRequestAsync(ListenerParsedFrame frame, CancellationToken cancellationToken)
    {
        var requestId = frame.Header.RequestId;
        if (State != CacheListenerSessionState.Running)
        {
            await QueueErrorAsync(requestId, ListenerProtocolErrorCode.ServerShuttingDown, terminalize: false, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (_requests.Count >= ListenerProtocol.MaxOutstandingRequests)
        {
            await QueueErrorAsync(requestId, ListenerProtocolErrorCode.RequestTableOverflow, terminalize: false, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var context = new RequestContext(requestId, frame.Payload.ToArray());
        if (!_requests.TryAdd(requestId, context))
        {
            await QueueErrorAsync(requestId, ListenerProtocolErrorCode.DuplicateRequestId, terminalize: false, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        _processingTasks[requestId] = ProcessRequestAsync(context, cancellationToken);
    }

    private async Task ObserveProcessingAsync()
    {
        var tasks = _processingTasks.Values.ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private async Task ProcessRequestAsync(RequestContext context, CancellationToken callerToken)
    {
        var sessionToken = _runCts?.Token ?? callerToken;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, sessionToken);
        var token = linked.Token;
        try
        {
            await _processingLimiter.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Terminalize(context.RequestId, context);
            return;
        }

        var reserved = 0;
        var queued = false;
        try
        {
            var dispatch = _handler.HandleGetRequest(context.RequestId, context.MessageIdMd5);
            OutboundDescriptor descriptor;
            if (dispatch.Kind == CacheListenerDispatchKind.Found)
            {
                if (!TryReserveFoundBytes(dispatch.FoundPayload.Length))
                {
                    descriptor = OutboundDescriptor.Error(
                        context.RequestId,
                        ListenerProtocolErrorCode.InternalError,
                        terminalize: true);
                }
                else
                {
                    reserved = dispatch.FoundPayload.Length;
                    descriptor = OutboundDescriptor.Found(context.RequestId, dispatch.FoundPayload, reserved);
                }
            }
            else if (dispatch.Kind == CacheListenerDispatchKind.NotFound)
            {
                descriptor = OutboundDescriptor.NotFound(context.RequestId);
            }
            else
            {
                descriptor = OutboundDescriptor.Error(context.RequestId, dispatch.ErrorCode, terminalize: true);
            }

            await _outbound.Writer.WriteAsync(descriptor, token).ConfigureAwait(false);
            queued = true;
        }
        catch (OperationCanceledException)
        {
            Terminalize(context.RequestId, context);
        }
        catch
        {
            await QueueErrorAsync(context.RequestId, ListenerProtocolErrorCode.InternalError, terminalize: true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            if (!queued && reserved > 0)
            {
                ReleaseFoundBytes(reserved);
            }

            _ = _processingLimiter.Release();
            _ = _processingTasks.TryRemove(context.RequestId, out _);
        }
    }

    private async Task HandleReceiptAckAsync(ListenerParsedFrame frame, CancellationToken cancellationToken)
    {
        var requestId = frame.Header.RequestId;
        if (!_requests.TryGetValue(requestId, out var context))
        {
            await QueueErrorAsync(requestId, ListenerProtocolErrorCode.InvalidRequestId, terminalize: false, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var transition = context.RegisterReceiptAck();
        if (transition == ReceiptAckTransition.AlreadyAcknowledged || transition == ReceiptAckTransition.PendingFoundWrite)
        {
            return;
        }

        if (transition == ReceiptAckTransition.Terminalize)
        {
            Terminalize(requestId, context);
            return;
        }

        await QueueErrorAsync(requestId, ListenerProtocolErrorCode.InvalidRequestId, terminalize: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task QueueErrorAsync(
        uint requestId,
        ListenerProtocolErrorCode errorCode,
        bool terminalize,
        CancellationToken cancellationToken)
    {
        var normalized = requestId == 0 ? 1u : requestId;
        await _outbound.Writer.WriteAsync(
                OutboundDescriptor.Error(normalized, errorCode, terminalize),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunWriterAsync(CancellationToken cancellationToken)
    {
        await foreach (var descriptor in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var status = await WriteAsync(descriptor, cancellationToken).ConfigureAwait(false);
            ApplyCompletion(descriptor, status);
        }
    }

    private async Task<bool> WriteAsync(OutboundDescriptor descriptor, CancellationToken cancellationToken)
    {
        try
        {
            if (descriptor.FoundHeader.Length > 0)
            {
                await WriteFullyAsync(descriptor.FoundHeader, cancellationToken).ConfigureAwait(false);
                await WriteFullyAsync(descriptor.FoundPayload, cancellationToken).ConfigureAwait(false);
                return true;
            }

            await WriteFullyAsync(descriptor.Frame, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
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

    private void ApplyCompletion(OutboundDescriptor descriptor, bool completed)
    {
        if (descriptor.ReservedFoundBytes > 0)
        {
            ReleaseFoundBytes(descriptor.ReservedFoundBytes);
        }

        if (!completed)
        {
            Terminalize(descriptor.RequestId);
            return;
        }

        if (descriptor.IsFound)
        {
            if (!_requests.TryGetValue(descriptor.RequestId, out var context))
            {
                return;
            }

            if (context.MarkFoundWriteCompleted())
            {
                Terminalize(descriptor.RequestId, context);
                return;
            }

            StartReceiptTimeout(descriptor.RequestId, context);
            return;
        }

        if (descriptor.Terminalize)
        {
            Terminalize(descriptor.RequestId);
        }
    }

    private void Terminalize(uint requestId, RequestContext? expected = null)
    {
        CancelReceiptTimeout(requestId);
        if (expected is not null)
        {
            if (_requests.TryRemove(new KeyValuePair<uint, RequestContext>(requestId, expected)))
            {
                _handler.Release(requestId);
            }

            return;
        }

        if (_requests.TryRemove(requestId, out _))
        {
            _handler.Release(requestId);
        }
    }

    private void TerminalizeAll()
    {
        foreach (var requestId in _requests.Keys.ToArray())
        {
            Terminalize(requestId);
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

    private void StartReceiptTimeout(uint requestId, RequestContext context)
    {
        var timeoutCts = new CancellationTokenSource();
        if (!_receiptTimeouts.TryAdd(requestId, timeoutCts))
        {
            timeoutCts.Dispose();
            return;
        }

        _ = AwaitReceiptTimeoutAsync(requestId, context, timeoutCts);
    }

    private async Task AwaitReceiptTimeoutAsync(
        uint requestId,
        RequestContext context,
        CancellationTokenSource timeoutCts)
    {
        try
        {
            await Task.Delay(_receiptAckTimeout, timeoutCts.Token).ConfigureAwait(false);
            if (_requests.TryGetValue(requestId, out var current) && ReferenceEquals(current, context))
            {
                Terminalize(requestId, context);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_receiptTimeouts.TryGetValue(requestId, out var current)
                && ReferenceEquals(current, timeoutCts)
                && _receiptTimeouts.TryRemove(new KeyValuePair<uint, CancellationTokenSource>(requestId, timeoutCts)))
            {
                timeoutCts.Dispose();
            }
        }
    }

    private void CancelReceiptTimeout(uint requestId)
    {
        if (_receiptTimeouts.TryRemove(requestId, out var timeoutCts))
        {
            timeoutCts.Cancel();
            timeoutCts.Dispose();
        }
    }

    private void CancelReceiptTimeouts()
    {
        foreach (var pair in _receiptTimeouts)
        {
            if (_receiptTimeouts.TryRemove(pair.Key, out var timeoutCts))
            {
                timeoutCts.Cancel();
                timeoutCts.Dispose();
            }
        }
    }

    private void BeginGracefulShutdown()
    {
        lock (_stateGate)
        {
            if (_state == CacheListenerSessionState.Running)
            {
                _state = CacheListenerSessionState.GracefulShutdown;
            }
        }

        _outbound.Writer.TryComplete();
    }

    private void BeginForcedShutdown()
    {
        lock (_stateGate)
        {
            if (_state is CacheListenerSessionState.Completed)
            {
                return;
            }

            _state = CacheListenerSessionState.ForcedShutdown;
        }

        try
        {
            _runCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _outbound.Writer.TryComplete();
    }

    private enum ReceiptAckTransition
    {
        Invalid,
        AlreadyAcknowledged,
        PendingFoundWrite,
        Terminalize,
    }

    private sealed class RequestContext(uint requestId, byte[] messageIdMd5)
    {
        private int _receiptAcked;
        private int _foundWritten;

        public uint RequestId { get; } = requestId;

        public ReadOnlyMemory<byte> MessageIdMd5 { get; } = messageIdMd5;

        public ReceiptAckTransition RegisterReceiptAck()
        {
            if (Interlocked.Exchange(ref _receiptAcked, 1) == 1)
            {
                return ReceiptAckTransition.AlreadyAcknowledged;
            }

            return Volatile.Read(ref _foundWritten) == 1
                ? ReceiptAckTransition.Terminalize
                : ReceiptAckTransition.PendingFoundWrite;
        }

        public bool MarkFoundWriteCompleted()
        {
            Volatile.Write(ref _foundWritten, 1);
            return Volatile.Read(ref _receiptAcked) == 1;
        }
    }

    private readonly record struct OutboundDescriptor(
        uint RequestId,
        ReadOnlyMemory<byte> Frame,
        ReadOnlyMemory<byte> FoundHeader,
        ReadOnlyMemory<byte> FoundPayload,
        int ReservedFoundBytes,
        bool IsFound,
        bool Terminalize)
    {
        public static OutboundDescriptor Found(uint requestId, ReadOnlyMemory<byte> payload, int reserved)
        {
            var encoded = ListenerProtocolEncoder.EncodeGetResponseFound(requestId, payload);
            return new OutboundDescriptor(requestId, default, encoded.Header, encoded.Payload, reserved, true, false);
        }

        public static OutboundDescriptor NotFound(uint requestId) =>
            new(requestId, ListenerProtocolEncoder.EncodeGetResponseNotFound(requestId), default, default, 0, false, true);

        public static OutboundDescriptor Error(uint requestId, ListenerProtocolErrorCode code, bool terminalize) =>
            new(requestId, ListenerProtocolEncoder.EncodeGetResponseError(requestId, code), default, default, 0, false, terminalize);
    }
}
