using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.History;
using VectorNNTP.NNTPD.Session.CommandProcessor;
using VectorNNTP.NNTPD.Session.Framing;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// IHAVE command as defined by RFC 3977, Section 6.3.2.
/// </summary>
/// <remarks>
/// Not pipelined. Serial two-stage exchange: HistoryDB peek → non-blocking
/// Transit queue probe → 335/435/436 → raw article receive (frame terminator,
/// own stuffed wire) → destuff + <c>ArticleRecordFactory</c> → non-blocking
/// <see cref="IArticleIngestionQueue.TryAdmit"/> → 235/436/437. IHAVE never
/// waits for queue memory. TAKETHIS is not used and is not modified. Common
/// owns article parse/materialize. IHAVE-specific behaviour is History peek,
/// non-blocking probe/admit, and 335/235/435/436/437 timing. The command
/// Message-ID is used for History and is not matched against the article
/// Message-ID (RFC 3977 §6.3.2 permits a mismatch). Incomplete articles that
/// cannot become CanonicalV1 are rejected with 437 after 335.
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
            await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.IhaveNotWanted,
                    NntpResponseStatus.IhaveNotWanted,
                    cancellationToken)
                .ConfigureAwait(false);
            context.CompletionDetail = "not wanted";
            return;
        }

        if (peek == HistoryLookupResult.Unavailable)
        {
            await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.IhaveTryLater,
                    NntpResponseStatus.IhaveTryLater,
                    cancellationToken)
                .ConfigureAwait(false);
            context.CompletionDetail = "try later";
            return;
        }

        var queue = context.Session.ArticleIngestion;
        if (!queue.TryProbeCapacity())
        {
            IHaveAdmissionLog.BudgetExhausted(Logger, queue);
            await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.IhaveTryLater,
                    NntpResponseStatus.IhaveTryLater,
                    cancellationToken)
                .ConfigureAwait(false);
            context.CompletionDetail = "queue full";
            return;
        }

        await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.IhaveSendArticle,
                    NntpResponseStatus.IhaveSendArticle,
                    cancellationToken)
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
            const string tooLarge = "rejected too large";
            IngressNewsEvents.TryWriteRejected(
                context.Session,
                CommandMessageId(messageId),
                437,
                IngressNewsReasons.ArticleTooLarge,
                read.Payload.Length);
            await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.IhaveRejected,
                    NntpResponseStatus.IhaveRejected,
                    cancellationToken)
                .ConfigureAwait(false);
            context.CompletionDetail = tooLarge;
            return;
        }

        var created = ArticleRecordIngress.TryCreateFromStuffedWire(
            context.Session.ArticleParser,
            read.Payload,
            queue.MaxArticleBytes);
        if (!created.IsAccepted)
        {
            var recordReject = created.ParseFailure != VectorNNTP.Common.Articles.Parsing.NntpArticleParseFailureCode.None
                ? "rejected article record " + created.ParseFailure
                : "rejected article record " + created.MaterializeFailure;
            IngressNewsEvents.TryWriteRejected(
                context.Session,
                CommandMessageId(messageId),
                437,
                IngressNewsReasons.ForArticleRecord(in created),
                read.Payload.Length);
            await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.IhaveRejected,
                    NntpResponseStatus.IhaveRejected,
                    cancellationToken)
                .ConfigureAwait(false);
            context.CompletionDetail = recordReject;
            return;
        }

        var messageIdText = System.Text.Encoding.ASCII.GetString(messageId.Span);
        var inbound = ArticleRecordIngress.CreateQueued(
            messageIdText,
            created.Record,
            context.Session.ClientIdentity,
            DateTimeOffset.UtcNow,
            InboundArticleProducer.IHave,
            IngressNewsEvents.SnapshotInboundFeed(context.Session));

        if (IngressNewsDisposition.IsUncarriedWantTrashRejection(
                inbound,
                context.Session.Transit,
                context.Session.NewsgroupCatalogue,
                out var uncarriedReason))
        {
            IngressNewsEvents.TryWriteRejected(
                context.Session,
                messageIdText,
                437,
                uncarriedReason,
                inbound.Payload.Length);
            await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.IhaveRejected,
                    NntpResponseStatus.IhaveRejected,
                    cancellationToken)
                .ConfigureAwait(false);
            context.CompletionDetail = IngressNewsEvents.NewsgroupNotCarried;
            return;
        }

        var enqueue = queue.TryAdmit(inbound);
        if (enqueue == ArticleEnqueueResult.Rejected)
        {
            const string budget = "rejected exceeds queue budget";
            IngressNewsEvents.TryWriteRejected(
                context.Session,
                messageIdText,
                437,
                IngressNewsReasons.QueueCapacityExceeded,
                inbound.Payload.Length);
            await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.IhaveRejected,
                    NntpResponseStatus.IhaveRejected,
                    cancellationToken)
                .ConfigureAwait(false);
            context.CompletionDetail = budget;
            return;
        }

        if (enqueue != ArticleEnqueueResult.Accepted)
        {
            if (enqueue == ArticleEnqueueResult.Full)
            {
                IHaveAdmissionLog.BudgetExhausted(Logger, queue);
            }

            await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.IhaveTransferFailed,
                    NntpResponseStatus.IhaveTransferFailed,
                    cancellationToken)
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

        await NntpCommandReply.WriteAsync(
                    context,
                    Logger,
                    NntpResponses.IhaveTransferredOk,
                    NntpResponseStatus.IhaveTransferredOk,
                    cancellationToken)
            .ConfigureAwait(false);
        context.CompletionDetail = "accepted";
    }

    private static string CommandMessageId(ReadOnlyMemory<byte> messageId) =>
        System.Text.Encoding.ASCII.GetString(messageId.Span);
}
