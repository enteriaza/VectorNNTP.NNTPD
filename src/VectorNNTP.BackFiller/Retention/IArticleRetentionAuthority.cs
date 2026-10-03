namespace VectorNNTP.BackFiller.Retention
{
    /// <summary>
    /// Single owner of retained CanonicalV1 <see cref="VectorNNTP.Common.Articles.ArticleRecord"/> values,
    /// identity, TTL, and capacity. Independent of RabbitMQ and NNTP sessions.
    /// VATP OPEN is the only data-plane consumer of retained ArtData.
    /// </summary>
    /// <remarks>
    /// An openable RequestId is a hard ArticleWork Success capability: FIFO capacity reclaim must not
    /// unindex an entry while <c>OpenableRequestIdCount &gt; 0</c>. TTL expiry, explicit cancel,
    /// successful OPEN consumption, and process dispose remain the ways a capability ends.
    /// </remarks>
    internal interface IArticleRetentionAuthority
    {
        /// <summary>Gets configured sweep interval.</summary>
        TimeSpan SweepInterval { get; }

        /// <summary>Gets currently owned retained ArtData bytes.</summary>
        long RetainedPayloadBytes { get; }

        /// <summary>Gets the number of physically retained entries.</summary>
        int RetainedCount { get; }

        /// <summary>
        /// Retains a CanonicalV1 article record and attaches a VATP OPEN RequestId capability.
        /// </summary>
        /// <param name="messageId">Exact Message-ID. Not normalized.</param>
        /// <param name="requestId">ArticleWork Success RequestId to attach as an openable capability.</param>
        /// <param name="record">CanonicalV1 record. ArtData is referenced on first admission and is not copied.</param>
        /// <param name="selectedDateHeaderName">Date-family header name stored for VATP META on first admission.</param>
        /// <returns>
        /// Admission classification, identity when one was resolved, the VATP endpoint when the article
        /// is available, and the payload-byte totals after the attempt.
        /// </returns>
        /// <remarks>
        /// Does not copy ArtData. On AlreadyPresent, the existing retained
        /// <see cref="VectorNNTP.Common.Articles.ArticleRecord"/> remains authoritative (first-wins);
        /// the new RequestId is added as an additional openable capability and does not revoke prior
        /// RequestIds. When the per-article openable RequestId bound is reached, admission is rejected
        /// without modifying existing RequestIds or ArtData.
        /// The caller retains ownership of the unused incoming ArtData buffer on AlreadyPresent.
        /// When capacity is insufficient, FIFO reclaim may remove only entries with no openable
        /// RequestIds; otherwise admission fails with <see cref="ArticleRetentionKind.CapacityUnavailable"/>.
        /// </remarks>
        ArticleRetentionResult RetainCanonical(
            string messageId,
            Guid requestId,
            VectorNNTP.Common.Articles.ArticleRecord record,
            VectorNNTP.Common.Articles.Parsing.NntpArticleHeaderName selectedDateHeaderName);

        /// <summary>
        /// Resolves a VATP OPEN: RequestId is primary; ArticleId is verified.
        /// Successful OPEN consumes only that RequestId after optional outbound-byte reservation succeeds.
        /// Wrong ArticleId does not consume RequestId and does not reserve.
        /// </summary>
        /// <param name="requestId">ArticleWork Success RequestId capability.</param>
        /// <param name="expectedArticleId">ArticleId from the OPEN frame.</param>
        /// <param name="tryReserveOutboundBytes">
        /// Optional session admission for found/outbound bytes (ArtSize). Invoked under the retention
        /// gate before RequestId consumption. Returning <see langword="false"/> rejects OPEN and
        /// leaves the RequestId available for retry.
        /// </param>
        /// <returns>
        /// <see cref="VatpOpenKind.Opened"/> with a reader lease after that RequestId is consumed, or
        /// <see cref="VatpOpenKind.Rejected"/> with no lease.
        /// </returns>
        VatpOpenResult TryOpenTransfer(
            Guid requestId,
            VectorNNTP.Common.Articles.ArticleId expectedArticleId,
            Func<int, bool>? tryReserveOutboundBytes = null);

        /// <summary>Cancels one openable RequestId without releasing the Message-ID entry.</summary>
        /// <param name="requestId">Openable RequestId to detach.</param>
        /// <returns>
        /// <see langword="true"/> when that RequestId was removed.
        /// <see langword="false"/> when it was not an openable capability.
        /// </returns>
        bool TryCancelPendingRequest(Guid requestId);

        /// <summary>Reclaims TTL-expired entries.</summary>
        /// <returns>Payload bytes physically released by this pass. Zero when nothing was physically released.</returns>
        long SweepExpired();

        /// <summary>Stops new admissions.</summary>
        /// <remarks>
        /// Closes <see cref="RetainCanonical"/> only. Existing entries, openable RequestIds,
        /// <see cref="TryOpenTransfer"/>, <see cref="TryCancelPendingRequest"/>, and
        /// <see cref="SweepExpired"/> stay available until the authority is disposed.
        /// </remarks>
        void BeginShutdown();
    }
}
