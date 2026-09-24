using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.History;
using VectorNNTP.NNTPD.Session.Framing;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// IHAVE command as defined by RFC 3977, Section 6.3.2.
/// </summary>
/// <remarks>
/// Not pipelined. Serial two-stage exchange: HistoryDB peek → non-blocking
/// Transit queue probe → 335/435/436 → raw article receive (frame terminator,
/// own stuffed wire) → non-blocking <see cref="IArticleIngestionQueue.TryAdmit"/>
/// → 235/436/437. IHAVE never waits for queue memory. TAKETHIS is not used and
/// is not modified. Destuff, classification, and <see cref="Article"/>
/// construction occur in <see cref="IhaveArticleInterpreter"/>.
/// </remarks>
internal static class IHave
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(IHave));

    /// <summary>Handles <c>IHAVE</c> (RFC 3977 §6.3.2).</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "IHAVE", ExecuteAsync, cancellationToken);

    private static async ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        var messageId = context.ArgumentMemory;
        var history = context.Session.HistoryDb;
        var peek = history is null
            ? HistoryLookupResult.Unseen
            : await history.PeekAsync(messageId, cancellationToken).ConfigureAwait(false);

        if (peek == HistoryLookupResult.Seen)
        {
            await context.Response.WriteLineAsync(NntpResponses.IhaveNotWanted, cancellationToken)
                .ConfigureAwait(false);
            context.CompletionDetail = "not wanted";
            return;
        }

        if (peek == HistoryLookupResult.Unavailable)
        {
            await context.Response.WriteLineAsync(NntpResponses.IhaveTryLater, cancellationToken)
                .ConfigureAwait(false);
            context.CompletionDetail = "try later";
            return;
        }

        var queue = context.Session.ArticleIngestion;
        if (!queue.TryProbeCapacity())
        {
            IHaveAdmissionLog.BudgetExhausted(Logger, queue);
            await context.Response.WriteLineAsync(NntpResponses.IhaveTryLater, cancellationToken)
                .ConfigureAwait(false);
            context.CompletionDetail = "queue full";
            return;
        }

        await context.Response.WriteLineAsync(NntpResponses.IhaveSendArticle, cancellationToken)
            .ConfigureAwait(false);

        IHaveArticleReadResult read;
        try
        {
            read = await IHaveArticleReader
                .ReadAsync(context.Connection.Input, queue.MaxArticleBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
            || context.Connection.ConnectionClosed.IsCancellationRequested)
        {
            return;
        }

        if (read.Status == NntpMultilineReadStatus.Incomplete)
        {
            return;
        }

        if (read.Status == NntpMultilineReadStatus.TooLarge)
        {
            await context.Response.WriteLineAsync(NntpResponses.IhaveRejected, cancellationToken)
                .ConfigureAwait(false);
            context.CompletionDetail = "rejected too large";
            return;
        }

        var messageIdText = System.Text.Encoding.ASCII.GetString(messageId.Span);
        var inbound = new InboundArticle(
            messageIdText,
            read.Payload,
            context.Session.ClientIdentity,
            DateTimeOffset.UtcNow,
            structured: null,
            InboundArticleProducer.IHave);

        var enqueue = queue.TryAdmit(inbound);
        if (enqueue == ArticleEnqueueResult.Rejected)
        {
            await context.Response.WriteLineAsync(NntpResponses.IhaveRejected, cancellationToken)
                .ConfigureAwait(false);
            context.CompletionDetail = "rejected exceeds queue budget";
            return;
        }

        if (enqueue != ArticleEnqueueResult.Accepted)
        {
            if (enqueue == ArticleEnqueueResult.Full)
            {
                IHaveAdmissionLog.BudgetExhausted(Logger, queue);
            }

            await context.Response.WriteLineAsync(NntpResponses.IhaveTransferFailed, cancellationToken)
                .ConfigureAwait(false);
            context.CompletionDetail = enqueue == ArticleEnqueueResult.Full
                ? "queue full"
                : "queue unavailable";
            return;
        }

        history?.Remember(messageId);
        IHaveLogMessages.Received(
            Logger,
            NntpCommandLogFormat.Client(context.Session),
            messageIdText,
            read.Metrics.ArticleSize,
            read.Metrics.PipeReads,
            read.Metrics.ReceiveElapsed.TotalMilliseconds,
            Queued: true);

        await context.Response.WriteLineAsync(NntpResponses.IhaveTransferredOk, cancellationToken)
            .ConfigureAwait(false);
        context.CompletionDetail = "accepted";
    }
}
