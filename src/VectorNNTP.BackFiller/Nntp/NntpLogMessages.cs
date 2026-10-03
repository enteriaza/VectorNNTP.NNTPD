namespace VectorNNTP.BackFiller.Nntp;

/// <summary>Source-generated NNTP provider logs. Never includes credentials or article bodies.</summary>
internal static partial class NntpLogMessages
{
    /// <summary>
    /// Written after connect, greeting, capability negotiation, optional STARTTLS, and authentication
    /// leave the session in <see cref="NntpSessionState.Ready"/>.
    /// </summary>
    /// <param name="logger">Logger that receives the information event.</param>
    /// <param name="Backbone">Provider backbone label.</param>
    /// <param name="Host">Upstream host from the provider definition.</param>
    /// <param name="Port">Upstream port from the provider definition.</param>
    /// <param name="UseTls">
    /// <see cref="BackFillerProviderDefinition.UseTls"/> (implicit TLS from connect).
    /// A session that upgraded with STARTTLS still logs false when implicit TLS is off.
    /// </param>
    [LoggerMessage(
        EventId = 5400,
        Level = LogLevel.Information,
        Message = "NNTP session ready backbone={Backbone} host={Host} port={Port} tls={UseTls}")]
    internal static partial void SessionReady(ILogger logger, string Backbone, string Host, int Port, bool UseTls);

    /// <summary>
    /// Written when a live pooled session is removed for retirement, before that session is disposed.
    /// Not written when the session was already absent from the pool.
    /// </summary>
    /// <param name="logger">Logger that receives the warning.</param>
    /// <param name="Backbone">Backbone of the pool that owned the session.</param>
    /// <param name="Reason">Caller-supplied retirement text. This method does not throw.</param>
    [LoggerMessage(
        EventId = 5401,
        Level = LogLevel.Warning,
        Message = "NNTP session retired backbone={Backbone} reason={Reason}")]
    internal static partial void SessionRetired(ILogger logger, string Backbone, string Reason);

    /// <summary>
    /// Written when <see cref="NntpProviderSession.DownloadArticleAsync"/> returns a kind other than
    /// <see cref="ArticleRetrievalKind.ArticleRetrieved"/>.
    /// Results returned before download are not logged: already-canceled acquisition, a missing pool,
    /// and <see cref="NntpProviderConnectException"/>.
    /// </summary>
    /// <param name="logger">Logger that receives the warning.</param>
    /// <param name="Backbone">Work-item backbone (<see cref="VectorNNTP.BackFiller.ArticleWork.ArticleWorkRequest.Backbone"/>).</param>
    /// <param name="Kind">Retrieval classification from the download result.</param>
    /// <param name="StatusCode">NNTP status from the download result, or null when the server produced none.</param>
    /// <param name="Reason">Diagnostic text from the download result. Callers do not pass secrets.</param>
    [LoggerMessage(
        EventId = 5402,
        Level = LogLevel.Warning,
        Message = "NNTP provider retrieval failed backbone={Backbone} kind={Kind} status={StatusCode} reason={Reason}")]
    internal static partial void RetrievalFailed(ILogger logger, string Backbone, ArticleRetrievalKind Kind, int? StatusCode, string Reason);

    /// <summary>
    /// Written by the provider registry after it publishes pool ACTIVE counts.
    /// Not written when dispose publishes an empty snapshot.
    /// </summary>
    /// <param name="logger">Logger that receives the information event.</param>
    /// <param name="BackboneCount">
    /// Caller-supplied count. The registry passes how many entries in the dictionary just given to
    /// <see cref="BackboneUsableCapacityState.PublishSnapshot"/> have a positive count.
    /// A blank name with a positive count is included here even though publication omits it.
    /// </param>
    [LoggerMessage(
        EventId = 5403,
        Level = LogLevel.Information,
        Message = "NNTP usable capacity published backboneCount={BackboneCount}")]
    internal static partial void UsableCapacityPublished(ILogger logger, int BackboneCount);

    /// <summary>
    /// Written when a pool deficit fill throws an exception other than shutdown cancellation,
    /// and when Article Work reconcile throws any exception.
    /// </summary>
    /// <param name="logger">Logger that receives the warning.</param>
    /// <param name="Backbone">
    /// Caller-supplied scope. Pool fills pass the provider backbone. Consumer reconcile passes the literal <c>consumers</c>.
    /// </param>
    /// <param name="Reason">
    /// <see cref="Exception.Message"/> from the caught exception. The exception is logged through this text; this method does not throw it.
    /// </param>
    [LoggerMessage(
        EventId = 5404,
        Level = LogLevel.Warning,
        Message = "NNTP session replenish failed backbone={Backbone} reason={Reason}")]
    internal static partial void SessionReplenishFailed(ILogger logger, string Backbone, string Reason);

    /// <summary>
    /// Written at Debug when a session starts connect, before the transport is opened.
    /// </summary>
    /// <param name="logger">Logger that receives the debug event.</param>
    /// <param name="Session"><see cref="NntpProviderSession.WireLogIdentity"/> for the session.</param>
    /// <param name="Host">Upstream host from the provider definition.</param>
    /// <param name="Port">Upstream port from the provider definition.</param>
    /// <param name="UseTls">
    /// Caller-supplied text. The session passes <c>true</c> or <c>false</c> for
    /// <see cref="BackFillerProviderDefinition.UseTls"/>, not a Boolean and not STARTTLS state.
    /// </param>
    [LoggerMessage(
        EventId = 5405,
        Level = LogLevel.Debug,
        Message = "{Session}: Connecting article acquisition session to {Host}:{Port} (SSL={UseTls})")]
    internal static partial void WireConnecting(ILogger logger, string Session, string Host, int Port, string UseTls);

    /// <summary>
    /// Written at Debug immediately before command bytes are written, including QUIT during dispose.
    /// The log is emitted even if the write then fails.
    /// </summary>
    /// <param name="logger">Logger that receives the debug event.</param>
    /// <param name="Session"><see cref="NntpProviderSession.WireLogIdentity"/> for the session.</param>
    /// <param name="Command">
    /// Caller-supplied command text. AUTHINFO callers pass redacted <c>AUTHINFO USER ***</c> and
    /// <c>AUTHINFO PASS ***</c>. This method does not redact <paramref name="Command"/>.
    /// </param>
    [LoggerMessage(
        EventId = 5406,
        Level = LogLevel.Debug,
        Message = "{Session}: TX: {Command}")]
    internal static partial void WireTx(ILogger logger, string Session, string Command);

    /// <summary>
    /// Written at Debug when a non-null status line has been read. Article payload bytes are not logged here.
    /// </summary>
    /// <param name="logger">Logger that receives the debug event.</param>
    /// <param name="Session"><see cref="NntpProviderSession.WireLogIdentity"/> for the session.</param>
    /// <param name="Response">ASCII decoding of the status-line bytes supplied by the caller.</param>
    [LoggerMessage(
        EventId = 5407,
        Level = LogLevel.Debug,
        Message = "{Session}: RX: {Response}")]
    internal static partial void WireRx(ILogger logger, string Session, string Response);

    /// <summary>
    /// Written at Debug after a destuffed ARTICLE payload has been read and before the header/body separator check.
    /// Not written when the payload read throws.
    /// </summary>
    /// <param name="logger">Logger that receives the debug event.</param>
    /// <param name="Session"><see cref="NntpProviderSession.WireLogIdentity"/> for the session.</param>
    /// <param name="Bytes">Destuffed payload length. The payload itself is not logged.</param>
    [LoggerMessage(
        EventId = 5408,
        Level = LogLevel.Debug,
        Message = "{Session}: RX: ARTICLE payload complete bytes={Bytes}")]
    internal static partial void WireArticlePayloadComplete(ILogger logger, string Session, int Bytes);

    /// <summary>
    /// Written at Debug on the first dispose, after the session enters <see cref="NntpSessionState.Retiring"/> and before QUIT.
    /// A second dispose does not write it.
    /// </summary>
    /// <param name="logger">Logger that receives the debug event.</param>
    /// <param name="Session"><see cref="NntpProviderSession.WireLogIdentity"/> for the session.</param>
    [LoggerMessage(
        EventId = 5409,
        Level = LogLevel.Debug,
        Message = "{Session}: Session retiring")]
    internal static partial void WireRetiring(ILogger logger, string Session);

    /// <summary>
    /// Written at Debug immediately before a STARTTLS handshake on the existing clear-text stream.
    /// Implicit TLS from connect does not write this event.
    /// </summary>
    /// <param name="logger">Logger that receives the debug event.</param>
    /// <param name="Session"><see cref="NntpProviderSession.WireLogIdentity"/> for the session.</param>
    [LoggerMessage(
        EventId = 5410,
        Level = LogLevel.Debug,
        Message = "{Session}: TLS handshake starting")]
    internal static partial void WireTlsHandshakeStarting(ILogger logger, string Session);

    /// <summary>
    /// Written at Debug after a STARTTLS handshake returns and the session stream has been replaced.
    /// Not written when the handshake throws. Implicit TLS from connect does not write this event.
    /// </summary>
    /// <param name="logger">Logger that receives the debug event.</param>
    /// <param name="Session"><see cref="NntpProviderSession.WireLogIdentity"/> for the session.</param>
    [LoggerMessage(
        EventId = 5411,
        Level = LogLevel.Debug,
        Message = "{Session}: TLS handshake completed")]
    internal static partial void WireTlsHandshakeCompleted(ILogger logger, string Session);
}
