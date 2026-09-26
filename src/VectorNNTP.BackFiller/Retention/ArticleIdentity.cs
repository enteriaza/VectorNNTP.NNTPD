using System.Security.Cryptography;
using System.Text;

namespace VectorNNTP.BackFiller.Retention;

/// <summary>
/// Deterministic cache identity for one exact Message-ID.
/// </summary>
/// <param name="MessageId">Exact Message-ID string. Not normalized.</param>
/// <param name="Md5Hex">32-character lowercase hexadecimal MD5 of the exact ASCII Message-ID bytes.</param>
public readonly record struct ArticleIdentity(string MessageId, string Md5Hex)
{
    /// <summary>Canonical MD5 hex length.</summary>
    public const int Md5HexLength = 32;

    /// <summary>
    /// Creates an identity from the exact Message-ID. Does not validate, trim, case-fold, or change brackets.
    /// </summary>
    /// <param name="messageId">Exact Message-ID from Article Work.</param>
    /// <returns>The identity including lowercase MD5 hex.</returns>
    public static ArticleIdentity FromExactMessageId(string messageId)
    {
        ArgumentNullException.ThrowIfNull(messageId);
        var digest = MD5.HashData(Encoding.ASCII.GetBytes(messageId));
        return new ArticleIdentity(messageId, Convert.ToHexString(digest).ToLowerInvariant());
    }
}
