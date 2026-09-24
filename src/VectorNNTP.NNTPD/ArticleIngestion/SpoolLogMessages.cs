namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>Source-generated incoming spool writer log messages.</summary>
internal static partial class SpoolLogMessages
{
    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Warning,
        Message = "Incoming spool writer stop canceled with {Queued} article(s) still buffered")]
    public static partial void StopCanceledWithBufferedArticles(ILogger logger, int Queued);

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Error,
        Message = "Incoming spool writer stopped with an error")]
    public static partial void WriterStoppedWithError(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Information,
        Message = "Incoming spool writer started (transit queue memory limit {MemoryLimit} bytes, max article {MaxBytes} bytes, dir {Dir})")]
    public static partial void WriterStarted(ILogger logger, long MemoryLimit, int MaxBytes, string Dir);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Error,
        Message = "Failed to persist incoming article {MessageId} ({Bytes} bytes)")]
    public static partial void PersistFailed(ILogger logger, Exception exception, string MessageId, int Bytes);

    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Information,
        Message = "Incoming spool writer stopped")]
    public static partial void WriterStopped(ILogger logger);
}
