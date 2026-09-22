namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>IHAVE command (RFC 3977 / transit).</summary>
/// <remarks>TODO: Implement RFC-compliant IHAVE behavior.</remarks>
internal static class IHave
{
    /// <summary>Handles <c>IHAVE</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // TODO: Implement RFC-compliant IHAVE behavior.
        return NntpCommandNotImplemented.HandleAsync(context, cancellationToken);
    }
}
