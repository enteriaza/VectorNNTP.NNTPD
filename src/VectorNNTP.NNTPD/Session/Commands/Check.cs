namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>CHECK command (RFC 4644).</summary>
/// <remarks>TODO: Implement RFC-compliant CHECK behavior.</remarks>
internal static class Check
{
    /// <summary>Handles <c>CHECK</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // TODO: Implement RFC-compliant CHECK behavior.
        return NntpCommandNotImplemented.HandleAsync(context, cancellationToken);
    }
}
