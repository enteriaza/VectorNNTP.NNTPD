using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Processing;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.RabbitMq.ArticleWork;
using VectorNNTP.NNTPD.Session.CommandProcessor;
using VectorNNTP.NNTPD.Session.Framing;
using VectorNNTP.NNTPD.Storage;
using VectorNNTP.NNTPD.Transport.Vatp;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// ARTICLE, HEAD, BODY, and STAT as defined by RFC 3977, Sections 6.2.1–6.2.4.
/// </summary>
/// <remarks>
/// <para>
/// Message-id form resolves through ArticleWork RPC then VATP
/// (<see cref="IVatpArticleClient"/>). A definitive ArticleWork
/// <see cref="ArticleWorkOutcome.ArticleNotFound"/> or
/// <see cref="ArticleWorkOutcome.InvalidArticle"/> asks
/// <see cref="IStorageArticleLookupClient"/> once and, on a positive URI for the same
/// ArticleId, uses that same VATP client. A validated CanonicalV1
/// <see cref="ArticleRecord"/> is required before any <c>220</c>/<c>221</c>/<c>222</c>/<c>223</c>
/// reply. Local ingest admission is independent of serving the requesting client.
/// </para>
/// <para>
/// Numeric article lookup and omitted current-article forms are unchanged:
/// message-id unavailable → <c>430</c>, article number → <c>412</c>/<c>423</c>,
/// omitted current article → <c>412</c>/<c>420</c>. Temporary transfer failures use
/// <c>400</c> without closing the connection. Error replies are single-line.
/// </para>
/// <para>
/// An unsuccessful lookup MUST NOT change the selected group or current article
/// (RFC 3977 §6.2.1.2). There is no current-article pointer yet, so the omitted form
/// after a successful GROUP is <c>420</c>.
/// </para>
/// </remarks>
internal static class Article
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(Article));

    private enum RetrievalKind
    {
        Article,
        Head,
        Body,
        Stat,
    }

    /// <summary>Handles <c>ARTICLE</c> (RFC 3977, Section 6.2.1).</summary>
    public static ValueTask HandleArticleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "ARTICLE", static (c, ct) => ExecuteAsync(c, RetrievalKind.Article, ct), cancellationToken);

    /// <summary>Handles <c>HEAD</c> (RFC 3977, Section 6.2.2).</summary>
    public static ValueTask HandleHeadAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "HEAD", static (c, ct) => ExecuteAsync(c, RetrievalKind.Head, ct), cancellationToken);

    /// <summary>Handles <c>BODY</c> (RFC 3977, Section 6.2.3).</summary>
    public static ValueTask HandleBodyAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "BODY", static (c, ct) => ExecuteAsync(c, RetrievalKind.Body, ct), cancellationToken);

    /// <summary>Handles <c>STAT</c> (RFC 3977, Section 6.2.4).</summary>
    public static ValueTask HandleStatAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "STAT", static (c, ct) => ExecuteAsync(c, RetrievalKind.Stat, ct), cancellationToken);

    private static async ValueTask ExecuteAsync(
        NntpCommandContext context,
        RetrievalKind kind,
        CancellationToken cancellationToken)
    {
        var argument = context.ArgumentSpan;
        if (argument.IsEmpty)
        {
            await WriteCurrentArticleFailureAsync(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (argument[0] == (byte)'<')
        {
            await ExecuteMessageIdAsync(context, kind, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteNumberLookupFailureAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ExecuteMessageIdAsync(
        NntpCommandContext context,
        RetrievalKind kind,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveByMessageIdAsync(context, cancellationToken).ConfigureAwait(false);
        switch (resolved.Kind)
        {
            case ResolveKind.NotFound:
                await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.NoArticleWithMessageId,
                    NntpResponseStatus.NoArticleWithMessageId,
                    cancellationToken).ConfigureAwait(false);
                return;
            case ResolveKind.TemporaryFailure:
                await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.ServiceTemporarilyUnavailable,
                    NntpResponseStatus.ServiceTemporarilyUnavailable,
                    cancellationToken).ConfigureAwait(false);
                return;
            case ResolveKind.Success:
                break;
            default:
                await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.ServiceTemporarilyUnavailable,
                    NntpResponseStatus.ServiceTemporarilyUnavailable,
                    cancellationToken).ConfigureAwait(false);
                return;
        }

        var record = resolved.Record;
        TryAdmitBackFiller(context, in record);

        var messageIdText = Encoding.ASCII.GetString(record.MessageId);
        switch (kind)
        {
            case RetrievalKind.Article:
                await context.Response.WriteCustomerArticleAsync(
                    record.ArtData,
                    messageIdText,
                    articleNumber: 0,
                    cancellationToken).ConfigureAwait(false);
                NntpCommandReply.TryNote(context, Logger, "220 0 " + messageIdText);
                return;
            case RetrievalKind.Head:
                if (!ArticleWireReconstructor.TrySplitHeadersAndBody(record.ArtData.Span, out var headersSpan, out _))
                {
                    headersSpan = record.ArtData.Span;
                }

                var headers = record.ArtData.Slice(0, headersSpan.Length);
                await context.Response.WriteCustomerHeadAsync(
                    headers,
                    messageIdText,
                    articleNumber: 0,
                    cancellationToken).ConfigureAwait(false);
                NntpCommandReply.TryNote(context, Logger, "221 0 " + messageIdText);
                return;
            case RetrievalKind.Body:
                _ = ArticleWireReconstructor.TrySplitHeadersAndBody(record.ArtData.Span, out var headerSpan, out var bodySpan);
                var body = bodySpan.IsEmpty
                    ? ReadOnlyMemory<byte>.Empty
                    : record.ArtData.Slice(headerSpan.Length, bodySpan.Length);
                await context.Response.WriteCustomerBodyFromBodyAsync(
                    body,
                    messageIdText,
                    articleNumber: 0,
                    cancellationToken).ConfigureAwait(false);
                NntpCommandReply.TryNote(context, Logger, "222 0 " + messageIdText);
                return;
            case RetrievalKind.Stat:
                await context.Response.WriteCustomerStatAsync(record.MessageId, articleNumber: 0, cancellationToken)
                    .ConfigureAwait(false);
                NntpCommandReply.TryNote(context, Logger, "223 0 " + messageIdText);
                return;
            default:
                await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.ServiceTemporarilyUnavailable,
                    NntpResponseStatus.ServiceTemporarilyUnavailable,
                    cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private static async ValueTask<ResolveResult> ResolveByMessageIdAsync(
        NntpCommandContext context,
        CancellationToken cancellationToken)
    {
        var rpc = context.Session.ArticleWorkRpc;
        if (rpc is null)
        {
            return ResolveResult.NotFound();
        }

        var messageId = context.ArgumentMemory;
        ArticleWorkRpcResult lookup;
        try
        {
            lookup = await rpc.LookupByMessageIdAsync(messageId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ArticleWorkRpcLogMessages.ArticleLookupFailed(
                Logger,
                ex,
                Encoding.ASCII.GetString(messageId.Span));
            return ResolveResult.Temporary();
        }

        if (lookup.Outcome is ArticleWorkOutcome.ArticleNotFound or ArticleWorkOutcome.InvalidArticle)
        {
            return await ResolveFromStorageAsync(context, messageId, cancellationToken).ConfigureAwait(false);
        }

        if (lookup.Outcome != ArticleWorkOutcome.Success
            || string.IsNullOrWhiteSpace(lookup.Uri)
            || lookup.ArticleId is not { } expectedArtId)
        {
            ArticleRetrievalLogMessages.TransferUnavailable(
                Logger,
                lookup.Outcome.ToString(),
                lookup.Error);
            return ResolveResult.Temporary();
        }

        var vatp = context.Session.VatpArticleClient;
        if (vatp is null)
        {
            ArticleRetrievalLogMessages.TransferUnavailable(Logger, "VatpClientMissing", lookup.Uri);
            return ResolveResult.Temporary();
        }

        VatpFetchResult fetch;
        try
        {
            fetch = await vatp.FetchArticleAsync(
                lookup.Uri,
                lookup.RequestId,
                expectedArtId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ArticleRetrievalLogMessages.VatpFetchFailed(Logger, ex, lookup.Uri, lookup.RequestId);
            return ResolveResult.Temporary();
        }

        return MapFetch(fetch, messageId.Span, expectedArtId);
    }

    /// <summary>
    /// Silence from the fleet lookup. Matches <c>StorageArticleLookupService</c> when no
    /// positive response arrives before the existing lookup budget.
    /// </summary>
    private const string StorageLookupSilence = "Storage article lookup timed out with no positive response.";

    private static async ValueTask<ResolveResult> ResolveFromStorageAsync(
        NntpCommandContext context,
        ReadOnlyMemory<byte> messageId,
        CancellationToken cancellationToken)
    {
        var storage = context.Session.StorageArticleLookup;
        if (storage is null)
        {
            return ResolveResult.NotFound();
        }

        var articleId = ArticleId.FromMessageId(messageId.Span);
        StorageArticleLookupResult fleet;
        try
        {
            fleet = await storage.LookupAsync(articleId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            ArticleRetrievalLogMessages.StorageLookupCompleted(
                Logger,
                "unavailable",
                articleId.ToLowerHexString(),
                Guid.Empty);
            return ResolveResult.Temporary();
        }

        if (fleet.Outcome != StorageArticleLookupOutcome.Found)
        {
            var miss = string.Equals(fleet.Error, StorageLookupSilence, StringComparison.Ordinal);
            ArticleRetrievalLogMessages.StorageLookupCompleted(
                Logger,
                miss ? "miss" : "unavailable",
                articleId.ToLowerHexString(),
                fleet.RequestId);
            return miss ? ResolveResult.NotFound() : ResolveResult.Temporary();
        }

        if (fleet.ArticleId != articleId || !TryBindStorageUri(fleet.Uri, articleId, out var cacheUri))
        {
            ArticleRetrievalLogMessages.StorageLookupCompleted(
                Logger,
                "unavailable",
                articleId.ToLowerHexString(),
                fleet.RequestId);
            ArticleRetrievalLogMessages.TransferUnavailable(Logger, "StorageUri", fleet.Uri);
            return ResolveResult.Temporary();
        }

        var vatp = context.Session.VatpArticleClient;
        if (vatp is null)
        {
            ArticleRetrievalLogMessages.StorageLookupCompleted(
                Logger,
                "found",
                articleId.ToLowerHexString(),
                fleet.RequestId);
            ArticleRetrievalLogMessages.TransferUnavailable(Logger, "VatpClientMissing", cacheUri);
            return ResolveResult.Temporary();
        }

        ArticleRetrievalLogMessages.StorageLookupCompleted(
            Logger,
            "found",
            articleId.ToLowerHexString(),
            fleet.RequestId);

        var serverId = fleet.ServerId ?? 0;
        VatpFetchResult fetch;
        try
        {
            fetch = await FetchStorageCandidateAsync(
                vatp,
                cacheUri,
                fleet.RequestId,
                articleId,
                serverId,
                attempt: 1,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return ResolveResult.Temporary();
        }

        if (IsAlternateEligible(fetch) && fleet.Alternates is not null)
        {
            // Caller cancellation is terminal even when an alternate is already retained
            // or the same cancel has already closed the lookup window.
            cancellationToken.ThrowIfCancellationRequested();
            var alternate = await fleet.Alternates.WaitForAlternateAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (alternate is { } next)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    fetch = await FetchStorageCandidateAsync(
                        vatp,
                        next.Uri,
                        fleet.RequestId,
                        articleId,
                        next.ServerId,
                        attempt: 2,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    return ResolveResult.Temporary();
                }
            }
            else
            {
                ArticleRetrievalLogMessages.StorageCandidateExhausted(
                    Logger,
                    articleId.ToLowerHexString(),
                    fleet.RequestId,
                    1);
            }
        }

        return MapFetch(fetch, messageId.Span, articleId);
    }

    private static async ValueTask<VatpFetchResult> FetchStorageCandidateAsync(
        IVatpArticleClient vatp,
        string cacheUri,
        Guid requestId,
        ArticleId articleId,
        int serverId,
        int attempt,
        CancellationToken cancellationToken)
    {
        ArticleRetrievalLogMessages.StorageCandidateSelected(
            Logger,
            articleId.ToLowerHexString(),
            requestId,
            serverId,
            attempt);
        try
        {
            var fetch = await vatp.FetchArticleAsync(cacheUri, requestId, articleId, cancellationToken)
                .ConfigureAwait(false);
            if (fetch.Kind != VatpFetchKind.Success)
            {
                ArticleRetrievalLogMessages.StorageCandidateFailed(
                    Logger,
                    articleId.ToLowerHexString(),
                    requestId,
                    serverId,
                    attempt,
                    fetch.Kind.ToString(),
                    fetch.AcceptedDataBytes,
                    IsAlternateEligible(fetch));
            }

            return fetch;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ArticleRetrievalLogMessages.VatpFetchFailed(Logger, ex, cacheUri, requestId);
            throw;
        }
    }

    /// <summary>
    /// A second candidate is allowed only when no DATA payload was copied and the failure
    /// is connection, remote transfer, or protocol. Every other result is terminal.
    /// </summary>
    private static bool IsAlternateEligible(VatpFetchResult fetch) =>
        fetch.AcceptedDataBytes == 0
        && fetch.Kind is VatpFetchKind.ConnectionFailure
            or VatpFetchKind.RemoteTransferFailure
            or VatpFetchKind.ProtocolFailure;

    private static bool TryBindStorageUri(string? uri, in ArticleId articleId, out string cacheUri)
    {
        cacheUri = string.Empty;
        if (string.IsNullOrWhiteSpace(uri)
            || !CacheArticleUriParser.TryParse(uri, out var parsed, out _))
        {
            return false;
        }

        if (!ArticleId.TryParseLowerHex(parsed.ArticleIdHex, out var pathId) || pathId != articleId)
        {
            return false;
        }

        cacheUri = uri;
        return true;
    }

    private static ResolveResult MapFetch(
        VatpFetchResult fetch,
        ReadOnlySpan<byte> requestedMessageId,
        ArticleId expectedArtId)
    {
        switch (fetch.Kind)
        {
            case VatpFetchKind.Success:
                break;
            case VatpFetchKind.Cancelled:
                throw new OperationCanceledException();
            default:
                ArticleRetrievalLogMessages.VatpFetchUnsuccessful(
                    Logger,
                    fetch.Kind.ToString(),
                    fetch.Error,
                    fetch.ErrorCode?.ToString());
                return ResolveResult.Temporary();
        }

        var record = fetch.Record;
        if (record.ParseStatus != ArticleParseStatus.CanonicalV1 || record.ArtSize <= 0)
        {
            ArticleRetrievalLogMessages.VatpFetchUnsuccessful(
                Logger,
                "IncompleteOrMalformedArticle",
                fetch.Error,
                fetch.ErrorCode?.ToString());
            return ResolveResult.Temporary();
        }

        if (!record.ArtId.Equals(expectedArtId)
            || !NntpArticleIdentity.MatchesRequest(record.MessageId, requestedMessageId))
        {
            ArticleRetrievalLogMessages.IdentityMismatch(Logger, fetch.Error);
            return ResolveResult.NotFound();
        }

        return ResolveResult.Success(record);
    }

    private static void TryAdmitBackFiller(NntpCommandContext context, in ArticleRecord record)
    {
        try
        {
            var messageIdText = Encoding.ASCII.GetString(record.MessageId);
            var inbound = ArticleRecordIngress.CreateQueued(
                messageIdText,
                in record,
                context.Session.ClientIdentity,
                DateTimeOffset.UtcNow,
                InboundArticleProducer.BackFiller);

            var enqueue = context.Session.ArticleIngestion.TryAdmit(inbound);
            if (enqueue == ArticleEnqueueResult.Accepted)
            {
                // Copy Message-ID octets once for History; do not invent a second History path.
                var midBytes = record.MessageId.ToArray();
                context.Session.HistoryDb?.Remember(midBytes);
                return;
            }

            ArticleRetrievalLogMessages.IngestNotAdmitted(Logger, enqueue.ToString(), messageIdText);
        }
        catch (Exception ex)
        {
            ArticleRetrievalLogMessages.IngestFailed(Logger, ex);
        }
    }

    private static ValueTask WriteNumberLookupFailureAsync(
        NntpCommandContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Session.HasSelectedGroup)
        {
            return NntpCommandReply.WriteAsync(
                context,
                Logger,
                NntpResponses.NoNewsgroupSelected,
                NntpResponseStatus.NoNewsgroupSelected,
                cancellationToken);
        }

        return NntpCommandReply.WriteAsync(
            context,
            Logger,
            NntpResponses.NoArticleWithNumber,
            NntpResponseStatus.NoArticleWithNumber,
            cancellationToken);
    }

    private static ValueTask WriteCurrentArticleFailureAsync(
        NntpCommandContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Session.HasSelectedGroup)
        {
            return NntpCommandReply.WriteAsync(
                context,
                Logger,
                NntpResponses.NoNewsgroupSelected,
                NntpResponseStatus.NoNewsgroupSelected,
                cancellationToken);
        }

        return NntpCommandReply.WriteAsync(
            context,
            Logger,
            NntpResponses.CurrentArticleNumberInvalid,
            NntpResponseStatus.CurrentArticleNumberInvalid,
            cancellationToken);
    }

    private enum ResolveKind
    {
        NotFound,
        TemporaryFailure,
        Success,
    }

    private readonly struct ResolveResult
    {
        private ResolveResult(ResolveKind kind, ArticleRecord record)
        {
            Kind = kind;
            Record = record;
        }

        public ResolveKind Kind { get; }

        public ArticleRecord Record { get; }

        public static ResolveResult NotFound() => new(ResolveKind.NotFound, default);

        public static ResolveResult Temporary() => new(ResolveKind.TemporaryFailure, default);

        public static ResolveResult Success(ArticleRecord record) => new(ResolveKind.Success, record);
    }
}
