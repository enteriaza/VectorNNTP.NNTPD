using Microsoft.Extensions.Logging;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// IHAVE command as defined by RFC 3977, Section 6.3.2.
/// </summary>
/// <remarks>
/// Offers an article to the server for transit. Deliberate placeholder until transit ingest is implemented.
/// </remarks>
internal static class IHave
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(IHave));

    /// <summary>Handles <c>IHAVE</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(
            Logger,
            context,
            "IHAVE",
            static (ctx, ct) => NntpCommandNotImplemented.HandleAsync(ctx, ct),
            cancellationToken,
            successDetail: "not implemented");
}
