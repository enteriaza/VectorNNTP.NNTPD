namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>NEWGROUPS command (RFC 3977).</summary>
/// <remarks>TODO: Implement RFC-compliant NEWGROUPS behavior.</remarks>
internal static class NewGroups
{
    /// <summary>Handles <c>NEWGROUPS</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // TODO: Implement RFC-compliant NEWGROUPS behavior.
        return NntpCommandNotImplemented.HandleAsync(context, cancellationToken);
    }
}
