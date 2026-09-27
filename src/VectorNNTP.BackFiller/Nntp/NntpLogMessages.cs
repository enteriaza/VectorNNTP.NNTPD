namespace VectorNNTP.BackFiller.Nntp;

/// <summary>Source-generated NNTP provider logs. Never includes credentials or article bodies.</summary>
internal static partial class NntpLogMessages
{
    [LoggerMessage(
        EventId = 5400,
        Level = LogLevel.Information,
        Message = "NNTP session ready backbone={Backbone} host={Host} port={Port} tls={UseTls}")]
    public static partial void SessionReady(ILogger logger, string Backbone, string Host, int Port, bool UseTls);

    [LoggerMessage(
        EventId = 5401,
        Level = LogLevel.Warning,
        Message = "NNTP session retired backbone={Backbone} reason={Reason}")]
    public static partial void SessionRetired(ILogger logger, string Backbone, string Reason);

    [LoggerMessage(
        EventId = 5402,
        Level = LogLevel.Warning,
        Message = "NNTP provider retrieval failed backbone={Backbone} kind={Kind} status={StatusCode} reason={Reason}")]
    public static partial void RetrievalFailed(ILogger logger, string Backbone, ArticleRetrievalKind Kind, int? StatusCode, string Reason);

    [LoggerMessage(
        EventId = 5403,
        Level = LogLevel.Information,
        Message = "NNTP usable capacity published backboneCount={BackboneCount}")]
    public static partial void UsableCapacityPublished(ILogger logger, int BackboneCount);

    [LoggerMessage(
        EventId = 5404,
        Level = LogLevel.Warning,
        Message = "NNTP session replenish failed backbone={Backbone} reason={Reason}")]
    public static partial void SessionReplenishFailed(ILogger logger, string Backbone, string Reason);

    [LoggerMessage(
        EventId = 5405,
        Level = LogLevel.Debug,
        Message = "{Session}: Connecting article acquisition session to {Host}:{Port} (SSL={UseTls})")]
    public static partial void WireConnecting(ILogger logger, string Session, string Host, int Port, string UseTls);

    [LoggerMessage(
        EventId = 5406,
        Level = LogLevel.Debug,
        Message = "{Session}: TX: {Command}")]
    public static partial void WireTx(ILogger logger, string Session, string Command);

    [LoggerMessage(
        EventId = 5407,
        Level = LogLevel.Debug,
        Message = "{Session}: RX: {Response}")]
    public static partial void WireRx(ILogger logger, string Session, string Response);

    [LoggerMessage(
        EventId = 5408,
        Level = LogLevel.Debug,
        Message = "{Session}: RX: ARTICLE payload complete bytes={Bytes}")]
    public static partial void WireArticlePayloadComplete(ILogger logger, string Session, int Bytes);

    [LoggerMessage(
        EventId = 5409,
        Level = LogLevel.Debug,
        Message = "{Session}: Session retiring")]
    public static partial void WireRetiring(ILogger logger, string Session);

    [LoggerMessage(
        EventId = 5410,
        Level = LogLevel.Debug,
        Message = "{Session}: TLS handshake starting")]
    public static partial void WireTlsHandshakeStarting(ILogger logger, string Session);

    [LoggerMessage(
        EventId = 5411,
        Level = LogLevel.Debug,
        Message = "{Session}: TLS handshake completed")]
    public static partial void WireTlsHandshakeCompleted(ILogger logger, string Session);
}
