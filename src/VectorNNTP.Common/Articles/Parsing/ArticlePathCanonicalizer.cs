namespace VectorNNTP.Common.Articles.Parsing;

/// <summary>
/// Classifies how a Path header would be rewritten without mutating the article buffer.
/// </summary>
public enum ArticlePathKind
{
    /// <summary>No Path header was present.</summary>
    Missing = 0,

    /// <summary>Path was present but contained only separators or whitespace.</summary>
    Empty = 1,

    /// <summary>A validated component already equals the local identity (case-insensitive).</summary>
    AlreadyContainsLocalIdentity = 2,

    /// <summary>Validated components exist and the local identity must be prepended.</summary>
    NeedsLocalIdentityPrepend = 3,
}

/// <summary>
/// Byte-oriented Path validation and canonical-write primitives.
/// </summary>
/// <remarks>
/// These helpers determine validity and the rewrite kind. They do not rewrite the article buffer.
/// Common owns the organizational tracker hostname <c>news.usenet.ninja</c> and application-hop
/// prepend rules. Callers supply only their own application FQDN.
/// </remarks>
public static class ArticlePathCanonicalizer
{
    /// <summary>Maximum accepted Path value length.</summary>
    public const int MaxPathLength = 8192;

    /// <summary>
    /// Organizational Vector tracker hostname. Present exactly once after any Vector application has seen the article.
    /// </summary>
    public static ReadOnlySpan<byte> OrganizationalTrackerHost => "news.usenet.ninja"u8;

    /// <summary>
    /// Validates a raw Path value and classifies the rewrite that a later materializer would apply.
    /// </summary>
    /// <param name="rawPath">Raw Path header value bytes, or empty when the header is absent.</param>
    /// <param name="localIdentity">Local application FQDN that would be prepended when missing.</param>
    /// <param name="pathPresent">Whether a Path header was present.</param>
    /// <param name="kind">Rewrite classification when validation succeeds.</param>
    /// <param name="containsOrganizationalTracker">
    /// Whether a Path token equals <see cref="OrganizationalTrackerHost"/> using existing case-insensitive token rules.
    /// </param>
    /// <param name="failureCode">Failure code when the Path value is invalid.</param>
    /// <returns><see langword="true"/> when the Path is usable (including a missing header).</returns>
    public static bool TryAnalyze(
        ReadOnlySpan<byte> rawPath,
        ReadOnlySpan<byte> localIdentity,
        bool pathPresent,
        out ArticlePathKind kind,
        out bool containsOrganizationalTracker,
        out NntpArticleParseFailureCode failureCode)
    {
        kind = ArticlePathKind.Missing;
        containsOrganizationalTracker = false;
        failureCode = NntpArticleParseFailureCode.None;

        if (!pathPresent)
        {
            return true;
        }

        if (rawPath.Length > MaxPathLength)
        {
            failureCode = NntpArticleParseFailureCode.InvalidPath;
            return false;
        }

        for (var i = 0; i < rawPath.Length; i++)
        {
            var b = rawPath[i];
            if (b is < 0x20 or > 0x7E)
            {
                failureCode = NntpArticleParseFailureCode.InvalidPath;
                return false;
            }
        }

        var remaining = TrimAscii(rawPath);
        if (remaining.IsEmpty)
        {
            kind = ArticlePathKind.Empty;
            return true;
        }

        var sawComponent = false;
        var alreadyPresent = false;
        while (TryConsumePathComponent(ref remaining, out var component))
        {
            if (component.IsEmpty)
            {
                continue;
            }

            if (!IsValidPathComponent(component))
            {
                failureCode = NntpArticleParseFailureCode.InvalidPath;
                return false;
            }

            sawComponent = true;
            if (AsciiEqualsIgnoreCase(component, OrganizationalTrackerHost))
            {
                containsOrganizationalTracker = true;
            }

            if (AsciiEqualsIgnoreCase(component, localIdentity))
            {
                alreadyPresent = true;
            }
        }

        if (!sawComponent)
        {
            kind = ArticlePathKind.Empty;
            return true;
        }

        kind = alreadyPresent
            ? ArticlePathKind.AlreadyContainsLocalIdentity
            : ArticlePathKind.NeedsLocalIdentityPrepend;
        return true;
    }

    /// <summary>
    /// Writes the canonical Path bytes that a later materializer would produce.
    /// </summary>
    /// <param name="rawPath">Original Path value bytes when present; ignored when <paramref name="kind"/> is <see cref="ArticlePathKind.Missing"/> or <see cref="ArticlePathKind.Empty"/>.</param>
    /// <param name="localIdentity">Local application FQDN written as an application hop when needed.</param>
    /// <param name="kind">Rewrite classification from <see cref="TryAnalyze"/>.</param>
    /// <param name="containsOrganizationalTracker">Whether the organizational tracker token is already present.</param>
    /// <param name="destination">Destination receiving ASCII Path bytes.</param>
    /// <param name="bytesWritten">Bytes written on success.</param>
    /// <returns><see langword="true"/> when <paramref name="destination"/> was large enough.</returns>
    /// <remarks>
    /// First Vector traversal (tracker absent) writes
    /// <c>news.usenet.ninja!{application-fqdn}!existing</c>.
    /// Later traversals (tracker already present) write
    /// <c>{application-fqdn}!existing</c>.
    /// Empty Path tokens from repeated <c>!</c> separators are dropped, matching existing rules.
    /// </remarks>
    public static bool TryWriteCanonicalPath(
        ReadOnlySpan<byte> rawPath,
        ReadOnlySpan<byte> localIdentity,
        ArticlePathKind kind,
        bool containsOrganizationalTracker,
        Span<byte> destination,
        out int bytesWritten)
    {
        var remaining = kind is ArticlePathKind.Missing or ArticlePathKind.Empty
            ? ReadOnlySpan<byte>.Empty
            : TrimAscii(rawPath);

        var prependTracker = !containsOrganizationalTracker;
        var prependLocal = kind is not ArticlePathKind.AlreadyContainsLocalIdentity;

        return TryWriteJoinedComponents(
            remaining,
            prependTracker ? OrganizationalTrackerHost : default,
            prependLocal ? localIdentity : default,
            destination,
            out bytesWritten);
    }

    /// <summary>
    /// Returns whether one Path component is syntactically acceptable.
    /// </summary>
    /// <param name="component">Path component bytes after trimming.</param>
    /// <returns><see langword="true"/> when the component is valid.</returns>
    public static bool IsValidPathComponent(ReadOnlySpan<byte> component)
    {
        for (var i = 0; i < component.Length; i++)
        {
            var b = component[i];
            if (b is <= 0x20 or (byte)'!' or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryWriteJoinedComponents(
        ReadOnlySpan<byte> remaining,
        ReadOnlySpan<byte> prepend1,
        ReadOnlySpan<byte> prepend2,
        Span<byte> destination,
        out int bytesWritten)
    {
        bytesWritten = 0;
        var first = true;
        if (!TryWriteHop(prepend1, ref first, destination, ref bytesWritten)
            || !TryWriteHop(prepend2, ref first, destination, ref bytesWritten))
        {
            return false;
        }

        while (TryConsumePathComponent(ref remaining, out var component))
        {
            if (component.IsEmpty)
            {
                continue;
            }

            if (!TryWriteHop(component, ref first, destination, ref bytesWritten))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryWriteHop(
        ReadOnlySpan<byte> hop,
        ref bool first,
        Span<byte> destination,
        ref int bytesWritten)
    {
        if (hop.IsEmpty)
        {
            return true;
        }

        var needed = hop.Length + (first ? 0 : 1);
        if (bytesWritten + needed > destination.Length)
        {
            bytesWritten = 0;
            return false;
        }

        if (!first)
        {
            destination[bytesWritten++] = (byte)'!';
        }

        hop.CopyTo(destination[bytesWritten..]);
        bytesWritten += hop.Length;
        first = false;
        return true;
    }

    private static bool TryConsumePathComponent(ref ReadOnlySpan<byte> remaining, out ReadOnlySpan<byte> component)
    {
        if (remaining.IsEmpty)
        {
            component = default;
            return false;
        }

        var separator = remaining.IndexOf((byte)'!');
        if (separator < 0)
        {
            component = TrimAscii(remaining);
            remaining = default;
            return true;
        }

        component = TrimAscii(remaining[..separator]);
        remaining = remaining[(separator + 1)..];
        return true;
    }

    private static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && value[start] is (byte)' ' or (byte)'\t')
        {
            start++;
        }

        while (end > start && value[end - 1] is (byte)' ' or (byte)'\t')
        {
            end--;
        }

        return value[start..end];
    }

    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (ToLowerAscii(left[i]) != ToLowerAscii(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static byte ToLowerAscii(byte value)
        => (uint)(value - (byte)'A') <= 'Z' - 'A' ? (byte)(value + 32) : value;
}
