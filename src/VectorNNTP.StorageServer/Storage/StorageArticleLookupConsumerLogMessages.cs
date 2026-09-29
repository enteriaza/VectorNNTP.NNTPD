namespace VectorNNTP.StorageServer.Storage;

/// <summary>Source-generated StorageServer article-lookup consumer log messages.</summary>
internal static partial class StorageArticleLookupConsumerLogMessages
{
    [LoggerMessage(
        EventId = 2910,
        Level = LogLevel.Information,
        Message = "Storage article lookup consumer ready (queue={Queue}, generation={Generation})")]
    public static partial void Ready(ILogger logger, string Queue, long Generation);

    [LoggerMessage(
        EventId = 2911,
        Level = LogLevel.Information,
        Message = "Storage article lookup consumer session replaced (queue={Queue}, generation={Generation})")]
    public static partial void SessionReplaced(ILogger logger, string Queue, long Generation);

    [LoggerMessage(
        EventId = 2912,
        Level = LogLevel.Error,
        Message = "Storage article lookup consumer session attach failed (generation={Generation})")]
    public static partial void SessionAttachFailed(ILogger logger, Exception exception, long Generation);

    [LoggerMessage(
        EventId = 2913,
        Level = LogLevel.Error,
        Message = "RabbitMQ connection is not ready for storage article lookup consume")]
    public static partial void ConnectionNotReady(ILogger logger);

    [LoggerMessage(
        EventId = 2914,
        Level = LogLevel.Warning,
        Message = "Rejected storage article lookup payload ({Reason})")]
    public static partial void PayloadRejected(ILogger logger, string Reason);

    [LoggerMessage(
        EventId = 2915,
        Level = LogLevel.Error,
        Message = "Storage article lookup delivery handling failed")]
    public static partial void DeliveryFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2916,
        Level = LogLevel.Information,
        Message = "Storage article lookup consumer stopped")]
    public static partial void Stopped(ILogger logger);
}
