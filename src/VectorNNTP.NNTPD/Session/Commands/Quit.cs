namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>QUIT command (RFC 3977).</summary>
internal static class Quit
{
    /// <summary>Handles <c>QUIT</c>.</summary>
    public static async ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        await context.Response
            .WriteLineAsync(NntpReplyCodes.ConnectionClosing, "Connection closing", cancellationToken)
            .ConfigureAwait(false);
        context.Session.RequestClose();
    }
}
