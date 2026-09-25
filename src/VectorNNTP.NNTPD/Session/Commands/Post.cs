using System.Text;
using VectorNNTP.NNTPD.ArticleIngestion;
using VectorNNTP.NNTPD.History;
using VectorNNTP.NNTPD.Session.CommandProcessor;
using VectorNNTP.NNTPD.Session.Commands.Posting;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// POST command as defined by RFC 3977, Section 6.3.1.
/// </summary>
/// <remarks>
/// Not pipelined. First-stage <c>440</c> does not consume an article. After <c>340</c>
/// the article is streamed from the session PipeReader into one stuffed output
/// buffer (client headers write-through, server-owned headers at the header/body
/// boundary, body copied with stuffing preserved). History Peek,
/// <see cref="IArticleIngestionQueue.TryAdmit"/>, Remember, and <c>240</c> stay
/// after the terminator. Downstream spool/worker processing is not awaited.
/// </remarks>
internal static class Post
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(Post));

    /// <summary>Handles <c>POST</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "POST", ExecuteAsync, cancellationToken);

    private static async ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        if (!context.Session.Authorization.PostingPermitted)
        {
            await WriteStatusAsync(
                    context,
                    NntpResponses.PostingProhibited,
                    NntpResponseStatus.PostingProhibited,
                    "posting not permitted",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await NntpCommandReply.WriteAsync(
                context,
                Logger,
                NntpResponses.PostSendArticle,
                NntpResponseStatus.PostSendArticle,
                cancellationToken)
            .ConfigureAwait(false);

        StreamingPostReadResult read;
        try
        {
            read = await StreamingPostArticleReader
                .ReadAsync(
                    context.Connection.Input,
                    new StreamingPostReadOptions
                    {
                        MaxArticleSize = context.Session.MaxArticleSize,
                        Time = context.Session.Time,
                        NewsgroupPolicy = context.Session.NewsgroupPostingPolicy,
                        InjectionIdentity = context.Session.InjectionIdentity,
                        ClientIdentity = context.Session.ClientIdentity,
                        MailComplaintsTo = context.Session.MailComplaintsTo,
                        TraceProtector = context.Session.PostingTraceProtector,
                        ControlCancelPermitted = context.Session.Authorization.ControlCancelPermitted,
                        AuthenticatedUsername = context.Session.Authentication.IsAuthenticated
                            ? context.Session.Authentication.Username
                            : null,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
            || context.Connection.ConnectionClosed.IsCancellationRequested)
        {
            return;
        }

        if (read.Status == StreamingPostReadStatus.Incomplete)
        {
            return;
        }

        if (read.Status != StreamingPostReadStatus.Completed)
        {
            var failure = read.Status == StreamingPostReadStatus.TooLarge
                ? new PostingFailure(PostingFailureCategory.ArticleTooLarge, "max article size exceeded")
                : read.Failure;
            await RejectAsync(
                    context,
                    failure,
                    read.MessageId,
                    FormatGroups(read.Newsgroups),
                    read.Status == StreamingPostReadStatus.TooLarge
                        ? context.Session.MaxArticleSize + 1
                        : read.DestuffedSize,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var messageIdBytes = Encoding.ASCII.GetBytes(read.MessageId!);
        var history = context.Session.HistoryDb;
        if (history is not null)
        {
            var peek = await history.PeekAsync(messageIdBytes, cancellationToken).ConfigureAwait(false);
            if (peek == HistoryLookupResult.Seen)
            {
                await RejectAsync(
                        context,
                        new PostingFailure(PostingFailureCategory.DuplicateArticle, "Message-ID already known"),
                        read.MessageId,
                        FormatGroups(read.Newsgroups),
                        read.DestuffedSize,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (peek == HistoryLookupResult.Unavailable)
            {
                await RejectAsync(
                        context,
                        new PostingFailure(PostingFailureCategory.PersistenceFailure, "history unavailable"),
                        read.MessageId,
                        FormatGroups(read.Newsgroups),
                        read.DestuffedSize,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }

        if (cancellationToken.IsCancellationRequested
            || context.Connection.ConnectionClosed.IsCancellationRequested)
        {
            return;
        }

        var inbound = new InboundArticle(
            read.MessageId!,
            read.Wire,
            context.Session.ClientIdentity,
            read.InjectionUtc,
            structured: null,
            InboundArticleProducer.Post);

        var enqueue = context.Session.ArticleIngestion.TryAdmit(inbound);
        if (enqueue != ArticleEnqueueResult.Accepted)
        {
            await RejectAsync(
                    context,
                    new PostingFailure(PostingFailureCategory.PersistenceFailure, enqueue.ToString()),
                    read.MessageId,
                    FormatGroups(read.Newsgroups),
                    read.Wire.Length,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        history?.Remember(messageIdBytes);
        PostLogMessages.Accepted(
            Logger,
            NntpCommandLogFormat.Client(context.Session),
            read.MessageId!,
            FormatGroups(read.Newsgroups),
            read.Wire.Length);

        await WriteStatusAsync(
                context,
                NntpResponses.ArticleReceivedOk,
                NntpResponseStatus.ArticleReceivedOk,
                "accepted",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask RejectAsync(
        NntpCommandContext context,
        PostingFailure failure,
        string? messageId,
        string? newsgroups,
        int size,
        CancellationToken cancellationToken)
    {
        PostLogMessages.Rejected(
            Logger,
            NntpCommandLogFormat.Client(context.Session),
            failure.Category,
            messageId ?? "-",
            newsgroups ?? "-",
            size,
            failure.Detail);
        await WriteFailedAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private static ValueTask WriteFailedAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        WriteStatusAsync(
            context,
            NntpResponses.PostingFailed,
            NntpResponseStatus.PostingFailed,
            "posting failed",
            cancellationToken);

    private static ValueTask WriteStatusAsync(
        NntpCommandContext context,
        ReadOnlyMemory<byte> wire,
        string statusLine,
        string detail,
        CancellationToken cancellationToken)
    {
        context.StatusLine = statusLine;
        context.CompletionDetail = detail;
        return context.Response.WriteLineAsync(wire, cancellationToken);
    }

    private static string FormatGroups(string[] groups) =>
        groups.Length == 0 ? "-" : string.Join(',', groups);
}
