using VectorNNTP.Common.Articles.Parsing;

namespace VectorNNTP.Common.Articles;

/// <summary>
/// Compact header-value ranges into one canonical ArtData buffer.
/// </summary>
/// <remarks>
/// Ranges refer to header values as stored in ArtData (including any folded continuation
/// bytes). They do not allocate strings or group collections. Newsgroups membership is
/// answered later by tokenizing <see cref="Newsgroups"/> against peer patterns.
/// <see cref="Date"/> is the winning Date-family header value after canonical rewrite.
/// </remarks>
public readonly struct ArticleFieldTable
{
    /// <summary>
    /// Initializes a field table.
    /// </summary>
    /// <param name="messageId">Message-ID header value range.</param>
    /// <param name="newsgroups">Newsgroups header value range (ArtGroups).</param>
    /// <param name="subject">Subject header value range.</param>
    /// <param name="from">From header value range.</param>
    /// <param name="date">Winning Date-family header value range.</param>
    /// <param name="references">References header value range.</param>
    /// <param name="path">Path header value range.</param>
    public ArticleFieldTable(
        ArticleByteRange messageId,
        ArticleByteRange newsgroups,
        ArticleByteRange subject,
        ArticleByteRange from,
        ArticleByteRange date,
        ArticleByteRange references,
        ArticleByteRange path)
    {
        MessageId = messageId;
        Newsgroups = newsgroups;
        Subject = subject;
        From = from;
        Date = date;
        References = references;
        Path = path;
    }

    /// <summary>Gets the Message-ID header value range.</summary>
    public ArticleByteRange MessageId { get; }

    /// <summary>Gets the Newsgroups header value range used as ArtGroups.</summary>
    public ArticleByteRange Newsgroups { get; }

    /// <summary>Gets the Subject header value range.</summary>
    public ArticleByteRange Subject { get; }

    /// <summary>Gets the From header value range.</summary>
    public ArticleByteRange From { get; }

    /// <summary>Gets the winning Date-family header value range.</summary>
    public ArticleByteRange Date { get; }

    /// <summary>Gets the References header value range.</summary>
    public ArticleByteRange References { get; }

    /// <summary>Gets the Path header value range.</summary>
    public ArticleByteRange Path { get; }

    /// <summary>
    /// Locates overview/routing fields in canonical ArtData without allocating strings.
    /// </summary>
    /// <param name="artData">Canonical unstuffed article bytes.</param>
    /// <param name="selectedDateHeaderName">Winning Date-family header from the parser.</param>
    /// <returns>Ranges into <paramref name="artData"/>.</returns>
    public static ArticleFieldTable Locate(
        ReadOnlySpan<byte> artData,
        NntpArticleHeaderName selectedDateHeaderName)
    {
        var messageId = ArticleByteRange.Absent;
        var newsgroups = ArticleByteRange.Absent;
        var subject = ArticleByteRange.Absent;
        var from = ArticleByteRange.Absent;
        var date = ArticleByteRange.Absent;
        var references = ArticleByteRange.Absent;
        var path = ArticleByteRange.Absent;

        var index = 0;
        var currentName = NntpArticleHeaderName.Unknown;
        var currentValueOffset = -1;
        var currentValueEndExclusive = -1;
        var currentHasValue = false;
        var haveCurrent = false;

        while (index < artData.Length)
        {
            var lineEnd = FindLineTerminator(artData, index);
            var lineContentEnd = lineEnd >= 0 ? lineEnd : artData.Length;
            var lineLength = lineContentEnd - index;

            if (lineLength == 0)
            {
                CommitCurrent();
                break;
            }

            var line = artData.Slice(index, lineLength);
            var continuation = line[0] is (byte)' ' or (byte)'\t';
            if (continuation)
            {
                if (haveCurrent)
                {
                    currentHasValue = true;
                    currentValueEndExclusive = lineContentEnd;
                }

                index = AdvancePastTerminator(artData, lineEnd);
                continue;
            }

            CommitCurrent();

            var colonIndex = line.IndexOf((byte)':');
            if (colonIndex <= 0)
            {
                break;
            }

            currentName = ClassifyKnownHeaderName(artData.Slice(index, colonIndex));
            var valueStartInLine = colonIndex + 1;
            while (valueStartInLine < line.Length && line[valueStartInLine] is (byte)' ' or (byte)'\t')
            {
                valueStartInLine++;
            }

            var valueEndInLine = line.Length;
            while (valueEndInLine > valueStartInLine && line[valueEndInLine - 1] is (byte)' ' or (byte)'\t')
            {
                valueEndInLine--;
            }

            currentValueOffset = index + valueStartInLine;
            currentValueEndExclusive = index + valueEndInLine;
            currentHasValue = valueEndInLine > valueStartInLine;
            haveCurrent = true;
            index = AdvancePastTerminator(artData, lineEnd);
        }

        CommitCurrent();
        return new ArticleFieldTable(messageId, newsgroups, subject, from, date, references, path);

        void CommitCurrent()
        {
            if (!haveCurrent)
            {
                return;
            }

            var length = currentHasValue ? currentValueEndExclusive - currentValueOffset : 0;
            var range = new ArticleByteRange(currentValueOffset, length);
            switch (currentName)
            {
                case NntpArticleHeaderName.MessageId when !messageId.IsPresent:
                    messageId = range;
                    break;
                case NntpArticleHeaderName.Newsgroups when !newsgroups.IsPresent:
                    newsgroups = range;
                    break;
                case NntpArticleHeaderName.Subject when !subject.IsPresent:
                    subject = range;
                    break;
                case NntpArticleHeaderName.From when !from.IsPresent:
                    from = range;
                    break;
                case NntpArticleHeaderName.References when !references.IsPresent:
                    references = range;
                    break;
                case NntpArticleHeaderName.Path when !path.IsPresent:
                    path = range;
                    break;
                default:
                    if (currentName == selectedDateHeaderName && selectedDateHeaderName != NntpArticleHeaderName.Unknown && !date.IsPresent)
                    {
                        date = range;
                    }

                    break;
            }

            haveCurrent = false;
            currentName = NntpArticleHeaderName.Unknown;
            currentValueOffset = -1;
            currentValueEndExclusive = -1;
            currentHasValue = false;
        }
    }

    private static NntpArticleHeaderName ClassifyKnownHeaderName(ReadOnlySpan<byte> nameBytes)
        => AsciiEqualsIgnoreCase(nameBytes, "Date"u8)
            ? NntpArticleHeaderName.Date
            : AsciiEqualsIgnoreCase(nameBytes, "Injection-Date"u8)
                ? NntpArticleHeaderName.InjectionDate
                : AsciiEqualsIgnoreCase(nameBytes, "NNTP-Posting-Date"u8)
                    ? NntpArticleHeaderName.NntpPostingDate
                    : AsciiEqualsIgnoreCase(nameBytes, "Posted"u8)
                        ? NntpArticleHeaderName.Posted
                        : AsciiEqualsIgnoreCase(nameBytes, "X-Date"u8)
                            ? NntpArticleHeaderName.XDate
                            : AsciiEqualsIgnoreCase(nameBytes, "Delivery-Date"u8)
                                ? NntpArticleHeaderName.DeliveryDate
                                : AsciiEqualsIgnoreCase(nameBytes, "Path"u8)
                                    ? NntpArticleHeaderName.Path
                                    : AsciiEqualsIgnoreCase(nameBytes, "Message-ID"u8)
                                        ? NntpArticleHeaderName.MessageId
                                        : AsciiEqualsIgnoreCase(nameBytes, "Newsgroups"u8)
                                            ? NntpArticleHeaderName.Newsgroups
                                            : AsciiEqualsIgnoreCase(nameBytes, "From"u8)
                                                ? NntpArticleHeaderName.From
                                                : AsciiEqualsIgnoreCase(nameBytes, "Subject"u8)
                                                    ? NntpArticleHeaderName.Subject
                                                    : AsciiEqualsIgnoreCase(nameBytes, "References"u8)
                                                        ? NntpArticleHeaderName.References
                                                        : NntpArticleHeaderName.Unknown;

    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            var a = left[i];
            var b = right[i];
            if ((uint)(a - (byte)'A') <= 'Z' - 'A')
            {
                a = (byte)(a + 32);
            }

            if ((uint)(b - (byte)'A') <= 'Z' - 'A')
            {
                b = (byte)(b + 32);
            }

            if (a != b)
            {
                return false;
            }
        }

        return true;
    }

    private static int FindLineTerminator(ReadOnlySpan<byte> buffer, int start)
    {
        for (var i = start; i < buffer.Length; i++)
        {
            if (buffer[i] is (byte)'\r' or (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static int AdvancePastTerminator(ReadOnlySpan<byte> buffer, int lineTerminatorIndex)
        => lineTerminatorIndex < 0 || lineTerminatorIndex >= buffer.Length
            ? buffer.Length
            : buffer[lineTerminatorIndex] == (byte)'\r'
              && lineTerminatorIndex + 1 < buffer.Length
              && buffer[lineTerminatorIndex + 1] == (byte)'\n'
                ? lineTerminatorIndex + 2
                : lineTerminatorIndex + 1;
}
