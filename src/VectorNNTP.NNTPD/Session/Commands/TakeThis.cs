using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.History;
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
/// STREAM path: the session RX task starts HistoryDB peek at the Message-ID, frames
/// the article with <see cref="IHaveArticleReader"/> (one owned stuffed-wire buffer,
/// terminator omitted, no destuff), attaches that buffer to
/// <see cref="TakeThisPipeline"/>, and returns. Peek / enqueue / Remember / ordered
/// 239/439 run on pipeline completion workers, not on the RX stack. Depth bounds
/// how many owned articles stay in flight. TAKETHIS responses flush immediately
/// (no coalesce batch).
/// </para>
/// <para>
/// MODE READER fallback still destuffs via <see cref="NntpMultilineDataReader"/> and
/// overlaps HistoryDB peek with that receive.
/// </para>
/// </remarks>
internal static class TakeThis
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(TakeThis));

    /// <summary>Handles <c>TAKETHIS</c> (RFC 4644 §2.5) on the serial (non-pipeline) path.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "TAKETHIS", ExecuteAsync, cancellationToken);

    /// <summary>HistoryDB peek without miss reservation (same contract as IHAVE).</summary>
    internal static ValueTask<HistoryLookupResult> PeekAsync(
        NntpSession session,
        ReadOnlyMemory<byte> messageId,
        CancellationToken cancellationToken)
    {
        if (session.HistoryDb is not { } history)
        {
            return new ValueTask<HistoryLookupResult>(HistoryLookupResult.Unseen);
        }

        return history.PeekAsync(messageId, cancellationToken);
    }

    internal static void WriteCompletion(
        ILogger logger,
        NntpSession session,
        long startedTimestamp,
        string? detail = null) =>
        NntpCommandExecution.WriteCompletion(
            logger,
            session,
            "TAKETHIS",
            System.Diagnostics.Stopwatch.GetElapsedTime(startedTimestamp),
            detail);

    internal static async ValueTask FailTemporaryAsync(
        NntpSession session,
        NntpResponseWriter response,
        CancellationToken cancellationToken)
    {
        try
        {
            await response
                .WriteLineAsync(NntpResponses.ServiceTemporarilyUnavailable, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (
            cancellationToken.IsCancellationRequested
            || session.Connection.ConnectionClosed.IsCancellationRequested
            || ex is ObjectDisposedException or InvalidOperationException)
        {
        }

        session.RequestClose();
        CommandLogMessages.TakeThisTemporaryFailure(
            Logger,
            NntpCommandLogFormat.Client(session));
    }

    private static async ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        var messageIdBytes = context.ArgumentSpan.ToArray();
        var lookup = PeekAsync(context.Session, messageIdBytes, cancellationToken);
        var queue = context.Session.ArticleIngestion;

        NntpMultilineReadStatus status;
        ReadOnlyMemory<byte> payload;
        try
        {
            if (context.PreReadArticle is { } preRead)
            {
                status = preRead.Status;
                payload = preRead.Payload;
            }
            else if (context.Session.ReceiveStrategy == NntpReceiveStrategy.StreamDataPlane)
            {
                var read = await IHaveArticleReader
                    .ReadAsync(context.Connection.Input, queue.MaxArticleBytes, cancellationToken)
                    .ConfigureAwait(false);
                status = read.Status;
                payload = read.Payload;
            }
            else
            {
                var article = await NntpMultilineDataReader
                    .ReadArticleAsync(context.Connection.Input, queue.MaxArticleBytes, cancellationToken)
                    .ConfigureAwait(false);
                status = article.Status;
                payload = article.Payload;
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
            || context.Connection.ConnectionClosed.IsCancellationRequested)
        {
            return;
        }

        if (status == NntpMultilineReadStatus.Incomplete)
        {
            return;
        }

        HistoryLookupResult peek;
        try
        {
            peek = await lookup.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
            || context.Connection.ConnectionClosed.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            peek = HistoryLookupResult.Unavailable;
        }

        if (status == NntpMultilineReadStatus.TooLarge)
        {
            await EnqueueTransferReplyAsync(
                    context,
                    NntpResponses.TransferRejectedPrefix,
                    cancellationToken)
                .ConfigureAwait(false);
            context.CompletionDetail = "rejected too large";
            return;
        }

        if (peek == HistoryLookupResult.Unavailable || !queue.IsAccepting)
        {
            await FailTemporaryAsync(context.Session, context.Response, cancellationToken).ConfigureAwait(false);
            context.CompletionDetail = "temporary failure";
            return;
        }

        if (peek == HistoryLookupResult.Seen)
        {
            await EnqueueTransferReplyAsync(
                    context,
                    NntpResponses.ArticleTransferredOkPrefix,
                    cancellationToken)
                .ConfigureAwait(false);
            context.CompletionDetail = "accepted duplicate";
            return;
        }

        var messageId = System.Text.Encoding.ASCII.GetString(messageIdBytes);
        var inbound = new InboundArticle(
            messageId,
            payload,
            context.Session.ClientIdentity,
            DateTimeOffset.UtcNow,
            structured: null,
            InboundArticleProducer.TakeThis);

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
            await FailTemporaryAsync(context.Session, context.Response, cancellationToken).ConfigureAwait(false);
            context.CompletionDetail = "temporary failure";
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

        context.Session.HistoryDb?.Remember(messageIdBytes);
        await EnqueueTransferReplyAsync(
                context,
                NntpResponses.ArticleTransferredOkPrefix,
                cancellationToken)
            .ConfigureAwait(false);
        context.CompletionDetail = "accepted";
    }

    private static ValueTask EnqueueTransferReplyAsync(
        NntpCommandContext context,
        ReadOnlyMemory<byte> prefix,
        CancellationToken cancellationToken)
    {
        var owned = NntpResponseCompose.Concat(
            prefix.Span,
            context.ArgumentSpan,
            NntpResponses.Crlf.Span);
        return context.Response.EnqueueLineImmediateAsync(owned, cancellationToken);
    }
}
