using VectorNNTP.NNTPD.Session.CommandProcessor;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// GROUP command as defined by RFC 3977, Section 6.1.1.
/// </summary>
/// <remarks>
/// Selects a newsgroup as the currently selected group. Deliberate placeholder until group storage is implemented.
/// </remarks>
internal static class Group
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(Group));

    /// <summary>Handles <c>GROUP</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(
            Logger,
            context,
            "GROUP",
            static (ctx, ct) => NntpCommandNotImplemented.HandleAsync(ctx, Logger, ct),
            cancellationToken,
            successDetail: "not implemented");
}
