using System.Buffers;
using System.Diagnostics;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Transport.ArticleTransfer;
using VectorNNTP.StorageServer.Ingress;

namespace VectorNNTP.StorageServer.Listener;

public sealed partial class StorageVatpSession
{
    private async Task<bool> HandleStoreAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
    {
        var streamId = frame.Header.StreamId;
        if (!_clientHelloComplete)
        {
            await WriteFailAsync(streamId, VatpErrorCode.InvalidStateTransition, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        if (!VatpStorePayload.TryDecode(frame.Payload, out var articleId, out var decodeError))
        {
            await WriteFailAsync(streamId, decodeError, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (streamId == VatpProtocol.ConnectionStreamId
            || _streams.ContainsKey(streamId)
            || _storeStreams.ContainsKey(streamId)
            || _streams.Count + _storeStreams.Count >= _limits.MaxStreamsPerConnection)
        {
            await WriteFailAsync(
                    streamId,
                    streamId == VatpProtocol.ConnectionStreamId
                        ? VatpErrorCode.InvalidStreamId
                        : VatpErrorCode.StreamTableError,
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        var admission = _storeAdmission;
        var held = false;
        if (admission is not null)
        {
            if (!admission.TryEnter())
            {
                await WriteFailAsync(streamId, VatpErrorCode.StreamTableError, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            held = true;
        }

        var receive = new ArticleTransferReceiveStream(
            streamId,
            StoreReceiveCorrelation,
            articleId,
            _limits);
        if (!_storeStreams.TryAdd(streamId, new StoreStream(receive, held)))
        {
            if (held)
            {
                _storeAdmission?.Exit();
            }

            await WriteFailAsync(streamId, VatpErrorCode.StreamTableError, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        return true;
    }

    private async Task<bool> HandleStoreMetaAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
    {
        var streamId = frame.Header.StreamId;
        if (!_storeStreams.TryGetValue(streamId, out var store))
        {
            await WriteFailAsync(streamId, VatpErrorCode.InvalidFrameType, cancellationToken).ConfigureAwait(false);
            return false;
        }

        Span<byte> metaBytes = stackalloc byte[VatpProtocol.MetaPayloadLength];
        if (frame.Payload.Length != VatpProtocol.MetaPayloadLength)
        {
            RemoveStore(streamId);
            await WriteFailAsync(streamId, VatpErrorCode.InvalidMeta, cancellationToken).ConfigureAwait(false);
            return true;
        }

        frame.Payload.CopyTo(metaBytes);
        if (!VatpMetaCodec.TryDecode(metaBytes, out var meta, out var decodeError))
        {
            RemoveStore(streamId);
            await WriteFailAsync(streamId, decodeError, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var apply = store.Receive.TryAcceptMeta(frame.Payload);
        if (!apply.Success)
        {
            RemoveStore(streamId);
            await WriteFailAsync(streamId, store.Receive.Failure, cancellationToken).ConfigureAwait(false);
            return true;
        }

        store.MetaArtHash = meta.ArtHash;
        store.MetaArtSize = meta.ArtSize;
        store.MetaSeen = true;
        return true;
    }

    private async Task<bool> HandleStoreDataAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
    {
        var streamId = frame.Header.StreamId;
        if (!_storeStreams.TryGetValue(streamId, out var store))
        {
            await WriteFailAsync(streamId, VatpErrorCode.InvalidFrameType, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var dataStarted = Stopwatch.GetTimestamp();
        var apply = store.Receive.TryAcceptData(frame.Payload, frame.Header.HasFin);
        Interlocked.Add(ref _dataAcceptTicks, Stopwatch.GetTimestamp() - dataStarted);
        Interlocked.Increment(ref _dataAcceptCount);
        if (apply.Success)
        {
            Interlocked.Add(ref _dataCopyBytes, frame.Payload.Length);
        }
        if (!apply.Success)
        {
            var error = store.Receive.Failure;
            RemoveStore(streamId);
            await WriteFailAsync(streamId, error, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var payloadLength = checked((uint)frame.Payload.Length);
        if (payloadLength > 0 && store.Receive.TryAcceptWindow(payloadLength).Success)
        {
            var windowStarted = Stopwatch.GetTimestamp();
            await WriteEncodedAsync(VatpFrameEncoder.EncodeWindow(streamId, payloadLength), cancellationToken)
                .ConfigureAwait(false);
            Interlocked.Add(ref _windowWriteTicks, Stopwatch.GetTimestamp() - windowStarted);
            Interlocked.Increment(ref _windowWriteCount);
        }

        if (store.Receive.IsTerminal)
        {
            var error = store.Receive.Failure == VatpErrorCode.None
                ? VatpErrorCode.IncompleteTransfer
                : store.Receive.Failure;
            RemoveStore(streamId);
            await WriteFailAsync(streamId, error, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<bool> HandleStoreEndAsync(VatpParsedFrame frame, CancellationToken cancellationToken)
    {
        var streamId = frame.Header.StreamId;
        if (!_storeStreams.TryGetValue(streamId, out var store))
        {
            await WriteFailAsync(streamId, VatpErrorCode.InvalidFrameType, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var endStarted = Stopwatch.GetTimestamp();
        var apply = store.Receive.TryAcceptEnd();
        var tookRecord = false;
        ArticleRecord record = default;
        if (apply.Success)
        {
            tookRecord = store.Receive.TryTakeRecord(out record);
        }

        Interlocked.Add(ref _endValidateTicks, Stopwatch.GetTimestamp() - endStarted);
        Interlocked.Increment(ref _endValidateCount);
        if (!tookRecord)
        {
            var error = store.Receive.Failure == VatpErrorCode.None
                ? VatpErrorCode.IncompleteTransfer
                : store.Receive.Failure;
            RemoveStore(streamId);
            await WriteFailAsync(streamId, error, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var mismatch = Mismatch(store, record);
        if (mismatch != VatpErrorCode.None)
        {
            RemoveStore(streamId);
            await WriteFailAsync(streamId, mismatch, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (_acceptedArticles is null)
        {
            RemoveStore(streamId);
            await WriteFailAsync(streamId, VatpErrorCode.OpenRejected, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var queued = new AcceptedArticle(record.ArtId, record.ArtHash, record.ArtSize, record.ArtData);
        if (!_acceptedArticles.TryEnqueue(queued))
        {
            RemoveStore(streamId);
            await WriteFailAsync(streamId, VatpErrorCode.IncompleteTransfer, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var resultStarted = Stopwatch.GetTimestamp();
        store.AcceptStarted = true;
        RemoveStore(streamId);
        StorageVatpSessionLogMessages.StoreCompleted(
            _logger,
            record.ArtId.ToLowerHexString(),
            "Accepted");
        await WriteEncodedAsync(
                VatpFrameEncoder.EncodeResult(streamId, VatpStoreOutcome.Accepted),
                cancellationToken)
            .ConfigureAwait(false);
        Interlocked.Add(ref _enqueueToResultTicks, Stopwatch.GetTimestamp() - resultStarted);
        Interlocked.Increment(ref _enqueueToResultCount);
        return true;
    }

    private static VatpErrorCode Mismatch(StoreStream store, in ArticleRecord record)
    {
        if (!store.MetaSeen)
        {
            return VatpErrorCode.IncompleteTransfer;
        }

        if (!record.ArtId.Equals(store.Receive.ExpectedArtId))
        {
            return VatpErrorCode.ArticleIdMismatch;
        }

        if (record.ArtHash != store.MetaArtHash)
        {
            return VatpErrorCode.ArtHashMismatch;
        }

        if (record.ArtSize != store.MetaArtSize)
        {
            return VatpErrorCode.ArtSizeMismatch;
        }

        return VatpErrorCode.None;
    }

    private void RemoveStore(uint streamId)
    {
        if (_storeStreams.TryRemove(streamId, out var store) && store.SlotHeld)
        {
            store.SlotHeld = false;
            _storeAdmission?.Exit();
        }
    }

    private void ReleaseStoreSlots()
    {
        if (_storeStreams.Count == 0)
        {
            return;
        }

        var ids = new uint[_storeStreams.Count];
        _storeStreams.Keys.CopyTo(ids, 0);
        foreach (var streamId in ids)
        {
            RemoveStore(streamId);
        }
    }

    private sealed class StoreStream
    {
        public StoreStream(ArticleTransferReceiveStream receive, bool slotHeld)
        {
            Receive = receive;
            SlotHeld = slotHeld;
        }

        public ArticleTransferReceiveStream Receive { get; }

        public bool SlotHeld { get; set; }

        public bool MetaSeen { get; set; }

        public ulong MetaArtHash { get; set; }

        public int MetaArtSize { get; set; }

        public bool AcceptStarted { get; set; }
    }
}
