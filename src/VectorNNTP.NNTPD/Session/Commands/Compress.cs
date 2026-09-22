namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>COMPRESS DEFLATE (RFC 8054); activation is transport-owned.</summary>
/// <remarks>TODO: Implement RFC-compliant COMPRESS DEFLATE command and capability advertisement.</remarks>
internal static class Compress
{
    /// <summary>Handles <c>COMPRESS DEFLATE</c>.</summary>
    public static ValueTask HandleDeflateAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // TODO: Implement RFC-compliant COMPRESS DEFLATE behavior (invoke UpgradeToDeflateAsync).
        return NntpCommandNotImplemented.HandleAsync(context, cancellationToken);
    }
}
