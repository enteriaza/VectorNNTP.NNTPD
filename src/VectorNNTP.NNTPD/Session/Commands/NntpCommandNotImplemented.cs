namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>Shared placeholder response for commands not yet implemented.</summary>
internal static class NntpCommandNotImplemented
{
    /// <summary>Returns <c>500 Command not implemented</c> (RFC 3977 for unsupported optional commands).</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        context.Response.WriteLineAsync(NntpReplyCodes.UnknownCommand, "Command not implemented", cancellationToken);
}
