namespace VectorNNTP.BackFiller.Retention;

/// <summary>Source-generated retention logs. Never includes article bodies or secrets.</summary>
internal static partial class ArticleRetentionLogMessages
{
    [LoggerMessage(
        EventId = 5500,
        Level = LogLevel.Information,
        Message = "Article retained articleId={ArticleIdHex} bytes={PayloadBytes} retainedBytes={RetainedPayloadBytes}")]
    public static partial void Retained(ILogger logger, string ArticleIdHex, int PayloadBytes, long RetainedPayloadBytes);

    [LoggerMessage(
        EventId = 5501,
        Level = LogLevel.Warning,
        Message = "Article retention rejected kind={Kind} articleId={ArticleIdHex} bytes={PayloadBytes} retainedBytes={RetainedPayloadBytes}")]
    public static partial void Rejected(ILogger logger, ArticleRetentionKind Kind, string? ArticleIdHex, int PayloadBytes, long RetainedPayloadBytes);

    [LoggerMessage(
        EventId = 5502,
        Level = LogLevel.Debug,
        Message = "Article retention sweep releasedBytes={ReleasedBytes} retainedBytes={RetainedPayloadBytes}")]
    public static partial void Swept(ILogger logger, long ReleasedBytes, long RetainedPayloadBytes);
}
