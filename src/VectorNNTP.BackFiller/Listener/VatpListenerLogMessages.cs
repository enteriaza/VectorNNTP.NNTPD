namespace VectorNNTP.BackFiller.Listener;

/// <summary>Source-generated VATP listener session logs (no article payloads).</summary>
internal static partial class VatpListenerLogMessages
{
    [LoggerMessage(
        EventId = 5450,
        Level = LogLevel.Debug,
        Message = "VATP listener client HELLO accepted maxFramePayload={MaxFramePayload}")]
    internal static partial void ClientHelloAccepted(ILogger logger, uint MaxFramePayload);

    [LoggerMessage(
        EventId = 5451,
        Level = LogLevel.Debug,
        Message = "VATP listener OPEN accepted streamId={StreamId} requestId={RequestId}")]
    internal static partial void OpenAccepted(ILogger logger, uint StreamId, Guid RequestId);

    [LoggerMessage(
        EventId = 5452,
        Level = LogLevel.Debug,
        Message = "VATP listener OPEN rejected streamId={StreamId} requestId={RequestId}")]
    internal static partial void OpenRejected(ILogger logger, uint StreamId, Guid RequestId);

    [LoggerMessage(
        EventId = 5453,
        Level = LogLevel.Debug,
        Message = "VATP listener transfer completed streamId={StreamId}")]
    internal static partial void TransferCompleted(ILogger logger, uint StreamId);

    [LoggerMessage(
        EventId = 5454,
        Level = LogLevel.Debug,
        Message = "VATP listener transfer cancelled streamId={StreamId}")]
    internal static partial void TransferCancelled(ILogger logger, uint StreamId);

    [LoggerMessage(
        EventId = 5455,
        Level = LogLevel.Debug,
        Message = "VATP listener transfer failed streamId={StreamId} error={ErrorCode}")]
    internal static partial void TransferFailed(ILogger logger, uint StreamId, ushort ErrorCode);

    [LoggerMessage(
        EventId = 5456,
        Level = LogLevel.Debug,
        Message = "VATP listener connection closed")]
    internal static partial void ConnectionClosed(ILogger logger);

    [LoggerMessage(
        EventId = 5457,
        Level = LogLevel.Debug,
        Message = "VATP listener writer transport failed")]
    internal static partial void WriterTransportFailed(ILogger logger, Exception exception);
}
