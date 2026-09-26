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

    [LoggerMessage(
        EventId = 5310,
        Level = LogLevel.Information,
        Message = "Article Work response publisher starting generation={Generation}")]
    public static partial void PublisherStarting(ILogger logger, long Generation);

    [LoggerMessage(
        EventId = 5311,
        Level = LogLevel.Information,
        Message = "Article Work response publisher running generation={Generation}")]
    public static partial void PublisherRunning(ILogger logger, long Generation);

    [LoggerMessage(
        EventId = 5312,
        Level = LogLevel.Information,
        Message = "Article Work response publisher retiring generation={Generation}")]
    public static partial void PublisherRetiring(ILogger logger, long Generation);

    [LoggerMessage(
        EventId = 5313,
        Level = LogLevel.Information,
        Message = "Article Work response publisher stopped generation={Generation}")]
    public static partial void PublisherStopped(ILogger logger, long Generation);

    [LoggerMessage(
        EventId = 5314,
        Level = LogLevel.Error,
        Message = "Article Work response publisher failed to start: {Reason}")]
    public static partial void PublisherStartFailed(ILogger logger, string Reason);

    [LoggerMessage(
        EventId = 5315,
        Level = LogLevel.Error,
        Message = "Article Work response publisher failed to rebuild after connection replacement: {Reason}")]
    public static partial void PublisherReplaceFailed(ILogger logger, string Reason);

    [LoggerMessage(
        EventId = 5316,
        Level = LogLevel.Warning,
        Message = "Article Work response publication failed generation={Generation} requestId={RequestId} outcome={Outcome}: {Reason}")]
    public static partial void PublicationFailed(
        ILogger logger,
        long Generation,
        string RequestId,
        string Outcome,
        string Reason);

    [LoggerMessage(
        EventId = 5317,
        Level = LogLevel.Debug,
        Message = "Article Work response published and confirmed generation={Generation} requestId={RequestId} correlationId={CorrelationId} outcome={Outcome}")]
    public static partial void PublicationConfirmed(
        ILogger logger,
        long Generation,
        string RequestId,
        string CorrelationId,
        string Outcome);
}
