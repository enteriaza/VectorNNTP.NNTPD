using System.Globalization;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>DATE command (RFC 3977).</summary>
internal static class Date
{
    /// <summary>Handles <c>DATE</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        return context.Response.WriteLineAsync(NntpReplyCodes.ServerDate, stamp, cancellationToken);
    }
}
