using VectorNNTP.NNTPD.Session;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>MODE READER / MODE STREAM (RFC 3977 / RFC 4644).</summary>
internal static class Mode
{
    /// <summary>Handles <c>MODE READER</c>.</summary>
    public static async ValueTask HandleReaderAsync(NntpCommandContext context, CancellationToken cancellationToken)
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

    /// <summary>
    /// Handles <c>MODE STREAM</c>.
    /// </summary>
    /// <remarks>TODO: Implement RFC-compliant MODE STREAM behavior.</remarks>
    public static ValueTask HandleStreamAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // TODO: Implement RFC-compliant MODE STREAM behavior.
        return context.Response.WriteLineAsync(
            NntpReplyCodes.SyntaxError,
            "MODE STREAM not implemented",
            cancellationToken);
    }
}
