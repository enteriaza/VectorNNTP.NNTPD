namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// LIST command as defined by RFC 3977, Section 7.6.1 (see also RFC 6048).
/// </summary>
/// <remarks>
/// Returns information lists for supported keywords (ACTIVE, NEWSGROUPS, HEADERS, MOTD,
/// OVERVIEW.FMT). Deliberate placeholder until list backends are implemented.
/// Unsupported LIST keywords (ACTIVE.TIMES, COUNTS, DISTRIB.PATS, DISTRIBUTIONS, MODERATORS,
/// SUBSCRIPTIONS) are not registered and resolve as unknown command variants.
/// </remarks>
internal static class List
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(List));

    /// <summary>Handles <c>LIST</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(
            Logger,
            context,
            "LIST",
            static (ctx, ct) => NntpCommandNotImplemented.HandleAsync(ctx, Logger, ct),
            cancellationToken,
            successDetail: "not implemented");
}
