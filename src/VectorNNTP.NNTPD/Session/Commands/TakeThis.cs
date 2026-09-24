using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.Session.Framing;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// TAKETHIS command as defined by RFC 4644, Section 2.5.
/// </summary>
/// <remarks>
/// <para>
/// Syntax: <c>TAKETHIS message-id</c> followed immediately by an NNTP multiline article.
/// Responses: <c>239 message-id</c> (accepted into ingestion) or <c>439 message-id</c>
/// (permanent reject). Temporary infrastructure failure uses <c>400</c> and closes the connection.
/// </para>
/// <para>
/// Critical path: parse → consume article (STREAM: framed wire copy; READER fallback:
/// <see cref="NntpMultilineDataReader"/> destuff) → enqueue → enqueue 239/439 → return.
/// Disk persistence and outbound network delivery of the status line are not awaited
/// (<see cref="NntpResponseWriter.EnqueueLineAsync(ReadOnlyMemory{byte}, CancellationToken)"/>).
/// </para>
/// </remarks>
internal static class TakeThis
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(TakeThis));

    /// <summary>Handles <c>TAKETHIS</c> (RFC 4644 §2.5).</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "TAKETHIS", ExecuteAsync, cancellationToken);

    private static async ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // Syntax already validated by NntpCommandParser. String is the ingest boundary only.
        var messageId = System.Text.Encoding.ASCII.GetString(context.ArgumentSpan);
        var queue = context.Session.ArticleIngestion;

        // Always consume the following multiline block so pipelined bytes stay synchronized,
        // even when the message-id is malformed — unless the session scanner already did.
        NntpMultilineReadResult article;
        try
        {
            if (context.PreReadArticle is { } preRead)
            {
                article = preRead;
            }
            else
            {
                article = await NntpMultilineDataReader
                    .ReadArticleAsync(context.Connection.Input, queue.MaxArticleBytes, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
            || context.Connection.ConnectionClosed.IsCancellationRequested)
        {
            // Peer gone mid-article — do not enqueue a partial article; do not respond.
            return;
        }

        if (article.Status == NntpMultilineReadStatus.Incomplete)
        {
            // Connection ended before terminator; no response, no enqueue.
            return;
        }

        if (article.Status == NntpMultilineReadStatus.TooLarge)
        {
            await EnqueueTransferReplyAsync(
                    context,
                    NntpResponses.TransferRejectedPrefix,
                    cancellationToken)
                .ConfigureAwait(false);
            context.CompletionDetail = "rejected too large";
            return;
        }

        if (!queue.IsAccepting)
        {
            await FailTemporaryAsync(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        var inbound = new InboundArticle(
            messageId,
            article.Payload,
            context.Session.ClientIdentity,
            DateTimeOffset.UtcNow);

        ArticleEnqueueResult enqueue;
        try
        {
            enqueue = await queue.EnqueueAsync(inbound, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
            || context.Connection.ConnectionClosed.IsCancellationRequested)
        {
            return;
        }

        if (enqueue == ArticleEnqueueResult.Unavailable)
        {
            await FailTemporaryAsync(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (enqueue == ArticleEnqueueResult.Rejected)
        {
            await EnqueueTransferReplyAsync(
                    context,
                    NntpResponses.TransferRejectedPrefix,
                    cancellationToken)
                .ConfigureAwait(false);
            context.CompletionDetail = "rejected exceeds queue budget";
            return;
        }

        // 239 means accepted into the ingestion pipeline — not yet persisted to disk.
        await EnqueueTransferReplyAsync(
                context,
                NntpResponses.ArticleTransferredOkPrefix,
                cancellationToken)
            .ConfigureAwait(false);
        context.CompletionDetail = "accepted";
    }

    /// <summary>
    /// Copies prefix + session-scratch Message-ID + CRLF into one owned buffer, then enqueues
    /// it. Scratch must not be given to the TX pump: the next command overwrites it.
    /// </summary>
    private static ValueTask EnqueueTransferReplyAsync(
        NntpCommandContext context,
        ReadOnlyMemory<byte> prefix,
        CancellationToken cancellationToken)
    {
        var owned = NntpResponseCompose.Concat(
            prefix.Span,
            context.ArgumentSpan,
            NntpResponses.Crlf.Span);
        return context.Response.EnqueueLineAsync(owned, cancellationToken);
    }

    private static async ValueTask FailTemporaryAsync(
        NntpCommandContext context,
        CancellationToken cancellationToken)
    {
        // RFC 4644 §2.5: temporary error that does not reject the article → 400 + close.
        try
        {
            await context.Response
                .WriteLineAsync(NntpResponses.ServiceTemporarilyUnavailable, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (
            cancellationToken.IsCancellationRequested
            || context.Connection.ConnectionClosed.IsCancellationRequested
            || ex is ObjectDisposedException or InvalidOperationException)
        {
            // Peer already gone / pipe completed.
        }

        context.Session.RequestClose();
        context.CompletionDetail = "temporary failure";
        CommandLogMessages.TakeThisTemporaryFailure(
            Logger,
            NntpCommandLogFormat.Client(context.Session));
    }
}
