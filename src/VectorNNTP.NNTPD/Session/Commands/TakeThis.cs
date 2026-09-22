using Microsoft.Extensions.Logging;
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
/// Critical path: parse → read/unstuff article → enqueue → enqueue 239/439 → return to the
/// command loop. Disk persistence and outbound network delivery of the status line are not
/// awaited on this path (<see cref="NntpResponseWriter.EnqueueLineAsync"/>).
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
        if (context.Arguments.Count != 1)
        {
            await context.Response
                .WriteLineAsync(NntpReplyCodes.SyntaxError, "Syntax error", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var messageId = context.Arguments[0];
        var queue = context.Session.ArticleIngestion;

        // Always consume the following multiline block so pipelined bytes stay synchronized,
        // even when the message-id is malformed.
        NntpMultilineReadResult article;
        try
        {
            article = await NntpMultilineDataReader
                .ReadArticleAsync(context.Connection.Input, queue.MaxArticleBytes, cancellationToken)
                .ConfigureAwait(false);
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

        if (!NntpMessageId.IsWellFormed(messageId))
        {
            await context.Response
                .WriteLineAsync(NntpReplyCodes.SyntaxError, "Syntax error", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (article.Status == NntpMultilineReadStatus.TooLarge)
        {
            await context.Response
                .EnqueueLineAsync(NntpReplyCodes.TransferRejected, messageId, cancellationToken)
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

        // 239 means accepted into the ingestion pipeline — not yet persisted to disk.
        await context.Response
            .EnqueueLineAsync(NntpReplyCodes.ArticleTransferredOk, messageId, cancellationToken)
            .ConfigureAwait(false);
        context.CompletionDetail = "accepted";
    }

    private static async ValueTask FailTemporaryAsync(
        NntpCommandContext context,
        CancellationToken cancellationToken)
    {
        // RFC 4644 §2.5: temporary error that does not reject the article → 400 + close.
        try
        {
            await context.Response
                .WriteLineAsync(
                    NntpReplyCodes.ServiceTemporarilyUnavailable,
                    "Service temporarily unavailable",
                    cancellationToken)
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
        Logger.LogWarning(
            "[{Client}] TAKETHIS temporary failure; closing connection.",
            NntpCommandLogFormat.Client(context.Session));
    }
}
