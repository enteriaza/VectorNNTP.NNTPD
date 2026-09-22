namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>POST command (RFC 3977).</summary>
/// <remarks>TODO: Implement RFC-compliant POST behavior.</remarks>
internal static class Post
{
    /// <summary>Handles <c>POST</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // TODO: Implement RFC-compliant POST behavior.
        return NntpCommandNotImplemented.HandleAsync(context, cancellationToken);
    }
}
