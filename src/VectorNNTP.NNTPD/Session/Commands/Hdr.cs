namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>HDR command (RFC 3977).</summary>
/// <remarks>TODO: Implement RFC-compliant HDR behavior.</remarks>
internal static class Hdr
{
    /// <summary>Handles <c>HDR</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // TODO: Implement RFC-compliant HDR behavior.
        return NntpCommandNotImplemented.HandleAsync(context, cancellationToken);
    }
}
