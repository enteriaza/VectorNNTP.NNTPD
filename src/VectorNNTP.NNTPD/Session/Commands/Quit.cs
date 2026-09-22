using Microsoft.Extensions.Logging;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// QUIT command as defined by RFC 3977, Section 5.4.
/// </summary>
/// <remarks>
/// Ends the session after sending a connection-closing response. The TX completion record is emitted
/// before the session loop observes <see cref="NntpSession.RequestClose"/>.
/// </remarks>
internal static class Quit
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(Quit));

    /// <summary>Handles <c>QUIT</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "QUIT", ExecuteAsync, cancellationToken);

    private static async ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        await context.Response
            .WriteLineAsync(NntpReplyCodes.ConnectionClosing, "Connection closing", cancellationToken)
            .ConfigureAwait(false);
        context.Session.RequestClose();
    }
}
