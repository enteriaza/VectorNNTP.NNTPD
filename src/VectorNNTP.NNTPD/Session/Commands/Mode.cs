using Microsoft.Extensions.Logging;
using VectorNNTP.NNTPD.Session;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// MODE READER (RFC 3977, Section 5.3) and MODE STREAM (RFC 4644, Section 2.3).
/// </summary>
/// <remarks>
/// <para>
/// MODE READER selects reader mode and reports posting permission. After authentication,
/// MODE READER is rejected (RFC 4643).
/// </para>
/// <para>
/// MODE STREAM is registered for STREAMING clients; RFC 4644 deprecates requiring it before
/// CHECK/TAKETHIS. The STREAM handler is a deliberate placeholder until streaming mode state is implemented.
/// </para>
/// </remarks>
internal static class Mode
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(Mode));

    /// <summary>Handles <c>MODE READER</c>.</summary>
    public static ValueTask HandleReaderAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "MODE READER", ExecuteReaderAsync, cancellationToken);

    /// <summary>Handles <c>MODE STREAM</c>.</summary>
    public static ValueTask HandleStreamAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(
            Logger,
            context,
            "MODE STREAM",
            ExecuteStreamAsync,
            cancellationToken,
            successDetail: "not implemented");

    private static async ValueTask ExecuteReaderAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // RFC 4643: client MUST NOT issue MODE READER after authentication; reject consistently.
        if (context.Session.Authentication.IsAuthenticated)
        {
            await context.Response
                .WriteLineAsync(NntpReplyCodes.CommandUnavailable, "Command unavailable after authentication", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        context.Session.SetMode(NntpSessionMode.Reader);
        if (context.Session.Authorization.PostingPermitted)
        {
            await context.Response
                .WriteLineAsync(NntpReplyCodes.PostingAllowed, "Reader mode, posting permitted", cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await context.Response
                .WriteLineAsync(NntpReplyCodes.PostingProhibited, "Reader mode, posting prohibited", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static ValueTask ExecuteStreamAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        context.Response.WriteLineAsync(
            NntpReplyCodes.SyntaxError,
            "MODE STREAM not implemented",
            cancellationToken);
}
