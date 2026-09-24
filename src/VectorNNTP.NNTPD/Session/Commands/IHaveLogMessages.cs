namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>Source-generated IHAVE receive/queue diagnostics. Does not log bodies or headers.</summary>
internal static partial class IHaveLogMessages
{
    [LoggerMessage(
        EventId = 1700,
        Level = LogLevel.Information,
        Message = "[{Client}] IHAVE {MessageId} size={Size} pipeReads={PipeReads} receiveMs={ReceiveMs:F1} queued={Queued}")]
    public static partial void Received(
        ILogger logger,
        string Client,
        string MessageId,
        int Size,
        int PipeReads,
        double ReceiveMs,
        bool Queued);
}
