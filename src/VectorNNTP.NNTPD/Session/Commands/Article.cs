using VectorNNTP.NNTPD.RabbitMq.ArticleWork;
using VectorNNTP.NNTPD.Session.CommandProcessor;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// ARTICLE, HEAD, BODY, and STAT as defined by RFC 3977, Sections 6.2.1–6.2.4.
/// </summary>
/// <remarks>
/// <para>
/// Article storage and retrieval are not implemented. Every syntactically valid lookup
/// therefore returns the RFC 3977 failure code for that argument form:
/// message-id → <c>430</c>, article number → <c>412</c>/<c>423</c>, omitted current
/// article → <c>412</c>/<c>420</c>. Successful retrieval codes remain
/// ARTICLE <c>220</c>, HEAD <c>221</c>, BODY <c>222</c>, STAT <c>223</c> and are not
/// emitted. Error replies are single-line; no multiline terminator is sent.
/// </para>
/// <para>
/// ARTICLE <c>&lt;message-id&gt;</c> invokes article-work RPC when an
/// <see cref="IArticleWorkRpcClient"/> is present. This phase maps every RPC result,
/// including success, to the existing <c>430</c> reply because article bytes are not
/// retrieved yet. HEAD, BODY, and STAT do not invoke RPC in this phase.
/// ARTICLE with an article number still cannot resolve a Message-ID (OverDB is absent)
/// and keeps the existing number-lookup failure codes.
/// </para>
/// <para>
/// GROUP selection does not invent a current article number from catalogue water marks.
/// An unsuccessful lookup MUST NOT change the selected group or current article
/// (RFC 3977 §6.2.1.2). There is no current-article pointer yet, so the omitted form
/// after a successful GROUP is <c>420</c> (invalid current article), not <c>423</c>
/// (a previously valid number whose article is gone).
/// </para>
/// <para>
/// TAKETHIS → spool ingestion is an ingress / application boundary — not an article
/// repository for these commands.
/// </para>
/// </remarks>
internal static class Article
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(Article));

    /// <summary>Handles <c>ARTICLE</c> (RFC 3977, Section 6.2.1). Future success code is <c>220</c>.</summary>
    public static ValueTask HandleArticleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "ARTICLE", ExecuteArticleAsync, cancellationToken);

    /// <summary>Handles <c>HEAD</c> (RFC 3977, Section 6.2.2). Future success code is <c>221</c>.</summary>
    public static ValueTask HandleHeadAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "HEAD", ExecuteAsync, cancellationToken);

    /// <summary>Handles <c>BODY</c> (RFC 3977, Section 6.2.3). Future success code is <c>222</c>.</summary>
    public static ValueTask HandleBodyAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "BODY", ExecuteAsync, cancellationToken);

    /// <summary>Handles <c>STAT</c> (RFC 3977, Section 6.2.4). Future success code is <c>223</c>.</summary>
    public static ValueTask HandleStatAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "STAT", ExecuteAsync, cancellationToken);

    private static async ValueTask ExecuteArticleAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        var argument = context.ArgumentSpan;
        if (argument.IsEmpty)
        {
            await WriteCurrentArticleFailureAsync(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (argument[0] == (byte)'<')
        {
            await LookupMessageIdAsync(context, cancellationToken).ConfigureAwait(false);
            await NntpCommandReply.WriteAsync(
                context,
                Logger,
                NntpResponses.NoArticleWithMessageId,
                NntpResponseStatus.NoArticleWithMessageId,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteNumberLookupFailureAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private static ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        var argument = context.ArgumentSpan;
        if (argument.IsEmpty)
        {
            return WriteCurrentArticleFailureAsync(context, cancellationToken);
        }

        if (argument[0] == (byte)'<')
        {
            return NntpCommandReply.WriteAsync(
                context,
                Logger,
                NntpResponses.NoArticleWithMessageId,
                NntpResponseStatus.NoArticleWithMessageId,
                cancellationToken);
        }

        return WriteNumberLookupFailureAsync(context, cancellationToken);
    }

    private static async ValueTask LookupMessageIdAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        var rpc = context.Session.ArticleWorkRpc;
        if (rpc is null)
        {
            return;
        }

        var messageId = context.ArgumentMemory;
        try
        {
            _ = await rpc.LookupByMessageIdAsync(messageId, cancellationToken).ConfigureAwait(false);
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
                System.Text.Encoding.ASCII.GetString(messageId.Span));
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
}
