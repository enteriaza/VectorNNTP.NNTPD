using Microsoft.Extensions.Logging;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// ARTICLE, HEAD, BODY, and STAT as defined by RFC 3977, Sections 6.2.1–6.2.4.
/// </summary>
/// <remarks>
/// <para>
/// Deliberate placeholders until article <strong>retrieval</strong> / selection state exist.
/// Ingestion (TAKETHIS → spool) is not a substitute for a customer article catalog.
/// </para>
/// <para>
/// Phase 2 TX readiness: when a lookup API exists, handlers must transmit via the shared
/// article TX data plane — <see cref="NntpResponseWriter.WriteCustomerArticleAsync"/> /
/// <see cref="NntpResponseWriter.WriteCustomerBodyAsync"/> (or WriteArticleAsync with
/// <see cref="NntpArticleTxFraming"/>) — not per-line
/// <see cref="NntpResponseWriter.WriteMultilineDataAsync"/>.
/// </para>
/// </remarks>
internal static class Article
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(Article));

    /// <summary>Handles <c>ARTICLE</c> (RFC 3977, Section 6.2.1).</summary>
    public static ValueTask HandleArticleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(
            Logger,
            context,
            "ARTICLE",
            static (ctx, ct) => NntpCommandNotImplemented.HandleAsync(ctx, ct),
            cancellationToken,
            successDetail: "not implemented");

    /// <summary>Handles <c>HEAD</c> (RFC 3977, Section 6.2.2).</summary>
    public static ValueTask HandleHeadAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(
            Logger,
            context,
            "HEAD",
            static (ctx, ct) => NntpCommandNotImplemented.HandleAsync(ctx, ct),
            cancellationToken,
            successDetail: "not implemented");

    /// <summary>Handles <c>BODY</c> (RFC 3977, Section 6.2.3).</summary>
    public static ValueTask HandleBodyAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(
            Logger,
            context,
            "BODY",
            static (ctx, ct) => NntpCommandNotImplemented.HandleAsync(ctx, ct),
            cancellationToken,
            successDetail: "not implemented");

    /// <summary>Handles <c>STAT</c> (RFC 3977, Section 6.2.4).</summary>
    public static ValueTask HandleStatAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(
            Logger,
            context,
            "STAT",
            static (ctx, ct) => NntpCommandNotImplemented.HandleAsync(ctx, ct),
            cancellationToken,
            successDetail: "not implemented");
}
