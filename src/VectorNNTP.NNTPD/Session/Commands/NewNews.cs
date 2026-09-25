using VectorNNTP.NNTPD.Session.CommandProcessor;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// NEWNEWS command as defined by RFC 3977, Section 7.4.
/// </summary>
/// <remarks>
/// Lists message-ids of articles posted after a given date/time. Deliberate placeholder until article storage is implemented.
/// </remarks>
internal static class NewNews
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(NewNews));

    /// <summary>Handles <c>NEWNEWS</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(
            Logger,
            context,
            "NEWNEWS",
            static (ctx, ct) => NntpCommandNotImplemented.HandleAsync(ctx, Logger, ct),
            cancellationToken,
            successDetail: "not implemented");
}
