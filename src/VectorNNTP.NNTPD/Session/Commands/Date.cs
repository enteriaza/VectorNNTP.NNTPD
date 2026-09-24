using System.Globalization;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// DATE command as defined by RFC 3977, Section 7.1.
/// </summary>
/// <remarks>
/// Returns the current UTC server date and time as <c>YYYYMMDDhhmmss</c>.
/// The stamp is formatted into an owned 20-octet wire line
/// (<c>111 </c> + 14 digits + CRLF) without a protocol string round-trip.
/// </remarks>
internal static class Date
{
    private const int DateWireLength = 20;
    private const int StampLength = 14;

    private static ILogger Logger => NntpCommandLoggers.For(typeof(Date));

    /// <summary>Handles <c>DATE</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "DATE", ExecuteAsync, cancellationToken);

    private static ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        var owned = FormatUtcDateLine(DateTime.UtcNow);
        return context.Response.WriteLineAsync(owned, cancellationToken);
    }

    /// <summary>
    /// Builds <c>111 YYYYMMDDhhmmss\r\n</c> as one owned buffer using
    /// <c>DateTime.TryFormat</c> so UTC stamp semantics stay on the framework formatter.
    /// </summary>
    internal static byte[] FormatUtcDateLine(DateTime utc)
    {
        Span<char> stamp = stackalloc char[StampLength];
        if (!utc.TryFormat(stamp, out var written, "yyyyMMddHHmmss", CultureInfo.InvariantCulture)
            || written != StampLength)
        {
            throw new InvalidOperationException("DATE stamp formatting failed.");
        }

        var wire = new byte[DateWireLength];
        NntpResponses.DatePrefix.Span.CopyTo(wire);
        for (var i = 0; i < StampLength; i++)
        {
            wire[NntpResponses.DatePrefix.Length + i] = (byte)stamp[i];
        }

        NntpResponses.Crlf.Span.CopyTo(wire.AsSpan(NntpResponses.DatePrefix.Length + StampLength));
        return wire;
    }
}
