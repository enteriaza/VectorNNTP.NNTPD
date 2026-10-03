using VectorNNTP.Common.Articles;

namespace VectorNNTP.Common.Transport.ArticleTransfer
{
    /// <summary>
    /// Bounded table of active receive streams. StreamId 0 is reserved and rejected.
    /// </summary>
    /// <remarks>
    /// Duplicate active StreamId is rejected. Completed/failed/cancelled streams must be
    /// removed before the identifier may be reused. This is Common state only; it does not
    /// own sockets.
    /// </remarks>
    public sealed class ArticleTransferStreamTable
    {
        private readonly Dictionary<uint, ArticleTransferReceiveStream> _streams;
        private readonly ArticleTransferLimits _limits;

        /// <summary>Creates a table with the given limits.</summary>
        public ArticleTransferStreamTable(ArticleTransferLimits? limits = null)
        {
            _limits = limits ?? ArticleTransferLimits.Default;
            _streams = new Dictionary<uint, ArticleTransferReceiveStream>(_limits.MaxStreamsPerConnection);
        }

        /// <summary>Gets the number of active streams.</summary>
        public int Count => _streams.Count;

        /// <summary>Gets the configured maximum.</summary>
        public int MaxStreams => _limits.MaxStreamsPerConnection;

        /// <summary>Attempts to open a new receive stream.</summary>
        public ArticleTransferApplyResult TryOpen(
            uint streamId,
            Guid requestId,
            ArticleId expectedArtId,
            out ArticleTransferReceiveStream? stream)
        {
            stream = null;
            if (streamId == VatpProtocol.ConnectionStreamId)
            {
                return ArticleTransferApplyResult.Fail(VatpErrorCode.InvalidStreamId);
            }

            if (_streams.Count >= _limits.MaxStreamsPerConnection)
            {
                return ArticleTransferApplyResult.Fail(VatpErrorCode.StreamTableError);
            }

            if (_streams.ContainsKey(streamId))
            {
                return ArticleTransferApplyResult.Fail(VatpErrorCode.StreamTableError);
            }

            stream = new ArticleTransferReceiveStream(streamId, requestId, expectedArtId, _limits);
            _streams.Add(streamId, stream);
            return ArticleTransferApplyResult.Ok();
        }

        /// <summary>Looks up an active stream.</summary>
        public bool TryGet(uint streamId, out ArticleTransferReceiveStream stream) =>
            _streams.TryGetValue(streamId, out stream!);

        /// <summary>
        /// Removes a stream. Required after terminalization before StreamId reuse.
        /// </summary>
        public bool TryRemove(uint streamId) => _streams.Remove(streamId);

        /// <summary>Removes all streams.</summary>
        public void Clear() => _streams.Clear();
    }
}
