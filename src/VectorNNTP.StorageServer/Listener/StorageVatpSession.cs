using System.Buffers;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Transport.ArticleTransfer;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;

namespace VectorNNTP.StorageServer.Listener;

/// <summary>
/// VATP server session: client HELLO, then OPEN served from
/// <see cref="IStorageArticleOpenBoundary"/> as META, credit-paced DATA/FIN, and END.
/// STORE is a separate receive stream. After META matches the inbound bytes, Path is
/// rematerialized with <see cref="StorageServerOptions.Fqdn"/> and that record is passed to
/// <see cref="IArticleStorageEngine.AcceptAsync"/>. Retrieval does not rewrite Path.
/// </summary>
public sealed partial class StorageVatpSession : IAsyncDisposable
{
    /// <summary>Local receive-stream correlation. Not a wire field and not a storage key.</summary>
    private static readonly Guid StoreReceiveCorrelation = new("5f5f5f5f-5f5f-5f5f-5f5f-5f5f5f5f5f5f");

    private readonly IStorageVatpTransport _transport;
    private readonly IStorageArticleOpenBoundary _openBoundary;
    private readonly StorageServerListenerRuntimeOptions _listener;
    private readonly ArticleTransferLimits _limits;
    private readonly ILogger _logger;
    private readonly IArticleStorageEngine? _placementEngine;
    private readonly StoreAssemblyAdmission? _storeAdmission;
    private readonly NntpArticleParser? _storePathParser;
    private readonly Dictionary<uint, SendStream> _streams = [];
    private readonly Dictionary<uint, StoreStream> _storeStreams = [];
    private readonly ArticleTransferReadyRing _readyRing = new();
    private uint _maxFramePayload = VatpProtocol.DefaultMaxFramePayload;
    private bool _clientHelloComplete;
    private bool _serverHelloSent;
    private int _disposed;

    /// <summary>Initializes a VATP session over an established transport.</summary>
    public StorageVatpSession(
        IStorageVatpTransport transport,
        IStorageArticleOpenBoundary openBoundary,
        StorageServerListenerRuntimeOptions listener,
        ILogger logger,
        ArticleTransferLimits? limits = null,
        IArticleStorageEngine? placementEngine = null,
        StoreAssemblyAdmission? storeAdmission = null,
        string? storePathIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(openBoundary);
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(logger);
        _transport = transport;
        _openBoundary = openBoundary;
        _listener = listener;
        _limits = limits ?? ArticleTransferLimits.Default;
        _logger = logger;
        _placementEngine = placementEngine;
        _storeAdmission = storeAdmission;
        _storePathParser = string.IsNullOrWhiteSpace(storePathIdentity)
            ? null
            : new NntpArticleParser(storePathIdentity.Trim());
    }

    /// <summary>Runs until peer close, cancellation, or fatal protocol error.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var readBuffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        var parseBuffer = ArrayPool<byte>.Shared.Rent(
            VatpProtocol.HeaderLengthBytes + (int)VatpProtocol.DefaultMaxFramePayload);
        var buffered = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = await _transport
                        .ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (TimeoutException)
                {
                    return;
                }

                if (bytesRead == 0)
                {
                    return;
                }

                if (buffered + bytesRead > parseBuffer.Length
                    || buffered + bytesRead > _listener.ParserAccumulationMaxBytes)
                {
                    await WriteFailAsync(
                            VatpProtocol.ConnectionStreamId,
                            VatpErrorCode.FrameTooLarge,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                Buffer.BlockCopy(readBuffer, 0, parseBuffer, buffered, bytesRead);
                buffered += bytesRead;

                var consumedTotal = 0;
                while (true)
                {
                    var remaining = new ReadOnlySequence<byte>(parseBuffer, consumedTotal, buffered - consumedTotal);
                    var parsed = VatpFrameParser.ParseOneFrame(remaining, _maxFramePayload);
                    if (parsed.Status == VatpFrameParseStatus.Incomplete)
                    {
                        break;
                    }

                    if (parsed.Status == VatpFrameParseStatus.Invalid)
                    {
                        await WriteFailAsync(
                                VatpProtocol.ConnectionStreamId,
                                parsed.Error,
                                cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    consumedTotal += checked((int)parsed.ConsumedBytes);
                    if (parsed.Frame is not { } frame)
                    {
                        await WriteFailAsync(
                                VatpProtocol.ConnectionStreamId,
                                VatpErrorCode.InvalidFrameType,
                                cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    var continueSession = await HandleFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                    if (!continueSession)
                    {
                        return;
                    }
                }

                if (consumedTotal > 0)
                {
                    var remainingBytes = buffered - consumedTotal;
                    if (remainingBytes > 0)
                    {
                        Buffer.BlockCopy(parseBuffer, consumedTotal, parseBuffer, 0, remainingBytes);
                    }

                    buffered = remainingBytes;
                }
            }
        }
        finally
        {
            ReleaseStoreSlots();
            _streams.Clear();
            _storeStreams.Clear();
            _readyRing.Clear();
            ArrayPool<byte>.Shared.Return(readBuffer);
            ArrayPool<byte>.Shared.Return(parseBuffer);
            StorageVatpSessionLogMessages.ConnectionClosed(_logger);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<bool> HandleFrameAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Header.Type)
        {
            case VatpFrameType.Hello:
                return await HandleHelloAsync(frame, cancellationToken).ConfigureAwait(false);
            case VatpFrameType.Open:
                return await HandleOpenAsync(frame, cancellationToken).ConfigureAwait(false);
            case VatpFrameType.Store:
                return await HandleStoreAsync(frame, cancellationToken).ConfigureAwait(false);
            case VatpFrameType.Meta:
                return await HandleStoreMetaAsync(frame, cancellationToken).ConfigureAwait(false);
            case VatpFrameType.Data:
                return await HandleStoreDataAsync(frame, cancellationToken).ConfigureAwait(false);
            case VatpFrameType.End:
                return await HandleStoreEndAsync(frame, cancellationToken).ConfigureAwait(false);
            case VatpFrameType.Window:
                return await HandleWindowAsync(frame, cancellationToken).ConfigureAwait(false);
            case VatpFrameType.Cancel:
                return await HandleCancelAsync(frame, cancellationToken).ConfigureAwait(false);
            default:
                await WriteFailAsync(
                        frame.Header.StreamId == VatpProtocol.ConnectionStreamId
                            ? VatpProtocol.ConnectionStreamId
                            : frame.Header.StreamId,
                        VatpErrorCode.InvalidFrameType,
                        cancellationToken)
                    .ConfigureAwait(false);
                return false;
        }
    }

    private async Task<bool> HandleHelloAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
    {
        if (frame.Header.StreamId != VatpProtocol.ConnectionStreamId)
        {
            await WriteFailAsync(VatpProtocol.ConnectionStreamId, VatpErrorCode.InvalidStreamId, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        if (_clientHelloComplete)
        {
            await WriteFailAsync(VatpProtocol.ConnectionStreamId, VatpErrorCode.InvalidHello, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        if (!VatpHello.TryDecode(frame.Payload, out var hello, out var error))
        {
            await WriteFailAsync(VatpProtocol.ConnectionStreamId, error, cancellationToken).ConfigureAwait(false);
            return false;
        }

        _maxFramePayload = Math.Min(hello.MaxFramePayload, VatpProtocol.DefaultMaxFramePayload);
        _clientHelloComplete = true;
        StorageVatpSessionLogMessages.ClientHelloAccepted(_logger, _maxFramePayload);

        if (!_serverHelloSent)
        {
            _serverHelloSent = true;
            var encoded = VatpFrameEncoder.EncodeHello(_maxFramePayload);
            await WriteEncodedAsync(encoded, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<bool> HandleOpenAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
    {
        var streamId = frame.Header.StreamId;
        if (!_clientHelloComplete)
        {
            await WriteFailAsync(streamId, VatpErrorCode.InvalidStateTransition, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (!VatpOpenPayload.TryDecode(frame.Payload, out var open, out var decodeError))
        {
            await WriteFailAsync(streamId, decodeError, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (streamId == VatpProtocol.ConnectionStreamId
            || _streams.ContainsKey(streamId)
            || _storeStreams.ContainsKey(streamId)
            || _streams.Count + _storeStreams.Count >= _limits.MaxStreamsPerConnection)
        {
            await WriteFailAsync(streamId, streamId == VatpProtocol.ConnectionStreamId
                    ? VatpErrorCode.InvalidStreamId
                    : VatpErrorCode.StreamTableError, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = _openBoundary.TryOpen(open.RequestId, open.ArticleId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Accepted || result.Record.ParseStatus != ArticleParseStatus.CanonicalV1)
        {
            StorageVatpSessionLogMessages.OpenRejected(_logger, streamId, open.RequestId);
            await WriteFailAsync(streamId, VatpErrorCode.OpenRejected, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var record = result.Record;
        var meta = ArticleCanonicalTransferMeta.FromRecord(in record, result.SelectedDateHeaderName);
        var metaBytes = VatpMetaCodec.Encode(in meta);
        var stream = new SendStream(
            streamId,
            record,
            new ArticleTransferWindow(_limits.InitialStreamWindowBytes, _limits.MaxStreamCreditBytes));
        _streams.Add(streamId, stream);
        StorageVatpSessionLogMessages.OpenAccepted(_logger, streamId, open.RequestId);
        await WriteEncodedAsync(VatpFrameEncoder.EncodeMeta(streamId, metaBytes), cancellationToken).ConfigureAwait(false);
        stream.Phase = SendPhase.SendingData;
        if (stream.SentBytes < record.ArtSize && stream.Window.HasCredit)
        {
            _readyRing.Enqueue(streamId);
        }

        await PumpDataAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> HandleWindowAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
    {
        var streamId = frame.Header.StreamId;
        if (frame.Payload.Length != VatpProtocol.WindowPayloadLength)
        {
            await WriteFailAsync(streamId, VatpErrorCode.InvalidFrameLength, cancellationToken).ConfigureAwait(false);
            return true;
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
            await WriteFailAsync(streamId, VatpErrorCode.InvalidFrameLength, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (addCredit == 0)
        {
            return true;
        }

        if (!_streams.TryGetValue(streamId, out var stream) || stream.Phase != SendPhase.SendingData)
        {
            await WriteFailAsync(streamId, VatpErrorCode.UnknownStream, cancellationToken).ConfigureAwait(false);
            return true;
        }

        _ = stream.Window.Add(addCredit);
        if (stream.SentBytes < stream.Record.ArtSize)
        {
            _readyRing.Enqueue(streamId);
        }

        await PumpDataAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> HandleCancelAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
    {
        var streamId = frame.Header.StreamId;
        if (_storeStreams.TryGetValue(streamId, out var store))
        {
            if (store.AcceptStarted)
            {
                return true;
            }

            _ = store.Receive.TryCancel();
            RemoveStore(streamId);
            StorageVatpSessionLogMessages.TransferCancelled(_logger, streamId);
            await WriteFailAsync(streamId, VatpErrorCode.Cancelled, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (!_streams.Remove(streamId, out var stream))
        {
            await WriteFailAsync(streamId, VatpErrorCode.UnknownStream, cancellationToken).ConfigureAwait(false);
            return true;
        }

        _readyRing.Remove(streamId);
        stream.Phase = SendPhase.Cancelled;
        StorageVatpSessionLogMessages.TransferCancelled(_logger, streamId);
        return true;
    }

    private async Task PumpDataAsync(CancellationToken cancellationToken)
    {
        while (TryTakeDataFrame(out var encoded, out var completedStreamId))
        {
            await WriteEncodedAsync(encoded, cancellationToken).ConfigureAwait(false);
            if (completedStreamId is { } streamId)
            {
                await WriteEncodedAsync(VatpFrameEncoder.EncodeEnd(streamId), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private bool TryTakeDataFrame(out VatpFrameEncoder.EncodedFrame encoded, out uint? completedStreamId)
    {
        encoded = default;
        completedStreamId = null;
        var attempts = _readyRing.Count;
        while (attempts-- > 0 && _readyRing.TryTakeNext(out var streamId))
        {
            if (!_streams.TryGetValue(streamId, out var stream) || stream.Phase != SendPhase.SendingData)
            {
                continue;
            }

            var remaining = stream.Record.ArtSize - stream.SentBytes;
            if (remaining <= 0)
            {
                CompleteStream(stream);
                continue;
            }

            if (!stream.Window.HasCredit)
            {
                continue;
            }

            var length = ArticleTransferReadyRing.ComputeDataPayloadLength(
                remaining,
                stream.Window.Credit,
                _maxFramePayload);
            if (length <= 0 || !stream.Window.TryConsume(length))
            {
                continue;
            }

            var fin = length == remaining;
            var payload = stream.Record.ArtData.Slice(stream.SentBytes, length);
            stream.SentBytes += length;
            encoded = VatpFrameEncoder.EncodeData(streamId, payload, fin);
            if (stream.SentBytes < stream.Record.ArtSize)
            {
                _readyRing.Enqueue(streamId);
            }
            else
            {
                CompleteStream(stream);
                completedStreamId = streamId;
            }

            return true;
        }

        return false;
    }

    private void CompleteStream(SendStream stream)
    {
        _readyRing.Remove(stream.StreamId);
        stream.Phase = SendPhase.Completed;
        _ = _streams.Remove(stream.StreamId);
        StorageVatpSessionLogMessages.TransferCompleted(_logger, stream.StreamId);
    }

    private async Task WriteFailAsync(uint streamId, VatpErrorCode error, CancellationToken cancellationToken)
    {
        var encoded = VatpFrameEncoder.EncodeFail(streamId, error);
        await WriteEncodedAsync(encoded, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteEncodedAsync(VatpFrameEncoder.EncodedFrame encoded, CancellationToken cancellationToken)
    {
        if (!encoded.Header.IsEmpty)
        {
            await WriteFullyAsync(encoded.Header, cancellationToken).ConfigureAwait(false);
        }

        if (!encoded.Payload.IsEmpty)
        {
            await WriteFullyAsync(encoded.Payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteFullyAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var written = 0;
        while (written < payload.Length)
        {
            var accepted = await _transport.WriteAsync(payload[written..], cancellationToken).ConfigureAwait(false);
            if (accepted <= 0)
            {
                throw new IOException("Transport returned zero accepted bytes.");
            }

            written += accepted;
        }
    }

    private enum SendPhase
    {
        SendingData,
        Completed,
        Cancelled,
    }

    private sealed class SendStream
    {
        public SendStream(uint streamId, ArticleRecord record, ArticleTransferWindow window)
        {
            StreamId = streamId;
            Record = record;
            Window = window;
        }

        public uint StreamId { get; }

        public ArticleRecord Record { get; }

        /// <summary>Mutable credit. Must stay a field so <see cref="ArticleTransferWindow.TryConsume"/> updates this stream.</summary>
        public ArticleTransferWindow Window;

        public int SentBytes { get; set; }

        public SendPhase Phase { get; set; }
    }
}
