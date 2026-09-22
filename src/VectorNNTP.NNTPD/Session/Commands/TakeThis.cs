using Microsoft.Extensions.Logging;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// TAKETHIS command as defined by RFC 4644, Section 2.5.
/// </summary>
/// <remarks>
/// Sends an article in a streaming transfer. Deliberate placeholder until streaming transfer is implemented.
/// </remarks>
internal static class TakeThis
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(TakeThis));

    /// <summary>Handles <c>TAKETHIS</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(
            Logger,
            context,
            "TAKETHIS",
            static (ctx, ct) => NntpCommandNotImplemented.HandleAsync(ctx, ct),
            cancellationToken,
            successDetail: "not implemented");
}
