namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>LIST command and LIST variants (RFC 3977 / RFC 6048).</summary>
/// <remarks>TODO: Implement RFC-compliant LIST behavior.</remarks>
internal static class List
{
    /// <summary>Handles <c>LIST</c> (and registered variants as they are added).</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // TODO: Implement RFC-compliant LIST behavior.
        return NntpCommandNotImplemented.HandleAsync(context, cancellationToken);
    }
}
