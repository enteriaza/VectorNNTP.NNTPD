using Microsoft.Extensions.Logging;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// ARTICLE, HEAD, BODY, and STAT as defined by RFC 3977, Sections 6.2.1–6.2.4.
/// </summary>
/// <remarks>
/// <para>
/// Deliberate <see cref="NntpCommandNotImplemented"/> placeholders. Article storage, catalog,
/// GROUP selection, and Message-ID / article-number lookup are <strong>out of scope</strong> for
/// VectorNNTP.NNTPD (network / data plane only). Do not invent retrieval here.
/// </para>
/// <para>
/// TAKETHIS → spool ingestion is an ingress / application boundary — not an article repository
/// for ARTICLE/BODY. Callers that already hold destuffed article bytes transmit via the shared
/// TX path (<see cref="NntpResponseWriter.WriteArticleAsync(ReadOnlyMemory{byte}, NntpArticleTxFraming, CancellationToken)"/> /
/// <see cref="NntpResponseWriter.WriteCustomerArticleAsync"/> /
/// <see cref="NntpResponseWriter.WriteCustomerBodyAsync"/>), not per-line
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
