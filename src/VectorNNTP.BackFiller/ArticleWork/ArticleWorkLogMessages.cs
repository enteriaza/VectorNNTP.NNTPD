namespace VectorNNTP.BackFiller.ArticleWork;

/// <summary>Source-generated Article Work consumer logs. Never includes payloads or credentials.</summary>
internal static partial class ArticleWorkLogMessages
{
    [LoggerMessage(
        EventId = 5300,
        Level = LogLevel.Information,
        Message = "Article Work consumer starting backbone={Backbone} queue={Queue} generation={Generation}")]
    public static partial void ConsumerStarting(ILogger logger, string Backbone, string Queue, long Generation);

    [LoggerMessage(
        EventId = 5301,
        Level = LogLevel.Information,
        Message = "Article Work consumer running backbone={Backbone} queue={Queue} generation={Generation} consumerTag={ConsumerTag}")]
    public static partial void ConsumerRunning(ILogger logger, string Backbone, string Queue, long Generation, string ConsumerTag);

    [LoggerMessage(
        EventId = 5302,
        Level = LogLevel.Information,
        Message = "Article Work consumer retiring backbone={Backbone} generation={Generation}")]
    public static partial void ConsumerRetiring(ILogger logger, string Backbone, long Generation);

    [LoggerMessage(
        EventId = 5303,
        Level = LogLevel.Information,
        Message = "Article Work consumer stopped backbone={Backbone} generation={Generation}")]
    public static partial void ConsumerStopped(ILogger logger, string Backbone, long Generation);

    [LoggerMessage(
        EventId = 5304,
        Level = LogLevel.Warning,
        Message = "Article Work request rejected backbone={Backbone} generation={Generation} deliveryTag={DeliveryTag} reason={Reason}")]
    public static partial void RequestRejected(ILogger logger, string Backbone, long Generation, ulong DeliveryTag, string Reason);

    [LoggerMessage(
        EventId = 5305,
        Level = LogLevel.Warning,
        Message = "Article Work settlement skipped on stale channel backbone={Backbone} generation={Generation} deliveryTag={DeliveryTag}")]
    public static partial void StaleSettlementSkipped(ILogger logger, string Backbone, long Generation, ulong DeliveryTag);

    [LoggerMessage(
        EventId = 5306,
        Level = LogLevel.Error,
        Message = "Article Work consumer failed to start backbone={Backbone} queue={Queue}: {Reason}")]
    public static partial void ConsumerStartFailed(ILogger logger, string Backbone, string Queue, string Reason);

    [LoggerMessage(
        EventId = 5307,
        Level = LogLevel.Error,
        Message = "Article Work consumers failed to rebuild after connection replacement: {Reason}")]
    public static partial void ConsumerReplaceFailed(ILogger logger, string Reason);
}
