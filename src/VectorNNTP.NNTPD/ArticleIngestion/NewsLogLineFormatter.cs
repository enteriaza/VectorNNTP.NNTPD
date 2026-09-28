namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>
/// Serializes one INN <c>news</c> line per innd(8) LOGGING / <c>ARTlog</c>.
/// </summary>
/// <remarks>
/// Verified INN field order (innd.pod LOGGING and <c>innd/art.c</c> <c>ARTlog</c>):
/// <c>mon dd hh:mm:ss.mmm disposition feedsite message-id [sites...|reason]</c>.
/// Timestamp uses English month abbreviations and local wall time at millisecond
/// resolution. Feed is INN's unavailable token <c>?</c> when empty. Rejected
/// and junk lines append the already-decided reason only; the NNTP response
/// code is not a news field. Site tokens are omitted when none are supplied
/// and are not written on <c>-</c> or <c>j</c> lines. Optional INN hostname/size
/// fields are not emitted.
/// Disposition characters and field order are hard-coded.
/// </remarks>
internal static class NewsLogLineFormatter
{
    private static ReadOnlySpan<byte> Months => "JanFebMarAprMayJunJulAugSepOctNovDec"u8;

    private static ReadOnlySpan<byte> UnknownFeed => "?"u8;

    /// <summary>Formats <paramref name="evt"/> into <paramref name="destination"/> as one LF-terminated line.</summary>
    /// <returns>Bytes written.</returns>
    public static int Write(Span<byte> destination, in NewsLogEvent evt, DateTimeOffset timestamp)
    {
        var local = timestamp.ToOffset(timestamp.Offset);
        var dt = local.DateTime;
        var written = 0;
        written += WriteMonth(destination[written..], dt.Month);
        destination[written++] = (byte)' ';
        written += WriteSpacePaddedDay(destination[written..], dt.Day);
        destination[written++] = (byte)' ';
        written += WriteTwoDigits(destination[written..], dt.Hour);
        destination[written++] = (byte)':';
        written += WriteTwoDigits(destination[written..], dt.Minute);
        destination[written++] = (byte)':';
        written += WriteTwoDigits(destination[written..], dt.Second);
        destination[written++] = (byte)'.';
        written += WriteThreeDigits(destination[written..], dt.Millisecond);
        destination[written++] = (byte)' ';
        destination[written++] = (byte)evt.Disposition;
        destination[written++] = (byte)' ';
        var feed = evt.Feed.IsEmpty ? UnknownFeed : evt.Feed.Span;
        feed.CopyTo(destination[written..]);
        written += feed.Length;
        destination[written++] = (byte)' ';
        evt.MessageId.Span.CopyTo(destination[written..]);
        written += evt.MessageId.Length;
        if (WritesReason(evt.Disposition))
        {
            if (!evt.Reason.IsEmpty)
            {
                destination[written++] = (byte)' ';
                evt.Reason.Span.CopyTo(destination[written..]);
                written += evt.Reason.Length;
            }
        }
        else if (!evt.Sites.IsEmpty)
        {
            destination[written++] = (byte)' ';
            evt.Sites.Span.CopyTo(destination[written..]);
            written += evt.Sites.Length;
        }

        destination[written++] = (byte)'\n';
        return written;
    }

    /// <summary>Byte count required to format <paramref name="evt"/> including the trailing LF.</summary>
    public static int RequiredLength(in NewsLogEvent evt)
    {
        var feedLength = evt.Feed.IsEmpty ? 1 : evt.Feed.Length;
        if (WritesReason(evt.Disposition))
        {
            var reason = evt.Reason.IsEmpty ? 0 : 1 + evt.Reason.Length;
            return 24 + feedLength + evt.MessageId.Length + reason;
        }

        var sites = evt.Sites.IsEmpty ? 0 : 1 + evt.Sites.Length;
        return 24 + feedLength + evt.MessageId.Length + sites;
    }

    private static bool WritesReason(NewsLogDisposition disposition) =>
        disposition is NewsLogDisposition.Rejected or NewsLogDisposition.Junk;

    private static int WriteMonth(Span<byte> dest, int month)
    {
        var index = (month - 1) * 3;
        Months.Slice(index, 3).CopyTo(dest);
        return 3;
    }

    private static int WriteSpacePaddedDay(Span<byte> dest, int day)
    {
        if (day < 10)
        {
            dest[0] = (byte)' ';
            dest[1] = (byte)('0' + day);
        }
        else
        {
            dest[0] = (byte)('0' + (day / 10));
            dest[1] = (byte)('0' + (day % 10));
        }

        return 2;
    }

    private static int WriteTwoDigits(Span<byte> dest, int value)
    {
        dest[0] = (byte)('0' + (value / 10));
        dest[1] = (byte)('0' + (value % 10));
        return 2;
    }

    private static int WriteThreeDigits(Span<byte> dest, int value)
    {
        dest[0] = (byte)('0' + (value / 100));
        dest[1] = (byte)('0' + ((value / 10) % 10));
        dest[2] = (byte)('0' + (value % 10));
        return 3;
    }
}
