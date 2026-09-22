namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>NEXT command (RFC 3977).</summary>
/// <remarks>TODO: Implement RFC-compliant NEXT behavior.</remarks>
internal static class Next
{
    /// <summary>Handles <c>NEXT</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // TODO: Implement RFC-compliant NEXT behavior.
        return NntpCommandNotImplemented.HandleAsync(context, cancellationToken);
    }
}
