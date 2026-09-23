namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// POST command as defined by RFC 3977, Section 6.3.1.
/// </summary>
/// <remarks>
/// Requests posting of an article to the server. Deliberate placeholder until posting/storage semantics are implemented.
/// </remarks>
internal static class Post
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(Post));

    /// <summary>Handles <c>POST</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(
            Logger,
            context,
            "POST",
            static (ctx, ct) => NntpCommandNotImplemented.HandleAsync(ctx, ct),
            cancellationToken,
            successDetail: "not implemented");
}
