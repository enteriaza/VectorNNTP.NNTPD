namespace VectorNNTP.Common.Articles.Parsing
{
    /// <summary>
    /// Classifies how a Path header would be rewritten without mutating the article buffer.
    /// </summary>
    internal enum ArticlePathKind
    {
        /// <summary>No Path header was present.</summary>
        Missing = 0,

        /// <summary>Path was present but contained only separators or whitespace.</summary>
        Empty = 1,

        /// <summary>The leftmost validated component already equals the local identity (case-insensitive).</summary>
        AlreadyContainsLocalIdentity = 2,

        /// <summary>
        /// The local identity is not the leftmost hop. Writes use <see cref="ArticlePathMode"/> rather than this classification.
        /// </summary>
        NeedsLocalIdentityPrepend = 3,
    }

    /// <summary>
    /// Selects whether a Path rewrite records a receiving-system traversal.
    /// </summary>
    internal enum ArticlePathMode
    {
        /// <summary>
        /// Validate and normalize Path without adding an application hop.
        /// <c>news.usenet.ninja</c> is inserted when absent and reduced to one occurrence.
        /// </summary>
        Normalize = 0,

        /// <summary>
        /// Normalize Path and always prepend the receiving system's application FQDN.
        /// </summary>
        Traverse = 1,
    }

    /// <summary>
    /// Byte-oriented Path validation and canonical-write primitives.
    /// </summary>
    /// <remarks>
    /// These helpers determine validity and the rewrite kind. They do not rewrite the article buffer.
    /// Common owns the organizational tracker hostname <c>news.usenet.ninja</c> and application-hop
    /// prepend rules. Callers supply only their own application FQDN.
    /// </remarks>
    internal static class ArticlePathCanonicalizer
    {
        /// <summary>Maximum accepted Path value length.</summary>
        public const int MaxPathLength = 8192;

        /// <summary>
        /// Organizational Vector tracker hostname. Present exactly once after any Vector application has seen the article.
        /// </summary>
        internal static ReadOnlySpan<byte> OrganizationalTrackerHost => "news.usenet.ninja"u8;

        /// <summary>
        /// Validates a raw Path value and classifies the rewrite that a later materializer would apply.
        /// </summary>
        /// <param name="rawPath">Raw Path header value bytes, or empty when the header is absent.</param>
        /// <param name="localIdentity">Local application FQDN. Analysis reports whether it is leftmost; writes use <see cref="ArticlePathMode"/>.</param>
        /// <param name="pathPresent">Whether a Path header was present.</param>
        /// <param name="kind">Rewrite classification when validation succeeds.</param>
        /// <param name="containsOrganizationalTracker">
        /// Whether a Path token equals <see cref="OrganizationalTrackerHost"/> using existing case-insensitive token rules.
        /// </param>
        /// <param name="failureCode">Failure code when the Path value is invalid.</param>
        /// <returns><see langword="true"/> when the Path is usable (including a missing header).</returns>
        internal static bool TryAnalyze(
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
            var leftmostMatchesLocal = false;
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

                if (!sawComponent)
                {
                    leftmostMatchesLocal = AsciiEqualsIgnoreCase(component, localIdentity);
                }

                sawComponent = true;
                if (AsciiEqualsIgnoreCase(component, OrganizationalTrackerHost))
                {
                    containsOrganizationalTracker = true;
                }
            }

            if (!sawComponent)
            {
                kind = ArticlePathKind.Empty;
                return true;
            }

            kind = leftmostMatchesLocal
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
        /// <param name="mode">Normalize leaves application hops unchanged. Traverse always prepends <paramref name="localIdentity"/>.</param>
        /// <param name="destination">Destination receiving ASCII Path bytes.</param>
        /// <param name="bytesWritten">Bytes written on success.</param>
        /// <returns><see langword="true"/> when <paramref name="destination"/> was large enough.</returns>
        /// <remarks>
        /// <see cref="ArticlePathMode.Traverse"/> with the tracker absent writes
        /// <c>news.usenet.ninja!{application-fqdn}!existing</c>.
        /// A later traverse writes <c>{application-fqdn}!existing</c> even when that FQDN is already present.
        /// <see cref="ArticlePathMode.Normalize"/> does not write an application FQDN.
        /// <c>news.usenet.ninja</c> is inserted when absent and reduced to the first occurrence.
        /// Empty Path tokens from repeated <c>!</c> separators are dropped.
        /// </remarks>
        internal static bool TryWriteCanonicalPath(
            ReadOnlySpan<byte> rawPath,
            ReadOnlySpan<byte> localIdentity,
            ArticlePathKind kind,
            bool containsOrganizationalTracker,
            ArticlePathMode mode,
            Span<byte> destination,
            out int bytesWritten)
        {
            var remaining = kind is ArticlePathKind.Missing or ArticlePathKind.Empty
                ? []
                : TrimAscii(rawPath);

            var traverse = mode == ArticlePathMode.Traverse;
            var localIsTracker = traverse
                && !localIdentity.IsEmpty
                && AsciiEqualsIgnoreCase(localIdentity, OrganizationalTrackerHost);
            var prependTracker = !containsOrganizationalTracker && !localIsTracker;
            var applicationHop = traverse ? localIdentity : default;

            return TryWriteJoinedComponents(
                remaining,
                prependTracker ? OrganizationalTrackerHost : default,
                applicationHop,
                trackerAlreadyEmitted: prependTracker || localIsTracker,
                destination,
                out bytesWritten);
        }

        /// <summary>
        /// Returns whether one Path component is syntactically acceptable.
        /// </summary>
        /// <param name="component">Path component bytes after trimming.</param>
        /// <returns><see langword="true"/> when the component is valid.</returns>
        private static bool IsValidPathComponent(ReadOnlySpan<byte> component)
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

        /// <summary>
        /// Writes optional prepended hops, then <c>!</c>-separated components from <paramref name="remaining"/>.
        /// </summary>
        /// <param name="remaining">Path value still to split. Empty components are skipped.</param>
        /// <param name="prepend1">First hop, or empty. Used for <see cref="OrganizationalTrackerHost"/> when it must be inserted.</param>
        /// <param name="prepend2">Second hop, or empty. Used for the application FQDN in traverse mode.</param>
        /// <param name="trackerAlreadyEmitted">
        /// When <see langword="true"/>, later components equal to <see cref="OrganizationalTrackerHost"/> are omitted.
        /// The first such component is written and then this suppression applies.
        /// </param>
        /// <param name="destination">Output buffer. Hop bytes are copied unchanged.</param>
        /// <param name="bytesWritten">Bytes written on success. Set to 0 when a hop does not fit.</param>
        /// <returns><see langword="false"/> when a non-empty hop does not fit in <paramref name="destination"/>.</returns>
        private static bool TryWriteJoinedComponents(
            ReadOnlySpan<byte> remaining,
            ReadOnlySpan<byte> prepend1,
            ReadOnlySpan<byte> prepend2,
            bool trackerAlreadyEmitted,
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

                if (AsciiEqualsIgnoreCase(component, OrganizationalTrackerHost))
                {
                    if (trackerAlreadyEmitted)
                    {
                        continue;
                    }

                    trackerAlreadyEmitted = true;
                }

                if (!TryWriteHop(component, ref first, destination, ref bytesWritten))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Appends one hop, prefixed by <c>!</c> when it is not the first hop written.
        /// </summary>
        /// <param name="hop">Hop bytes. Empty is a no-op success.</param>
        /// <param name="first"><see langword="true"/> until the first non-empty hop is written, then set to <see langword="false"/>.</param>
        /// <param name="destination">Output buffer.</param>
        /// <param name="bytesWritten">Running length. Set to 0 when the hop does not fit.</param>
        /// <returns><see langword="false"/> when <paramref name="bytesWritten"/> plus the hop and optional separator exceeds <paramref name="destination"/>.</returns>
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

        /// <summary>
        /// Takes the next <c>!</c>-separated component and trims SP and HTAB from it.
        /// </summary>
        /// <param name="remaining">Unconsumed path bytes. Advanced past the component and its separator.</param>
        /// <param name="component">Trimmed component, which may be empty between consecutive <c>!</c> bytes. The last component is the remainder.</param>
        /// <returns><see langword="false"/> only when <paramref name="remaining"/> is empty on entry.</returns>
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

        /// <summary>Drops leading and trailing SP and HTAB.</summary>
        /// <param name="value">One path component.</param>
        /// <returns>A slice of <paramref name="value"/>.</returns>
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

        /// <summary>
        /// Compares hops after folding ASCII <c>A-Z</c> to <c>a-z</c>. Other bytes are compared unchanged.
        /// </summary>
        /// <param name="left">Left hop.</param>
        /// <param name="right">Right hop.</param>
        /// <returns><see langword="false"/> when the lengths differ or any folded byte differs.</returns>
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

        /// <summary>Folds ASCII <c>A-Z</c> to <c>a-z</c>. Every other byte is returned unchanged.</summary>
        /// <param name="value">Byte to fold.</param>
        /// <returns>The folded byte.</returns>
        private static byte ToLowerAscii(byte value)
            => (uint)(value - (byte)'A') <= 'Z' - 'A' ? (byte)(value + 32) : value;
    }
}
