namespace VectorNNTP.BackFiller.Listener;

/// <summary>Source-generated VATP listener session logs (no article payloads).</summary>
/// <remarks>
/// Each method writes one structured event. An <see cref="Exception"/> parameter is logged and is not thrown by the log method.
/// Call sites are <see cref="VatpListenerSession"/>.
/// </remarks>
internal static partial class VatpListenerLogMessages
{
    /// <summary>Written after the first client HELLO decodes and the negotiated maximum frame payload is stored, before the server HELLO is queued.</summary>
    /// <param name="logger">Logger that receives the event.</param>
    /// <param name="MaxFramePayload">Negotiated maximum DATA payload, the smaller of the client offer and the session default.</param>
    [LoggerMessage(
        EventId = 5450,
        Level = LogLevel.Debug,
        Message = "VATP listener client HELLO accepted maxFramePayload={MaxFramePayload}")]
    internal static partial void ClientHelloAccepted(ILogger logger, uint MaxFramePayload);

    /// <summary>Written after an OPEN is inserted in the send-stream table and before META is queued.</summary>
    /// <param name="logger">Logger that receives the event.</param>
    /// <param name="StreamId">VATP stream id from the OPEN frame.</param>
    /// <param name="RequestId">Request id from the OPEN payload.</param>
    [LoggerMessage(
        EventId = 5451,
        Level = LogLevel.Debug,
        Message = "VATP listener OPEN accepted streamId={StreamId} requestId={RequestId}")]
    internal static partial void OpenAccepted(ILogger logger, uint StreamId, Guid RequestId);

    /// <summary>
    /// Written when retention does not return an opened lease. The open result is disposed first; a FAIL frame is queued afterward.
    /// </summary>
    /// <param name="logger">Logger that receives the event.</param>
    /// <param name="StreamId">VATP stream id from the OPEN frame.</param>
    /// <param name="RequestId">Request id from the OPEN payload.</param>
    [LoggerMessage(
        EventId = 5452,
        Level = LogLevel.Debug,
        Message = "VATP listener OPEN rejected streamId={StreamId} requestId={RequestId}")]
    internal static partial void OpenRejected(ILogger logger, uint StreamId, Guid RequestId);

    /// <summary>
    /// Written when the last DATA bytes for a stream are taken for send, before that DATA frame and the following END frame are written.
    /// </summary>
    /// <param name="logger">Logger that receives the event.</param>
    /// <param name="StreamId">Stream that reached its article size.</param>
    [LoggerMessage(
        EventId = 5453,
        Level = LogLevel.Debug,
        Message = "VATP listener transfer completed streamId={StreamId}")]
    internal static partial void TransferCompleted(ILogger logger, uint StreamId);

    /// <summary>Written after a peer CANCEL removes a known stream. No response frame is queued for that CANCEL.</summary>
    /// <param name="logger">Logger that receives the event.</param>
    /// <param name="StreamId">Stream id from the CANCEL frame.</param>
    [LoggerMessage(
        EventId = 5454,
        Level = LogLevel.Debug,
        Message = "VATP listener transfer cancelled streamId={StreamId}")]
    internal static partial void TransferCancelled(ILogger logger, uint StreamId);

    /// <summary>
    /// Written from stream-scoped FAIL queueing, including when the stream is not in the table.
    /// Connection-scoped FAIL (stream id 0) does not write this event.
    /// </summary>
    /// <param name="logger">Logger that receives the event.</param>
    /// <param name="StreamId">Stream id placed on the FAIL frame.</param>
    /// <param name="ErrorCode">Numeric value of the VATP error code.</param>
    [LoggerMessage(
        EventId = 5455,
        Level = LogLevel.Debug,
        Message = "VATP listener transfer failed streamId={StreamId} error={ErrorCode}")]
    internal static partial void TransferFailed(ILogger logger, uint StreamId, ushort ErrorCode);

    /// <summary>Written once from <see cref="VatpListenerSession"/> run cleanup, after send streams are disposed and before a writer failure is rethrown.</summary>
    /// <param name="logger">Logger that receives the event.</param>
    [LoggerMessage(
        EventId = 5456,
        Level = LogLevel.Debug,
        Message = "VATP listener connection closed")]
    internal static partial void ConnectionClosed(ILogger logger);

    /// <summary>Written when the writer fails for a reason other than session-token cancellation. The caller then requests termination and rethrows.</summary>
    /// <param name="logger">Logger that receives the event.</param>
    /// <param name="exception">Writer failure. Logged with the event and not thrown by this method.</param>
    [LoggerMessage(
        EventId = 5457,
        Level = LogLevel.Debug,
        Message = "VATP listener writer transport failed")]
    internal static partial void WriterTransportFailed(ILogger logger, Exception exception);
}
