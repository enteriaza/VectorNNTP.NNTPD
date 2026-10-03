using VectorNNTP.Common.Articles;

namespace VectorNNTP.BackFiller.Retention;

/// <summary>
/// Message-ID plus its canonical <see cref="ArticleId"/> hex used in Article Work Success
/// metadata and retention collision checks.
/// Not a VATP transfer key (VATP uses RequestId + ArticleId).
/// </summary>
/// <param name="MessageId">Exact Message-ID string. Not normalized.</param>
/// <param name="ArticleIdHex">64-character lowercase hexadecimal <see cref="ArticleId"/>.</param>
internal readonly record struct ArticleIdentity(string MessageId, string ArticleIdHex)
{
    /// <summary>Canonical ArticleId hex length.</summary>
    internal const int ArticleIdHexLength = ArticleId.HexLength;

    /// <summary>
    /// Creates an identity from the exact Message-ID and an already-computed <see cref="ArticleId"/>.
    /// Does not hash the Message-ID.
    /// </summary>
    internal static ArticleIdentity From(string messageId, ArticleId artId)
    {
        ArgumentNullException.ThrowIfNull(messageId);
        return new ArticleIdentity(messageId, artId.ToLowerHexString());
    }
}
