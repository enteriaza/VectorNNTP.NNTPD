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
}
