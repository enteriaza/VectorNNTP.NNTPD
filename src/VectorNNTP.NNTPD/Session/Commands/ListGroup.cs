namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>LISTGROUP command (RFC 3977).</summary>
/// <remarks>TODO: Implement RFC-compliant LISTGROUP behavior.</remarks>
internal static class ListGroup
{
    /// <summary>Handles <c>LISTGROUP</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // TODO: Implement RFC-compliant LISTGROUP behavior.
        return NntpCommandNotImplemented.HandleAsync(context, cancellationToken);
    }
}
