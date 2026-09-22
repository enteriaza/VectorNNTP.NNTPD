namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>ARTICLE / HEAD / BODY / STAT (RFC 3977).</summary>
/// <remarks>TODO: Implement RFC-compliant article retrieval behavior.</remarks>
internal static class Article
{
    /// <summary>Handles <c>ARTICLE</c>.</summary>
    public static ValueTask HandleArticleAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // TODO: Implement RFC-compliant ARTICLE behavior.
        return NntpCommandNotImplemented.HandleAsync(context, cancellationToken);
    }

    /// <summary>Handles <c>HEAD</c>.</summary>
    public static ValueTask HandleHeadAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // TODO: Implement RFC-compliant HEAD behavior.
        return NntpCommandNotImplemented.HandleAsync(context, cancellationToken);
    }

    /// <summary>Handles <c>BODY</c>.</summary>
    public static ValueTask HandleBodyAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // TODO: Implement RFC-compliant BODY behavior.
        return NntpCommandNotImplemented.HandleAsync(context, cancellationToken);
    }

    /// <summary>Handles <c>STAT</c>.</summary>
    public static ValueTask HandleStatAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        // TODO: Implement RFC-compliant STAT behavior.
        return NntpCommandNotImplemented.HandleAsync(context, cancellationToken);
    }
}
