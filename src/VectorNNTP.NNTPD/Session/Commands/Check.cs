using VectorNNTP.NNTPD.History;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// CHECK command as defined by RFC 4644, Section 2.4.
/// </summary>
/// <remarks>
/// Advises whether the server wants an article (STREAMING) using HistoryDB
/// <see cref="IHistoryDb.PeekAsync"/> (no miss reservation):
/// local memory hit → 438; Redis hit → 438 and warm local memory; double miss → 238
/// without recording the identifier. Redis infrastructure failure returns 431
/// (try again later) and is never treated as a 238 miss.
/// The Message-ID is copied from session scratch into one owned wire buffer; it is not
/// converted to a string for response construction. A completed local-hit lookup
/// writes the 438 response without an extra async state machine.
/// Consecutive authorized CHECK commands may overlap Redis lookups in the per-session
/// <see cref="CheckPipeline"/> (fixed depth <see cref="CheckPipeline.Depth"/>).
/// Every response passes through the pipeline emit gate and is enqueued in send order.
/// A slot stays occupied until the writer accepts the line.
/// </remarks>
internal static class Check
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(Check));

    /// <summary>Handles <c>CHECK</c> when dispatched serially (gates / non-pipeline fallback).</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "CHECK", ExecuteAsync, cancellationToken);

    /// <summary>
    /// Looks up <paramref name="messageId"/> without composing a response or recording a miss.
    /// </summary>
    internal static ValueTask<HistoryLookupResult> LookupAsync(
        NntpSession session,
        ReadOnlyMemory<byte> messageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.HistoryDb is not { } history)
        {
            return new ValueTask<HistoryLookupResult>(HistoryLookupResult.Unseen);
        }

        return history.PeekAsync(messageId, cancellationToken);
    }

    /// <summary>Composes the RFC 4644 CHECK wire line for <paramref name="result"/>.</summary>
    internal static ReadOnlyMemory<byte> Compose(HistoryLookupResult result, ReadOnlySpan<byte> messageId)
    {
        ReadOnlyMemory<byte> prefix;
        ReadOnlyMemory<byte> suffix;
        switch (result)
        {
            case HistoryLookupResult.Seen:
                prefix = NntpResponses.CheckNotWantedPrefix;
                suffix = NntpResponses.CheckNotWantedSuffix;
                break;
            case HistoryLookupResult.Unavailable:
                prefix = NntpResponses.CheckTryLaterPrefix;
                suffix = NntpResponses.CheckTryLaterSuffix;
                break;
            default:
                prefix = NntpResponses.CheckPrefix;
                suffix = NntpResponses.CheckSuffix;
                break;
        }

        return NntpResponseCompose.Concat(prefix.Span, messageId, suffix.Span);
    }

    private static ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        var messageId = context.ArgumentMemory;
        var lookup = LookupAsync(context.Session, messageId, cancellationToken);
        if (lookup.IsCompletedSuccessfully)
        {
            return WriteResultAsync(context, lookup.Result, messageId, cancellationToken);
        }

        return AwaitLookupAndWriteAsync(context, lookup, messageId, cancellationToken);
    }

    private static async ValueTask AwaitLookupAndWriteAsync(
        NntpCommandContext context,
        ValueTask<HistoryLookupResult> lookup,
        ReadOnlyMemory<byte> messageId,
        CancellationToken cancellationToken)
    {
        var result = await lookup.ConfigureAwait(false);
        await WriteResultAsync(context, result, messageId, cancellationToken).ConfigureAwait(false);
    }

    private static ValueTask WriteResultAsync(
        NntpCommandContext context,
        HistoryLookupResult result,
        ReadOnlyMemory<byte> messageId,
        CancellationToken cancellationToken)
    {
        var owned = Compose(result, messageId.Span);
        return context.Response.WriteLineAsync(owned, cancellationToken);
    }
}
