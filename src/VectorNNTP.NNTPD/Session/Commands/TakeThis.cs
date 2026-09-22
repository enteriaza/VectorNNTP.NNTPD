namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>TAKETHIS command (RFC 4644).</summary>
/// <remarks>TODO: Implement RFC-compliant TAKETHIS behavior.</remarks>
internal static class TakeThis
{
    /// <summary>Handles <c>TAKETHIS</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // TODO: Implement RFC-compliant TAKETHIS behavior.
        return NntpCommandNotImplemented.HandleAsync(context, cancellationToken);
    }
}
