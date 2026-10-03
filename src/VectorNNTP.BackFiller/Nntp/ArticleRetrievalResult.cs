namespace VectorNNTP.BackFiller.Nntp;

/// <summary>Outcome of one ARTICLE retrieval. Payload is present only for <see cref="ArticleRetrievalKind.ArticleRetrieved"/>.</summary>
/// <param name="Kind">Retrieval classification.</param>
/// <param name="StatusCode">NNTP status when the server produced one.</param>
/// <param name="Reason">Diagnostic text. Never a secret.</param>
/// <param name="Article">Owned payload when retrieval succeeded.</param>
/// <param name="SessionReusable">Whether the leased session may return to the idle pool.</param>
internal sealed record ArticleRetrievalResult(
    ArticleRetrievalKind Kind,
    int? StatusCode,
    string Reason,
    RetrievedArticle? Article,
    bool SessionReusable) : IDisposable
{
    /// <summary>Disposes the owned <see cref="Article"/> when one is present.</summary>
    public void Dispose()
    {
        Article?.Dispose();
    }

    /// <summary>Creates a successful retrieval that owns <paramref name="article"/>.</summary>
    /// <param name="statusCode">NNTP status stored on the result.</param>
    /// <param name="reason">Diagnostic text stored on the result.</param>
    /// <param name="article">Payload owned by the result.</param>
    /// <returns>
    /// A result with <see cref="ArticleRetrievalKind.ArticleRetrieved"/>, <see cref="SessionReusable"/> true,
    /// and <paramref name="article"/>.
    /// </returns>
    internal static ArticleRetrievalResult Retrieved(int statusCode, string reason, RetrievedArticle article) =>
        new(ArticleRetrievalKind.ArticleRetrieved, statusCode, reason, article, SessionReusable: true);

    /// <summary>Creates a non-payload result.</summary>
    /// <param name="kind">Retrieval classification stored on the result.</param>
    /// <param name="statusCode">NNTP status when the server produced one; otherwise null.</param>
    /// <param name="reason">Diagnostic text stored on the result.</param>
    /// <param name="sessionReusable">Whether the leased session may return to the idle pool.</param>
    /// <returns>
    /// A result whose <see cref="Article"/> is null and whose other fields are the supplied values.
    /// </returns>
    internal static ArticleRetrievalResult Failed(
        ArticleRetrievalKind kind,
        int? statusCode,
        string reason,
        bool sessionReusable) =>
        new(kind, statusCode, reason, Article: null, sessionReusable);
}
