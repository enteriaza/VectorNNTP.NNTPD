namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// CHECK command as defined by RFC 4644, Section 2.4.
/// </summary>
/// <remarks>
/// Advises whether the server wants an article (STREAMING). This is a deliberate stub: a
/// syntactically valid <c>CHECK message-id</c> always returns
/// <c>238 message-id send article to be transferred</c>. Duplicate detection, deferral, and
/// TAKETHIS coupling are not implemented.
/// </remarks>
internal static class Check
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(Check));

    /// <summary>Handles <c>CHECK</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "CHECK", ExecuteAsync, cancellationToken);

    private static ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        if (context.Arguments.Count != 1 || !NntpMessageId.IsWellFormed(context.Arguments[0]))
        {
            return context.Response.WriteLineAsync(NntpReplyCodes.SyntaxError, "Syntax error", cancellationToken);
        }

        var messageId = context.Arguments[0];
        return context.Response.WriteLineAsync(
            NntpReplyCodes.SendArticleToBeTransferred,
            messageId + " send article to be transferred",
            cancellationToken);
    }
}
