namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>OVER command (RFC 3977).</summary>
/// <remarks>TODO: Implement RFC-compliant OVER behavior.</remarks>
internal static class Over
{
    /// <summary>Handles <c>OVER</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // TODO: Implement RFC-compliant OVER behavior.
        return NntpCommandNotImplemented.HandleAsync(context, cancellationToken);
    }
}
