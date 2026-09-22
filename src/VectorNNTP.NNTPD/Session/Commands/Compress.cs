using Microsoft.Extensions.Logging;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// COMPRESS command (DEFLATE) as defined by RFC 8054, Section 2.2.
/// </summary>
/// <remarks>
/// Activates raw DEFLATE compression on the connection. Transport DEFLATE exists; the NNTP command path is a deliberate placeholder until capability advertisement and activation are wired.
/// </remarks>
internal static class Compress
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(Compress));

    /// <summary>Handles <c>COMPRESS DEFLATE</c>.</summary>
    public static ValueTask HandleDeflateAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(
            Logger,
            context,
            "COMPRESS DEFLATE",
            static (ctx, ct) => NntpCommandNotImplemented.HandleAsync(ctx, ct),
            cancellationToken,
            successDetail: "not implemented");
}
