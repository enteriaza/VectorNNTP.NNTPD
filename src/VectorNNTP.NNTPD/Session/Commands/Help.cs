using Microsoft.Extensions.Logging;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// HELP command as defined by RFC 3977, Section 7.2.
/// </summary>
/// <remarks>
/// Returns a multi-line help text describing available public commands for this session foundation.
/// </remarks>
internal static class Help
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(Help));

    /// <summary>Handles <c>HELP</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "HELP", ExecuteAsync, cancellationToken);

    private static async ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        await context.Response
            .WriteMultilineStartAsync(NntpReplyCodes.HelpTextFollows, "Help text follows", cancellationToken)
            .ConfigureAwait(false);
        await context.Response.WriteMultilineDataAsync("VectorNNTP.NNTPD session foundation", cancellationToken)
            .ConfigureAwait(false);
        await context.Response
            .WriteMultilineDataAsync(
                "Public: CAPABILITIES MODE READER HELP DATE QUIT STARTTLS COMPRESS AUTHINFO USER PASS",
                cancellationToken)
            .ConfigureAwait(false);
        await context.Response.WriteMultilineEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
