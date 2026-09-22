namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>LAST command (RFC 3977).</summary>
/// <remarks>TODO: Implement RFC-compliant LAST behavior.</remarks>
internal static class Last
{
    /// <summary>Handles <c>LAST</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // TODO: Implement RFC-compliant LAST behavior.
        return NntpCommandNotImplemented.HandleAsync(context, cancellationToken);
    }
}
