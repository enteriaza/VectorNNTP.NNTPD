namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>NEWNEWS command (RFC 3977).</summary>
/// <remarks>TODO: Implement RFC-compliant NEWNEWS behavior.</remarks>
internal static class NewNews
{
    /// <summary>Handles <c>NEWNEWS</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // TODO: Implement RFC-compliant NEWNEWS behavior.
        return NntpCommandNotImplemented.HandleAsync(context, cancellationToken);
    }
}
