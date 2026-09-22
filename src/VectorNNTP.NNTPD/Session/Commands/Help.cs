namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>HELP command (RFC 3977).</summary>
internal static class Help
{
    /// <summary>Handles <c>HELP</c>.</summary>
    public static async ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        await context.Response
            .WriteMultilineStartAsync(NntpReplyCodes.HelpTextFollows, "Help text follows", cancellationToken)
            .ConfigureAwait(false);
        await context.Response.WriteMultilineDataAsync("VectorNNTP.NNTPD session foundation", cancellationToken)
            .ConfigureAwait(false);
        await context.Response
            .WriteMultilineDataAsync(
                "Public: CAPABILITIES MODE READER HELP DATE QUIT STARTTLS AUTHINFO USER PASS",
                cancellationToken)
            .ConfigureAwait(false);
        await context.Response.WriteMultilineEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
