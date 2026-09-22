namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>GROUP command (RFC 3977).</summary>
/// <remarks>TODO: Implement RFC-compliant GROUP behavior.</remarks>
internal static class Group
{
    /// <summary>Handles <c>GROUP</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // TODO: Implement RFC-compliant GROUP behavior.
        return NntpCommandNotImplemented.HandleAsync(context, cancellationToken);
    }
}
