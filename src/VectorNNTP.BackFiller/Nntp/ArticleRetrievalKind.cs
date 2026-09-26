namespace VectorNNTP.BackFiller.Nntp;

/// <summary>Classification of one upstream retrieval attempt.</summary>
public enum ArticleRetrievalKind
{
    /// <summary>ARTICLE succeeded and produced an owned destuffed payload.</summary>
    ArticleRetrieved = 0,

    /// <summary>Provider reported that the article does not exist (430).</summary>
    ArticleNotFound = 1,

    /// <summary>Framing completed but the destuffed payload is not a usable article.</summary>
    InvalidArticle = 2,

    /// <summary>Transport, timeout, protocol, or remote rejection that is not a miss.</summary>
    ProviderFailure = 3,

    /// <summary>AUTHINFO failed or credentials are half-configured. Maps to ProviderFailure disposition.</summary>
    AuthenticationFailure = 4,

    /// <summary>Caller or shutdown cancellation.</summary>
    Cancelled = 5,
}
