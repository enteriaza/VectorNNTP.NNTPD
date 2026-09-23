namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// LISTGROUP command as defined by RFC 3977, Section 6.1.2.
/// </summary>
/// <remarks>
/// Selects a group and lists article numbers. Deliberate placeholder until group storage is implemented.
/// </remarks>
internal static class ListGroup
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(ListGroup));

    /// <summary>Handles <c>LISTGROUP</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(
            Logger,
            context,
            "LISTGROUP",
            static (ctx, ct) => NntpCommandNotImplemented.HandleAsync(ctx, ct),
            cancellationToken,
            successDetail: "not implemented");
}
