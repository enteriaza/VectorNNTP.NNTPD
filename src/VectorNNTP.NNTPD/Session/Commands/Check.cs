namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// CHECK command as defined by RFC 4644, Section 2.4.
/// </summary>
/// <remarks>
/// Advises whether the server wants an article (STREAMING). Deliberate placeholder until streaming transfer is implemented.
/// </remarks>
internal static class Check
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(Check));

    /// <summary>Handles <c>CHECK</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(
            Logger,
            context,
            "CHECK",
            static (ctx, ct) => NntpCommandNotImplemented.HandleAsync(ctx, ct),
            cancellationToken,
            successDetail: "not implemented");
}
