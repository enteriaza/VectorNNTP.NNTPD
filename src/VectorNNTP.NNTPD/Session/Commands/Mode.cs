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
/// MODE STREAM (RFC 4644 §2.3) is retained for legacy clients. It returns <c>203</c> and
/// MUST NOT change session state. Authorization is <see cref="NntpCommandAccess.RequiresStreaming"/>
/// (peer or authenticated streaming privilege). CHECK/TAKETHIS are gated by transit authorization
/// and the advertised <c>STREAMING</c> capability — not by issuing MODE STREAM first.
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
        NntpCommandExecution.RunAsync(Logger, context, "MODE STREAM", ExecuteStreamAsync, cancellationToken);

    private static async ValueTask ExecuteReaderAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // RFC 4643: client MUST NOT issue MODE READER after authentication; reject consistently.
        if (context.Session.Authentication.IsAuthenticated)
        {
            await context.Response
                .WriteLineAsync(NntpResponses.CommandUnavailableAfterAuthentication, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // RFC 8054 §2.2.2: client MUST NOT issue MODE READER after a compression layer is active.
        if (context.Connection.IsCompressed)
        {
            await context.Response
                .WriteLineAsync(NntpResponses.CommandUnavailableAfterCompression, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        context.Session.SetMode(NntpSessionMode.Reader);
        if (context.Session.Authorization.PostingPermitted)
        {
            await context.Response
                .WriteLineAsync(NntpResponses.ReaderModePostingPermitted, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await context.Response
                .WriteLineAsync(NntpResponses.ReaderModePostingProhibited, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask ExecuteStreamAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // RFC 4644 §2.3.2: MUST return 203 and MUST NOT affect server state despite the name.
        // StreamingPermitted is enforced by the dispatcher (RequiresStreaming).
        await context.Response
            .WriteLineAsync(NntpResponses.StreamingPermitted, cancellationToken)
            .ConfigureAwait(false);
    }
}
