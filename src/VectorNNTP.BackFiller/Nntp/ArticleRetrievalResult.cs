namespace VectorNNTP.BackFiller.Nntp;

/// <summary>Outcome of one ARTICLE retrieval. Payload is present only for <see cref="ArticleRetrievalKind.ArticleRetrieved"/>.</summary>
/// <param name="Kind">Retrieval classification.</param>
/// <param name="StatusCode">NNTP status when the server produced one.</param>
/// <param name="Reason">Diagnostic text. Never a secret.</param>
/// <param name="Article">Owned payload when retrieval succeeded.</param>
/// <param name="SessionReusable">Whether the leased session may return to the idle pool.</param>
public sealed record ArticleRetrievalResult(
    ArticleRetrievalKind Kind,
    int? StatusCode,
    string Reason,
    RetrievedArticle? Article,
    bool SessionReusable) : IDisposable
{
    /// <inheritdoc />
    public void Dispose()
    {
        Article?.Dispose();
    }

    /// <summary>Creates a successful retrieval that owns <paramref name="article"/>.</summary>
    public static ArticleRetrievalResult Retrieved(int statusCode, string reason, RetrievedArticle article) =>
        new(ArticleRetrievalKind.ArticleRetrieved, statusCode, reason, article, SessionReusable: true);

    /// <summary>Creates a non-payload result.</summary>
    public static ArticleRetrievalResult Failed(
        ArticleRetrievalKind kind,
        int? statusCode,
        string reason,
        bool sessionReusable) =>
        new(kind, statusCode, reason, Article: null, sessionReusable);
}
