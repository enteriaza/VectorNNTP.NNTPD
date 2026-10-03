namespace VectorNNTP.Common.Articles
{
    /// <summary>
    /// Persistent <see cref="ArticleType"/> flag combinations used by account
    /// capability and enforcement. Distinct from one-type PostFilter policy ENUM rows.
    /// </summary>
    internal static class ArticleTypeCapabilities
    {
        /// <summary>
        /// Every defined classifier flag. Database default for existing
        /// <c>nntpusers</c> rows (<c>65535</c>). Means no ArtType restriction.
        /// </summary>
        public const ArticleType All =
            ArticleType.Default
            | ArticleType.Control
            | ArticleType.Cancel
            | ArticleType.Mime
            | ArticleType.Binary
            | ArticleType.UuEncode
            | ArticleType.Base64
            | ArticleType.YEncoded
            | ArticleType.BommaNews
            | ArticleType.UniData
            | ArticleType.Multipart
            | ArticleType.Html
            | ArticleType.PostScript
            | ArticleType.BinHex
            | ArticleType.Partial
            | ArticleType.PgpMessage;

        /// <summary>Unsigned integer stored for <see cref="All"/>.</summary>
        public const uint AllValue = (uint)All;

        /// <summary>Ordinary text only (<see cref="ArticleType.Default"/>).</summary>
        public const ArticleType TextOnly = ArticleType.Default;

        /// <summary>
        /// Returns whether <paramref name="allowed"/> permits <paramref name="articleType"/>.
        /// Unrestricted (<see cref="All"/>) permits every flag combination, including
        /// <see cref="ArticleType.None"/>. Otherwise every flag present on the article
        /// must be present on the capability.
        /// </summary>
        internal static bool Allows(ArticleType allowed, ArticleType articleType)
        {
            if (allowed == All)
            {
                return true;
            }

            return (articleType & ~allowed) == 0;
        }
    }
}
