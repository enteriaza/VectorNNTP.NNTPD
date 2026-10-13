using VectorNNTP.Common.Articles;

namespace VectorNNTP.StorageServer.Storage.SpoolShards;

/// <summary>
/// Deterministic relative article path from the canonical 32-byte BLAKE3 <see cref="ArticleId"/>.
/// The path does not hash the identity again and does not read the filesystem.
/// The same identity has the same relative path on every spool.
/// </summary>
internal static class SpoolArticlePath
{
    /// <summary>
    /// Relative path <c>{n0}/{n1}/{n2}/{64 lowercase hex}</c>.
    /// The three directory nibbles are the first three characters of the canonical lowercase hex digest.
    /// </summary>
    /// <param name="articleId">Canonical VATP article identity.</param>
    /// <returns>A relative path with no parent segments.</returns>
    internal static string RelativePath(ArticleId articleId)
    {
        var hex = articleId.ToLowerHexString();
        return string.Concat(hex[0], "/", hex[1], "/", hex[2], "/", hex);
    }

    /// <summary>
    /// Resolves a lowercase hexadecimal article id. Uppercase, the wrong length, and any non-hex character fail.
    /// </summary>
    /// <param name="articleIdHex">Exactly 64 lowercase hexadecimal characters.</param>
    /// <param name="relativePath">The relative article path when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="articleIdHex"/> is a canonical <see cref="ArticleId"/> encoding.</returns>
    internal static bool TryRelativePath(ReadOnlySpan<char> articleIdHex, out string relativePath)
    {
        if (!ArticleId.TryParseLowerHex(articleIdHex, out var articleId))
        {
            relativePath = string.Empty;
            return false;
        }

        relativePath = RelativePath(articleId);
        return true;
    }

    /// <summary>
    /// Combines a spool root with a resolver-produced relative path and rejects a result outside that root.
    /// </summary>
    /// <param name="spoolRoot">Absolute spool directory.</param>
    /// <param name="relativePath">Path from <see cref="RelativePath"/>.</param>
    /// <returns>The absolute article path.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the combined path leaves <paramref name="spoolRoot"/>.</exception>
    internal static string CombineUnderRoot(string spoolRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (relativePath.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Article path is not a relative spool path.");
        }

        var combined = spoolRoot;
        var rest = relativePath.AsSpan();
        while (rest.Length > 0)
        {
            var slash = rest.IndexOf('/');
            var segment = slash < 0 ? rest : rest[..slash];
            if (segment.Length == 0 || segment.Length > ArticleId.HexLength)
            {
                throw new InvalidOperationException("Article path contains an empty or oversized segment.");
            }

            for (var i = 0; i < segment.Length; i++)
            {
                var c = segment[i];
                if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                {
                    throw new InvalidOperationException("Article path contains a non-hexadecimal segment.");
                }
            }

            combined = Path.Combine(combined, segment.ToString());
            if (slash < 0)
            {
                break;
            }

            rest = rest[(slash + 1)..];
        }

        var root = Path.GetFullPath(spoolRoot);
        var full = Path.GetFullPath(combined);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, comparison))
        {
            throw new InvalidOperationException("Article path escapes the spool root.");
        }

        return full;
    }
}
