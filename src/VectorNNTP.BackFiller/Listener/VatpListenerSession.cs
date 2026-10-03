using System.Buffers;
using System.Threading.Channels;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.Common.Transport.ArticleTransfer;

namespace VectorNNTP.BackFiller.Listener
{
    /// <summary>
    /// One VATP server session over an established cache listener transport.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The read loop is the only frame dispatcher. A separate writer task drains
    /// <see cref="VatpListenerSession.OutboundItem"/> values and DATA frames. The outbound channel is bounded to
    /// <see cref="ArticleTransferLimits.MaxStreamsPerConnection"/> times four, waits when full, has one reader
    /// and many writers, and does not allow synchronous continuations. Stream-table and ready-ring updates that
    /// must stay together take <see cref="VatpListenerSession._streamGate"/>. Reserved payload bytes use
    /// <see cref="Interlocked"/> and are not covered by that lock.
    /// </para>
    /// <para>
    /// The first valid client HELLO is required. Its maximum payload is reduced to the smaller of the offer and
    /// <see cref="ArticleTransferLimits.DefaultMaxFramePayload"/>, then echoed in one server HELLO. OPEN before
    /// that HELLO stops the read loop after FAIL. Later OPEN rejection keeps the connection. WINDOW and CANCEL
    /// keep the connection. Any other frame type, including peer DATA, META, END, FAIL, STORE, and RESULT, is
    /// answered with FAIL <see cref="VatpErrorCode.InvalidFrameType"/> and the connection stays up.
    /// </para>
    /// <para>
    /// Limits other than <see cref="BackFillerListenerRuntimeOptions.MaxQueuedFoundPayloadBytes"/> come from
    /// <see cref="ArticleTransferLimits.Default"/>.
    /// </para>
    /// </remarks>
    internal sealed class VatpListenerSession : IAsyncDisposable
    {
        /// <summary>Largest slice passed to one transport write so the no-progress timeout applies at least this often.</summary>
        private const int MaxWriteProgressChunkBytes = 32 * 1024;

        /// <summary>Connected byte transport. Not used to accept or bind.</summary>
        private readonly ICacheListenerTransport _transport;
        /// <summary>OPEN authority. Request-id consumption is decided inside <see cref="IArticleRetentionAuthority.TryOpenTransfer"/>.</summary>
        private readonly IArticleRetentionAuthority _retention;
        /// <summary>Listener bounds captured for <see cref="BackFillerListenerRuntimeOptions.MaxQueuedFoundPayloadBytes"/>.</summary>
        private readonly BackFillerListenerRuntimeOptions _listener;
        /// <summary>Session logger. Article bytes are not written to it.</summary>
        private readonly ILogger _logger;
        /// <summary><see cref="ArticleTransferLimits.Default"/>. Not overwritten from configuration.</summary>
        private readonly ArticleTransferLimits _limits;
        /// <summary>
        /// Frames and schedule wakeups for <see cref="RunWriterAsync"/>. Capacity is
        /// <see cref="ArticleTransferLimits.MaxStreamsPerConnection"/> times four.
        /// </summary>
        private readonly Channel<OutboundItem> _outbound;
        /// <summary>Stream ids eligible for another DATA frame. Mutated with the stream table under <see cref="_streamGate"/>.</summary>
        private readonly ArticleTransferReadyRing _readyRing = new();
        /// <summary>OPEN streams that have not been completed, cancelled, failed, or disposed. Guarded by <see cref="_streamGate"/>.</summary>
        private readonly Dictionary<uint, SendStream> _streams = new();
        /// <summary>Guards <see cref="_streams"/> and ready-ring updates that must observe the same stream phase.</summary>
        private readonly object _streamGate = new();
        /// <summary>Cap copied from <see cref="BackFillerListenerRuntimeOptions.MaxQueuedFoundPayloadBytes"/>.</summary>
        private readonly int _maxQueuedFoundPayloadBytes;
        /// <summary>Sum of article sizes reserved for streams that still hold a found-byte reservation.</summary>
        private long _reservedFoundPayloadBytes;
        /// <summary>
        /// Current maximum DATA payload. Starts at <see cref="VatpProtocol.DefaultMaxFramePayload"/> and can only shrink at HELLO.
        /// </summary>
        private uint _maxFramePayload = VatpProtocol.DefaultMaxFramePayload;
        /// <summary>Set on the read loop after the first valid client HELLO. A second HELLO fails the connection.</summary>
        private bool _clientHelloComplete;
        /// <summary>Set before the single server HELLO is queued. The read loop is the only writer.</summary>
        private bool _serverHelloSent;
        /// <summary>Linked session token created by <see cref="RunAsync"/> and disposed in its <c>finally</c>. Null before and after the run.</summary>
        private CancellationTokenSource? _runCts;
        /// <summary>One after the first <see cref="DisposeAsync"/>.</summary>
        private int _disposed;
        /// <summary>One after the first <see cref="RequestSessionTermination"/>. Makes that transition idempotent.</summary>
        private int _terminating;

        /// <summary>Gets the number of active send streams (test observation).</summary>
        /// <remarks>Taken under <see cref="_streamGate"/>. The table drops a stream when that stream becomes terminal.</remarks>
        internal int ActiveStreamCount
        {
            get
            {
                lock (_streamGate)
                {
                    return _streams.Count;
                }
            }
        }

        /// <summary>Gets reserved outbound/found payload bytes (test observation).</summary>
        /// <remarks>
        /// Updated with <see cref="Interlocked"/> by OPEN reservation, stream completion, cancel, FAIL removal, and session disposal.
        /// The value is not clamped.
        /// </remarks>
        internal long ReservedFoundPayloadBytes => Volatile.Read(ref _reservedFoundPayloadBytes);

        /// <summary>Initializes a VATP session over an established transport.</summary>
        /// <param name="transport">Connected transport. Disposed by <see cref="DisposeAsync"/>.</param>
        /// <param name="retention">OPEN authority. This session does not retain articles itself.</param>
        /// <param name="listener">Listener bounds. Only <see cref="BackFillerListenerRuntimeOptions.MaxQueuedFoundPayloadBytes"/> is read.</param>
        /// <param name="logger">Logger passed to <see cref="VatpListenerLogMessages"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
        internal VatpListenerSession(
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
        /// <param name="cancellationToken">Cancels the linked session token. Disposal cancels that same token.</param>
        /// <returns>
        /// A task that completes after the writer finishes and send streams are disposed.
        /// A writer failure is rethrown after that cleanup.
        /// </returns>
        /// <remarks>
        /// The outbound writer is completed in <c>finally</c> without cancelling first, so a queued FAIL can still drain.
        /// Cancellation is reserved for <see cref="RequestSessionTermination"/>. A reader exception other than
        /// cancellation or <see cref="ChannelClosedException"/> also propagates after cleanup unless the writer
        /// failure is rethrown from <c>finally</c> in its place. <see cref="VatpListenerLogMessages.ConnectionClosed"/>
        /// is written once on this path.
        /// </remarks>
        internal async Task RunAsync(CancellationToken cancellationToken)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runCts = linked;
            var token = linked.Token;
            var writer = RunWriterAsync(token);
            try
            {
                try
                {
                    await RunReadLoopAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Session or host termination unblocked the reader / QueueFrameAsync.
                }
                catch (ChannelClosedException)
                {
                    // Outbound completed while a producer awaited capacity.
                }
            }
            finally
            {
                // Complete outbound so the writer can drain (e.g. queued FAIL) or exit.
                // Do not cancel here: cancellation is reserved for half-failure paths that must
                // unblock a live reader / QueueFrameAsync. Cancelling before drain would drop
                // protocol responses the peer is still entitled to observe.
                _ = _outbound.Writer.TryComplete();

                Exception? writerError = null;
                try
                {
                    await writer.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    // Preserve the transport fault for the caller, but never skip stream/lease cleanup.
                    writerError = ex;
                }

                DisposeAllStreams();
                VatpListenerLogMessages.ConnectionClosed(_logger);
                linked.Dispose();
                _runCts = null;

                if (writerError is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(writerError);
                }
            }
        }

        /// <summary>Cancels the session, releases any remaining send streams, and disposes the transport.</summary>
        /// <returns>A task that completes when transport disposal finishes. A second call completes immediately.</returns>
        /// <remarks>
        /// Does not wait for <see cref="RunAsync"/>. Stream disposal drops leases and reserved bytes.
        /// The transport is disposed even when <see cref="RunAsync"/> has already completed.
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            RequestSessionTermination();
            DisposeAllStreams();
            await _transport.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Idempotent session terminal transition: cancel the session token and complete the outbound
        /// channel so reader, writer, and blocked <see cref="QueueFrameAsync"/> producers all exit.
        /// </summary>
        /// <summary>
        /// Idempotent session terminal transition: cancel the session token and complete the outbound
        /// channel so reader, writer, and blocked <see cref="QueueFrameAsync"/> producers all exit.
        /// </summary>
        /// <remarks><see cref="ObjectDisposedException"/> from cancelling an already disposed token is ignored.</remarks>
        private void RequestSessionTermination()
        {
            if (Interlocked.Exchange(ref _terminating, 1) == 1)
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

            _ = _outbound.Writer.TryComplete();
        }

        /// <summary>Reads transport bytes, parses VATP frames, and dispatches them until the session should stop reading.</summary>
        /// <param name="cancellationToken">Session token. Cancellation returns without a FAIL frame.</param>
        /// <returns>A task that completes when the read side stops. Buffers rented from <see cref="ArrayPool{T}"/> are returned.</returns>
        /// <remarks>
        /// A zero read returns as peer close. <see cref="TimeoutException"/> requests session termination and returns.
        /// Any other read exception requests termination and is rethrown. If the next read would make the parse buffer
        /// larger than the header plus <see cref="_maxFramePayload"/>, the loop returns without queueing FAIL.
        /// An invalid frame that reports no consumed bytes also ends the loop after <see cref="HandleInvalidFrameAsync"/>.
        /// </remarks>
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
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (TimeoutException)
                    {
                        // Reader-side terminal I/O failure: stop the whole session.
                        RequestSessionTermination();
                        return;
                    }
                    catch (Exception)
                    {
                        RequestSessionTermination();
                        throw;
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

        /// <summary>Selects parse errors that end the read loop after one connection-scoped FAIL.</summary>
        /// <param name="error">Error reported by <see cref="VatpFrameParser"/>.</param>
        /// <returns>
        /// <see langword="true"/> for unsupported version, invalid header length, frame too large, invalid HELLO,
        /// or invalid maximum frame payload.
        /// </returns>
        private static bool IsFatalParseError(VatpErrorCode error) =>
            error is VatpErrorCode.UnsupportedVersion
                or VatpErrorCode.InvalidHeaderLength
                or VatpErrorCode.FrameTooLarge
                or VatpErrorCode.InvalidHello
                or VatpErrorCode.InvalidMaxFramePayload;

        /// <summary>Queues a connection-scoped FAIL carrying <paramref name="parsed"/>'s error.</summary>
        /// <param name="parsed">Invalid parse result. Both fatal and non-fatal errors are queued the same way.</param>
        /// <param name="cancellationToken">Cancellation observed while queueing the FAIL frame.</param>
        /// <returns>A task that completes when the FAIL frame is queued.</returns>
        /// <remarks>The read loop, not this method, decides whether the connection stops reading.</remarks>
        private async Task HandleInvalidFrameAsync(VatpFrameParseResult parsed, CancellationToken cancellationToken)
        {
            if (IsFatalParseError(parsed.Error))
            {
                await QueueFailAsync(VatpProtocol.ConnectionStreamId, parsed.Error, cancellationToken).ConfigureAwait(false);
                return;
            }

            await QueueFailAsync(VatpProtocol.ConnectionStreamId, parsed.Error, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Dispatches one parsed frame.</summary>
        /// <param name="frame">Frame already accepted by the parser.</param>
        /// <param name="cancellationToken">Cancellation observed by handlers that queue a response.</param>
        /// <returns>
        /// <see langword="false"/> when the read loop must stop. HELLO, OPEN, WINDOW, and CANCEL use their own rules.
        /// Every other type queues <see cref="VatpErrorCode.InvalidFrameType"/> and returns <see langword="true"/>.
        /// </returns>
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

        /// <summary>Accepts the first connection-scoped client HELLO and queues one server HELLO.</summary>
        /// <param name="frame">HELLO frame.</param>
        /// <param name="cancellationToken">Cancellation observed while queueing FAIL or the server HELLO.</param>
        /// <returns>
        /// <see langword="false"/> when the stream id is not the connection stream, HELLO is repeated, or the payload
        /// does not decode. <see langword="true"/> after the negotiated maximum is stored and the server HELLO is queued.
        /// </returns>
        /// <remarks>
        /// The stored maximum is the smaller of the client offer and <see cref="ArticleTransferLimits.DefaultMaxFramePayload"/>.
        /// It does not increase above <see cref="VatpProtocol.DefaultMaxFramePayload"/>.
        /// </remarks>
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

        /// <summary>Opens one send stream for a retained article, or queues FAIL and keeps the connection.</summary>
        /// <param name="frame">OPEN frame.</param>
        /// <param name="cancellationToken">Cancellation observed while queueing FAIL or META.</param>
        /// <returns>
        /// <see langword="false"/> only when OPEN arrives before a completed client HELLO.
        /// Decode failure, a full or duplicate stream table, and retention rejection return <see langword="true"/> after FAIL.
        /// </returns>
        /// <remarks>
        /// <see cref="IArticleRetentionAuthority.TryOpenTransfer"/> is called with <see cref="TryReserveFoundBytes"/>.
        /// A false reservation does not consume the request id; that rule belongs to the retention authority.
        /// On success the stream is inserted, META is queued, and <see cref="SignalDataSchedule"/> runs.
        /// The read loop is single-threaded, so the stream id checked before OPEN cannot be taken by another OPEN on this connection.
        /// </remarks>
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

            // Reservation is attempted inside TryOpenTransfer under the retention gate so a temporary
            // MaxQueuedFoundPayloadBytes failure does not consume the Success RequestId.
            var openResult = _retention.TryOpenTransfer(open.RequestId, open.ArticleId, TryReserveFoundBytes);
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
                // Stream-table capacity was checked before OPEN commit; a concurrent OPEN on the same
                // connection cannot steal this streamId without racing the single-threaded read loop.
                _streams[streamId] = stream;
            }

            VatpListenerLogMessages.OpenAccepted(_logger, streamId, open.RequestId);
            await QueueFrameAsync(VatpFrameEncoder.EncodeMeta(streamId, metaBytes), cancellationToken).ConfigureAwait(false);
            SignalDataSchedule();
            return true;
        }

        /// <summary>Applies a WINDOW credit add to a live sending stream, or queues FAIL for a malformed or unknown stream.</summary>
        /// <param name="frame">WINDOW frame.</param>
        /// <param name="cancellationToken">Cancellation observed while queueing FAIL.</param>
        /// <returns>A task that completes after a FAIL is queued, or immediately when the add is zero or was applied.</returns>
        /// <remarks>
        /// The payload must be <see cref="VatpProtocol.WindowPayloadLength"/> and must decode.
        /// A zero credit add is ignored. A missing or terminal stream gets <see cref="VatpErrorCode.UnknownStream"/>.
        /// A positive add calls <see cref="ArticleTransferWindow.Add"/> on the stream window property and, when the
        /// stream is still sending and has bytes left, enqueues it for DATA.
        /// </remarks>
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

        /// <summary>Removes a known stream for peer CANCEL, or queues FAIL when the stream is unknown.</summary>
        /// <param name="frame">CANCEL frame.</param>
        /// <param name="cancellationToken">Cancellation observed while queueing FAIL for an unknown stream. A known stream does not queue a response.</param>
        /// <returns>A completed task after the known stream is removed, or the FAIL queue task for an unknown stream.</returns>
        /// <remarks>Known CANCEL sets <see cref="SendStreamPhase.Cancelled"/>, releases the reservation, and disposes the lease. No END or FAIL is queued.</remarks>
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

        /// <summary>Writes queued frames, then DATA and END for ready streams, including a final drain after the channel completes.</summary>
        /// <param name="cancellationToken">Session token passed to each transport write.</param>
        /// <returns>A task that completes when the outbound channel is complete and no further DATA frame is taken.</returns>
        /// <remarks>
        /// <see cref="OutboundItemKind.ScheduleData"/> items are wakeups and are not written.
        /// Session-token cancellation calls <see cref="RequestSessionTermination"/> and is rethrown.
        /// Any other exception is logged with <see cref="VatpListenerLogMessages.WriterTransportFailed"/>, requests termination, and is rethrown.
        /// </remarks>
        private async Task RunWriterAsync(CancellationToken cancellationToken)
        {
            try
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RequestSessionTermination();
                throw;
            }
            catch (Exception ex)
            {
                VatpListenerLogMessages.WriterTransportFailed(_logger, ex);
                RequestSessionTermination();
                throw;
            }
        }

        /// <summary>After a META frame is written, marks that stream as sending and wakes DATA if credit and bytes remain.</summary>
        /// <param name="frame">Frame just written. Non-META frames are ignored.</param>
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

        /// <summary>Takes at most one DATA frame from the ready ring.</summary>
        /// <param name="encoded">Encoded DATA frame when this method returns <see langword="true"/>.</param>
        /// <param name="completedStreamId">
        /// Stream whose article size was reached by this chunk, so the writer sends END after DATA.
        /// Null when the stream still has bytes left or this method returns <see langword="false"/>.
        /// </param>
        /// <returns><see langword="false"/> when no eligible stream produced a chunk.</returns>
        /// <remarks>
        /// The scan holds <see cref="_streamGate"/> and examines at most the ring count from the start of the call.
        /// Streams with no credit are left off the ring. A sending stream already at <see cref="SendStream.ArtSize"/>
        /// is completed without an END id. Payload length uses <see cref="ArticleTransferReadyRing.ComputeDataPayloadLength"/>
        /// with the stream window credit and <see cref="_maxFramePayload"/>. FIN is set when the chunk finishes the article.
        /// Completion releases the stream before the caller writes DATA and END.
        /// </remarks>
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

        /// <summary>Completes a fully sent stream. Caller holds <see cref="_streamGate"/>.</summary>
        /// <param name="stream">Stream whose bytes have been handed to the encoder.</param>
        /// <remarks>
        /// Removes the stream from the ring and table, logs completion, releases <see cref="SendStream.ReservedBytes"/>,
        /// and disposes <see cref="SendStream.Lease"/>. This runs before the writer writes the finishing DATA frame and END.
        /// </remarks>
        private void CompleteStreamLocked(SendStream stream)
        {
            _readyRing.Remove(stream.StreamId);
            stream.Phase = SendStreamPhase.Completed;
            VatpListenerLogMessages.TransferCompleted(_logger, stream.StreamId);
            ReleaseFoundBytes(stream.ReservedBytes);
            stream.Lease.Dispose();
            _streams.Remove(stream.StreamId);
        }

        /// <summary>Removes <paramref name="stream"/> under <see cref="_streamGate"/>.</summary>
        /// <param name="stream">Stream to remove.</param>
        /// <param name="cancelled">
        /// <see langword="true"/> for peer CANCEL (<see cref="SendStreamPhase.Cancelled"/>);
        /// <see langword="false"/> for FAIL (<see cref="SendStreamPhase.Failed"/>).
        /// </param>
        private void RemoveStream(SendStream stream, bool cancelled)
        {
            lock (_streamGate)
            {
                RemoveStreamLocked(stream, cancelled);
            }
        }

        /// <summary>Removes a stream that is still in the table. Caller holds <see cref="_streamGate"/>.</summary>
        /// <param name="stream">Stream to remove.</param>
        /// <param name="cancelled">Selects <see cref="SendStreamPhase.Cancelled"/> or <see cref="SendStreamPhase.Failed"/>.</param>
        /// <remarks>
        /// Returns without releasing bytes when the stream id is already absent, so completion and disposal do not double-release.
        /// Otherwise the ring entry, reservation, and lease are released.
        /// </remarks>
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

        /// <summary>Releases every stream still in the table. Does not log transfer completion, cancel, or failure.</summary>
        /// <remarks>
        /// The table and ready ring are cleared under <see cref="_streamGate"/> before leases are disposed outside the lock.
        /// </remarks>
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

        /// <summary>Queues one encoded frame on the outbound channel.</summary>
        /// <param name="frame">Frame the writer will write in order with other queued frames.</param>
        /// <param name="cancellationToken">Cancels the wait when the bounded channel is full.</param>
        /// <returns>A task that completes when the frame is accepted by the channel.</returns>
        /// <remarks>
        /// The channel waits when full. Completion or cancellation of the session token surfaces from the channel write.
        /// </remarks>
        private async Task QueueFrameAsync(VatpFrameEncoder.EncodedFrame frame, CancellationToken cancellationToken) =>
            await _outbound.Writer.WriteAsync(new OutboundItem(OutboundItemKind.Frame, frame), cancellationToken).ConfigureAwait(false);

        /// <summary>Logs and drops a non-connection stream, then queues a FAIL frame.</summary>
        /// <param name="streamId">FAIL stream id. Zero is connection-scoped and does not log <see cref="VatpListenerLogMessages.TransferFailed"/>.</param>
        /// <param name="error">VATP error code written on the FAIL frame and, for a non-zero stream, logged as <c>ushort</c>.</param>
        /// <param name="cancellationToken">Cancellation observed while queueing the FAIL frame.</param>
        /// <returns>A task that completes when the FAIL frame is queued.</returns>
        /// <remarks>A non-zero id that is not in the table is still logged and still produces FAIL.</remarks>
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

        /// <summary>Best-effort wakeup so the writer polls the ready ring. A full or completed channel does not throw.</summary>
        /// <remarks>Stream ids are placed on the ring before this wakeup. The writer also polls the ring after each queued item.</remarks>
        private void SignalDataSchedule() =>
            _ = _outbound.Writer.TryWrite(new OutboundItem(OutboundItemKind.ScheduleData, null));

        /// <summary>Writes the frame header and, when present, the payload.</summary>
        /// <param name="frame">Header plus optional payload. Payload memory is written as supplied.</param>
        /// <param name="cancellationToken">Cancellation passed to each transport write.</param>
        /// <returns>A task that completes when both parts have been accepted.</returns>
        private async Task WriteEncodedFrameAsync(VatpFrameEncoder.EncodedFrame frame, CancellationToken cancellationToken)
        {
            await WriteFullyAsync(frame.Header, cancellationToken).ConfigureAwait(false);
            if (!frame.Payload.IsEmpty)
            {
                await WriteFullyAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Writes <paramref name="payload"/> in slices of at most <see cref="MaxWriteProgressChunkBytes"/>.</summary>
        /// <param name="payload">Bytes to write. Empty input completes without a transport call.</param>
        /// <param name="cancellationToken">Cancellation passed to each <see cref="ICacheListenerTransport.WriteAsync"/>.</param>
        /// <returns>A task that completes when every byte has been accepted.</returns>
        /// <exception cref="IOException">Thrown when the transport accepts zero or fewer bytes.</exception>
        /// <remarks>A short positive accept advances the cursor and continues. Each slice is one no-progress timeout interval.</remarks>
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

        /// <summary>Reserves <paramref name="payloadBytes"/> against <see cref="_maxQueuedFoundPayloadBytes"/>.</summary>
        /// <param name="payloadBytes">Article size offered to the retention OPEN callback.</param>
        /// <returns>
        /// <see langword="false"/> when <see cref="_reservedFoundPayloadBytes"/> is already greater than the cap minus
        /// <paramref name="payloadBytes"/>. Otherwise <see langword="true"/> after the compare-and-swap adds the size.
        /// </returns>
        /// <remarks>
        /// Passed to <see cref="IArticleRetentionAuthority.TryOpenTransfer"/>. The retention authority treats
        /// <see langword="false"/> as OPEN rejection and does not consume the request id.
        /// </remarks>
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

        /// <summary>Subtracts a reservation. The counter is not prevented from going negative.</summary>
        /// <param name="payloadBytes">Bytes previously reserved for one stream, normally <see cref="SendStream.ReservedBytes"/>.</param>
        private void ReleaseFoundBytes(int payloadBytes) =>
            Interlocked.Add(ref _reservedFoundPayloadBytes, -payloadBytes);

        /// <summary>Discriminator for <see cref="OutboundItem"/>.</summary>
        private enum OutboundItemKind
        {
            /// <summary>An encoded frame the writer must write.</summary>
            Frame,
            /// <summary>A wakeup. The writer skips the item and then polls the ready ring.</summary>
            ScheduleData,
        }

        /// <summary>One outbound channel item.</summary>
        /// <param name="Kind">Frame or data-schedule wakeup.</param>
        /// <param name="Frame">Encoded frame when <paramref name="Kind"/> is <see cref="OutboundItemKind.Frame"/>; otherwise null.</param>
        private readonly record struct OutboundItem(OutboundItemKind Kind, VatpFrameEncoder.EncodedFrame? Frame);

        /// <summary>Send progress for one OPEN stream.</summary>
        private enum SendStreamPhase
        {
            /// <summary>Inserted after OPEN. DATA is not taken until META has been written.</summary>
            PendingMeta,
            /// <summary>META has been written. The stream may be placed on the ready ring.</summary>
            SendingData,
            /// <summary>All article bytes were handed to the DATA encoder and the stream was removed.</summary>
            Completed,
            /// <summary>Removed because the peer sent CANCEL.</summary>
            Cancelled,
            /// <summary>Removed because a stream-scoped FAIL was queued.</summary>
            Failed,
        }

        /// <summary>One accepted OPEN: lease, article bytes, window, and send cursor.</summary>
        /// <remarks>
        /// <see cref="ArtData"/> is the retained article memory sliced into DATA frames. The encoder does not copy that payload.
        /// <see cref="MetaPayload"/> stores the encoded META bytes supplied at construction; the send path queues those bytes
        /// from the OPEN handler and does not read this property again.
        /// </remarks>
        private sealed class SendStream
        {
            /// <summary>Captures one opened transfer.</summary>
            /// <param name="streamId">Non-zero VATP stream id from OPEN.</param>
            /// <param name="lease">Retention lease. Disposed when the stream is completed, removed, or the session drops it.</param>
            /// <param name="metaPayload">Encoded META payload queued separately by the OPEN handler.</param>
            /// <param name="artData">Article bytes sliced into DATA frames. Not copied here.</param>
            /// <param name="artSize">Article size in bytes. Also the reservation size.</param>
            /// <param name="sendWindow">Initial window. Stored in <see cref="SendWindow"/>.</param>
            /// <param name="reservedBytes">Found-byte reservation to release exactly once. OPEN passes <paramref name="artSize"/>.</param>
            internal SendStream(
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

            /// <summary>VATP stream id for this OPEN.</summary>
            internal uint StreamId { get; }

            /// <summary>Retention lease for the article bytes. Dispose releases the reader lease and does not delete the message-id entry.</summary>
            internal VatpTransferLease Lease { get; }

            /// <summary>Encoded META bytes from construction. Not read by the writer.</summary>
            internal byte[] MetaPayload { get; }

            /// <summary>Article bytes. DATA frames slice this memory; they do not copy it.</summary>
            internal ReadOnlyMemory<byte> ArtData { get; }

            /// <summary>Total article size. The send cursor stops when <see cref="SentBytes"/> reaches this value.</summary>
            internal int ArtSize { get; }

            /// <summary>Bytes already handed to the DATA encoder. Advanced under <see cref="VatpListenerSession._streamGate"/>.</summary>
            internal int SentBytes { get; set; }

            /// <summary>Window supplied at construction.</summary>
            /// <remarks>
            /// <see cref="ArticleTransferWindow"/> is a struct and this property has no setter.
            /// <see cref="ArticleTransferWindow.Add"/> and <see cref="ArticleTransferWindow.TryConsume"/> invoked on the
            /// property value therefore mutate a copy. <see cref="ArticleTransferWindow.Credit"/> stays at the constructed value.
            /// </remarks>
            internal ArticleTransferWindow SendWindow { get; }

            /// <summary>Found-byte reservation released once when this stream leaves the table.</summary>
            internal int ReservedBytes { get; }

            /// <summary>Send phase. Starts at <see cref="SendStreamPhase.PendingMeta"/>.</summary>
            internal SendStreamPhase Phase { get; set; } = SendStreamPhase.PendingMeta;

            /// <summary>True when <see cref="Phase"/> is completed, cancelled, or failed.</summary>
            internal bool IsTerminal =>
                Phase is SendStreamPhase.Completed or SendStreamPhase.Cancelled or SendStreamPhase.Failed;
        }
    }
}
