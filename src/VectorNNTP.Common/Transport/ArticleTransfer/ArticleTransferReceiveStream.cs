using System.Buffers;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Processing;

namespace VectorNNTP.Common.Transport.ArticleTransfer;

/// <summary>
/// Result of applying one protocol event to a receive stream.
/// </summary>
public readonly record struct ArticleTransferApplyResult(bool Success, VatpErrorCode Error)
{
    /// <summary>Creates a success result.</summary>
    public static ArticleTransferApplyResult Ok() => new(true, VatpErrorCode.None);

    /// <summary>Creates a failure result.</summary>
    public static ArticleTransferApplyResult Fail(VatpErrorCode error) => new(false, error);
}

/// <summary>
/// Receiver state machine for one multiplexed VATP transfer.
/// </summary>
/// <remarks>
/// <para>
/// Completion contract: FIN marks the final DATA frame; END confirms transfer completion.
/// A stream becomes consumable only after exact <c>ArtSize</c> bytes, FIN, END, and
/// successful <see cref="ArticleRecordFactory.TryCreateFromCanonicalTransfer"/>.
/// Neither FIN nor END alone exposes an article.
/// </para>
/// <para>
/// Invalid sequences (DATA before META, duplicate META, DATA after FIN/END, duplicate END,
/// END before ArtSize+FIN, etc.) fail the stream deterministically. WINDOW for a terminal
/// stream is <see cref="VatpErrorCode.UnknownStream"/>.
/// </para>
/// </remarks>
public sealed class ArticleTransferReceiveStream
{
    private readonly ArticleTransferLimits _limits;
    private ArticleTransferWindow _receiveWindow;
    private ArticleCanonicalTransferMeta _meta;
    private byte[]? _artData;
    private int _received;
    private bool _finSeen;
    private ArticleRecord _record;
    private bool _hasRecord;

    /// <summary>Creates a stream after a successful OPEN.</summary>
    public ArticleTransferReceiveStream(
        uint streamId,
        Guid requestId,
        ArticleId expectedArtId,
        ArticleTransferLimits? limits = null)
    {
        if (streamId == VatpProtocol.ConnectionStreamId)
        {
            throw new ArgumentOutOfRangeException(nameof(streamId));
        }

        if (requestId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(requestId));
        }

        _limits = limits ?? ArticleTransferLimits.Default;
        StreamId = streamId;
        RequestId = requestId;
        ExpectedArtId = expectedArtId;
        Phase = ArticleTransferPhase.AwaitingMeta;
        _receiveWindow = new ArticleTransferWindow(
            _limits.InitialStreamWindowBytes,
            _limits.MaxStreamCreditBytes);
    }

    /// <summary>Gets the connection-local stream identifier.</summary>
    public uint StreamId { get; }

    /// <summary>Gets the OPEN RequestId.</summary>
    public Guid RequestId { get; }

    /// <summary>Gets the OPEN ArticleId used for canonical binding.</summary>
    public ArticleId ExpectedArtId { get; }

    /// <summary>Gets the current phase.</summary>
    public ArticleTransferPhase Phase { get; private set; }

    /// <summary>Gets the failure code when <see cref="Phase"/> is <see cref="ArticleTransferPhase.Failed"/>.</summary>
    public VatpErrorCode Failure { get; private set; }

    /// <summary>
    /// Gets the number of DATA payload bytes copied into this stream.
    /// </summary>
    /// <remarks>
    /// Releasing the article buffer on failure or cancellation does not erase this count.
    /// A failed or cancelled stream is terminal and is not reused for another article.
    /// </remarks>
    public int ReceivedBytes => _received;

    /// <summary>Gets META ArtSize after META is accepted; otherwise 0.</summary>
    public int ArtSize => Phase >= ArticleTransferPhase.ReceivingData && Phase != ArticleTransferPhase.Failed
        && Phase != ArticleTransferPhase.Cancelled
        ? _meta.ArtSize
        : 0;

    /// <summary>Gets a value indicating whether a consumable CanonicalV1 record is available.</summary>
    public bool HasConsumableRecord => Phase == ArticleTransferPhase.Completed && _hasRecord;

    /// <summary>Gets the validated record. Throws when not consumable.</summary>
    public ArticleRecord ConsumableRecord
    {
        get
        {
            if (!HasConsumableRecord)
            {
                throw new InvalidOperationException("Transfer has no consumable ArticleRecord.");
            }

            return _record;
        }
    }

    /// <summary>Gets remaining receive WINDOW credit.</summary>
    public long ReceiveCredit => _receiveWindow.Credit;

    /// <summary>Gets a value indicating whether the phase is terminal.</summary>
    public bool IsTerminal =>
        Phase is ArticleTransferPhase.Completed
            or ArticleTransferPhase.Failed
            or ArticleTransferPhase.Cancelled;

    /// <summary>Applies a decoded META payload.</summary>
    public ArticleTransferApplyResult TryAcceptMeta(ReadOnlySpan<byte> metaPayload)
    {
        if (Phase != ArticleTransferPhase.AwaitingMeta)
        {
            return FailStream(VatpErrorCode.InvalidStateTransition);
        }

        if (!VatpMetaCodec.TryDecode(metaPayload, out var meta, out var decodeError))
        {
            return FailStream(decodeError);
        }

        if (meta.ArtSize < 1 || meta.ArtSize > ArticleResourceLimits.MaxArticleBytes)
        {
            return FailStream(VatpErrorCode.ArticleTooLarge);
        }

        _meta = meta;
        _artData = new byte[meta.ArtSize];
        _received = 0;
        _finSeen = false;
        Phase = ArticleTransferPhase.ReceivingData;
        return ArticleTransferApplyResult.Ok();
    }

    /// <summary>Applies a META payload sequence.</summary>
    public ArticleTransferApplyResult TryAcceptMeta(in ReadOnlySequence<byte> metaPayload)
    {
        if (metaPayload.Length != VatpProtocol.MetaPayloadLength)
        {
            return FailStream(VatpErrorCode.InvalidMeta);
        }

        Span<byte> buffer = stackalloc byte[VatpProtocol.MetaPayloadLength];
        metaPayload.CopyTo(buffer);
        return TryAcceptMeta(buffer);
    }

    /// <summary>Applies a DATA chunk. Does not expose a record.</summary>
    public ArticleTransferApplyResult TryAcceptData(ReadOnlySpan<byte> payload, bool fin)
    {
        if (Phase != ArticleTransferPhase.ReceivingData)
        {
            return FailStream(VatpErrorCode.InvalidStateTransition);
        }

        if (_artData is null)
        {
            return FailStream(VatpErrorCode.InvalidStateTransition);
        }

        if (payload.Length == 0 && !fin)
        {
            return FailStream(VatpErrorCode.InvalidFrameLength);
        }

        if (!_receiveWindow.TryConsume(payload.Length))
        {
            return FailStream(VatpErrorCode.FlowControlViolation);
        }

        if ((long)_received + payload.Length > _meta.ArtSize)
        {
            return FailStream(VatpErrorCode.ArtSizeMismatch);
        }

        payload.CopyTo(_artData.AsSpan(_received, payload.Length));
        _received += payload.Length;
        if (fin)
        {
            _finSeen = true;
        }

        if (_received < _meta.ArtSize)
        {
            if (fin)
            {
                return FailStream(VatpErrorCode.ArtSizeMismatch);
            }

            return ArticleTransferApplyResult.Ok();
        }

        // Exact ArtSize received. FIN marks the final DATA frame; END is still required.
        if (_finSeen)
        {
            Phase = ArticleTransferPhase.AwaitingEnd;
            return ArticleTransferApplyResult.Ok();
        }

        // Exact bytes without FIN: wait for an empty DATA FIN (or fail on extra payload).
        return ArticleTransferApplyResult.Ok();
    }

    /// <summary>Applies a DATA payload sequence.</summary>
    public ArticleTransferApplyResult TryAcceptData(in ReadOnlySequence<byte> payload, bool fin)
    {
        if (payload.IsSingleSegment)
        {
            return TryAcceptData(payload.FirstSpan, fin);
        }

        var length = checked((int)payload.Length);
        var rented = length <= 1024 ? null : System.Buffers.ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Span<byte> buffer = rented is null ? stackalloc byte[length] : rented.AsSpan(0, length);
            payload.CopyTo(buffer);
            return TryAcceptData(buffer, fin);
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// Applies END. Requires exact ArtSize bytes and FIN already observed, then runs
    /// canonical validation.
    /// </summary>
    public ArticleTransferApplyResult TryAcceptEnd()
    {
        if (Phase == ArticleTransferPhase.Completed)
        {
            return FailStream(VatpErrorCode.InvalidStateTransition);
        }

        if (Phase == ArticleTransferPhase.AwaitingEnd
            && _finSeen
            && _received == _meta.ArtSize)
        {
            return TryFinalizeCanonical();
        }

        return FailStream(VatpErrorCode.IncompleteTransfer);
    }

    /// <summary>
    /// Replenishes receive WINDOW credit after accepted DATA bytes, matching the amount
    /// advertised in an outbound WINDOW frame to the peer.
    /// </summary>
    /// <remarks>
    /// Call only after a successful DATA accept for the same byte count.
    /// Does not double-count: DATA acceptance consumes credit; this restores it
    /// once those bytes are buffered in the stream. Unknown/terminal streams fail.
    /// </remarks>
    public ArticleTransferApplyResult TryAcceptWindow(uint addCredit)
    {
        if (IsTerminal || Phase == ArticleTransferPhase.AwaitingMeta)
        {
            return ArticleTransferApplyResult.Fail(VatpErrorCode.UnknownStream);
        }

        _ = _receiveWindow.Add(addCredit);
        return ArticleTransferApplyResult.Ok();
    }

    /// <summary>Fails the stream.</summary>
    public ArticleTransferApplyResult TryFail(VatpErrorCode error)
    {
        if (IsTerminal)
        {
            return ArticleTransferApplyResult.Fail(VatpErrorCode.InvalidStateTransition);
        }

        return FailStream(error == VatpErrorCode.None ? VatpErrorCode.IncompleteTransfer : error);
    }

    /// <summary>Cancels the stream.</summary>
    public ArticleTransferApplyResult TryCancel()
    {
        if (IsTerminal)
        {
            return ArticleTransferApplyResult.Fail(VatpErrorCode.InvalidStateTransition);
        }

        ClearArtData();
        Phase = ArticleTransferPhase.Cancelled;
        Failure = VatpErrorCode.Cancelled;
        return ArticleTransferApplyResult.Ok();
    }

    /// <summary>
    /// Attempts to take the consumable record exactly once. Returns false when not completed.
    /// </summary>
    public bool TryTakeRecord(out ArticleRecord record)
    {
        if (!HasConsumableRecord)
        {
            record = default;
            return false;
        }

        record = _record;
        _hasRecord = false;
        return true;
    }

    private ArticleTransferApplyResult TryFinalizeCanonical()
    {
        if (_artData is null || _received != _meta.ArtSize)
        {
            return FailStream(VatpErrorCode.IncompleteTransfer);
        }

        // END arrived with exact ArtSize + FIN: validate before exposing.
        var expectedArtId = ExpectedArtId;
        var created = ArticleRecordFactory.TryCreateFromCanonicalTransfer(
            _artData,
            in _meta,
            in expectedArtId);
        if (!created.IsAccepted)
        {
            return FailStream(MapTransferFailure(created.MaterializeFailure));
        }

        _record = created.Record;
        _hasRecord = true;
        // Ownership of _artData transferred into the record; do not clear the array.
        _artData = null;
        Phase = ArticleTransferPhase.Completed;
        Failure = VatpErrorCode.None;
        return ArticleTransferApplyResult.Ok();
    }

    private ArticleTransferApplyResult FailStream(VatpErrorCode error)
    {
        ClearArtData();
        Phase = ArticleTransferPhase.Failed;
        Failure = error;
        _hasRecord = false;
        return ArticleTransferApplyResult.Fail(error);
    }

    private void ClearArtData()
    {
        // Drop the article buffer. _received remains the copied DATA payload count.
        _artData = null;
    }

    private static VatpErrorCode MapTransferFailure(NntpArticleCanonicalFailureCode code) =>
        code switch
        {
            NntpArticleCanonicalFailureCode.TransferArtSizeMismatch => VatpErrorCode.ArtSizeMismatch,
            NntpArticleCanonicalFailureCode.TransferArtHashMismatch => VatpErrorCode.ArtHashMismatch,
            NntpArticleCanonicalFailureCode.TransferArticleIdMismatch => VatpErrorCode.ArticleIdMismatch,
            NntpArticleCanonicalFailureCode.TransferInvalidFieldRange => VatpErrorCode.InvalidFieldRange,
            NntpArticleCanonicalFailureCode.TransferFieldTableMismatch => VatpErrorCode.InvalidMeta,
            NntpArticleCanonicalFailureCode.TransferInvalidSelectedDateHeader => VatpErrorCode.InvalidMeta,
            NntpArticleCanonicalFailureCode.TransferInvalidDate => VatpErrorCode.InvalidMeta,
            NntpArticleCanonicalFailureCode.TransferInvalidArtLines => VatpErrorCode.InvalidMeta,
            NntpArticleCanonicalFailureCode.TransferMissingMessageId => VatpErrorCode.InvalidMeta,
            NntpArticleCanonicalFailureCode.TransferNullArtData => VatpErrorCode.IncompleteTransfer,
            NntpArticleCanonicalFailureCode.ArticleTooLarge => VatpErrorCode.ArticleTooLarge,
            _ => VatpErrorCode.CanonicalTransferRejected,
        };
}
