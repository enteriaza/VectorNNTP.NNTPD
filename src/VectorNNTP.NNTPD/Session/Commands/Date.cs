using System.Globalization;
using Microsoft.Extensions.Logging;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// DATE command as defined by RFC 3977, Section 7.1.
/// </summary>
/// <remarks>
/// Returns the current UTC server date and time as <c>YYYYMMDDhhmmss</c>.
/// </remarks>
internal static class Date
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(Date));

    /// <summary>Handles <c>DATE</c>.</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "DATE", ExecuteAsync, cancellationToken);

    private static ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        return context.Response.WriteLineAsync(NntpReplyCodes.ServerDate, stamp, cancellationToken);
    }
}
