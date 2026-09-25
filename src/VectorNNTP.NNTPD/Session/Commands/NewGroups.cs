namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// NEWGROUPS command as defined by RFC 3977, Section 7.3.
/// </summary>
/// <remarks>
/// Lists newsgroups created after a given date/time. Deliberate placeholder until group inventory is implemented.
/// </remarks>
internal static class NewGroups
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(NewGroups));

    /// <summary>Handles <c>NEWGROUPS</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(
            Logger,
            context,
            "NEWGROUPS",
            static (ctx, ct) => NntpCommandNotImplemented.HandleAsync(ctx, Logger, ct),
            cancellationToken,
            successDetail: "not implemented");
}
