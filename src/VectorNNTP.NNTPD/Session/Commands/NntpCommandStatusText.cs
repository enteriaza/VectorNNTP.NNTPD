using System.Text;
using VectorNNTP.NNTPD.History;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// Debug-only semantic TX status lines for dynamic CHECK/TAKETHIS/DATE replies.
/// </summary>
internal static class NntpCommandStatusText
{
    /// <summary>
    /// Formats the CHECK status line from the lookup result and Message-ID octets.
    /// Call only after <see cref="ILogger.IsEnabled"/>.
    /// </summary>
    internal static string FormatCheck(HistoryLookupResult result, ReadOnlySpan<byte> messageId)
    {
        var id = Encoding.ASCII.GetString(messageId);
        return result switch
        {
            HistoryLookupResult.Seen => "438 " + id,
            HistoryLookupResult.Unavailable => "431 " + id,
            _ => "238 " + id + " send article to be transferred",
        };
    }

    /// <summary>
    /// Formats TAKETHIS <c>239 message-id</c>. Call only after <see cref="ILogger.IsEnabled"/>.
    /// </summary>
    internal static string FormatTakeThisAccepted(ReadOnlySpan<byte> messageId) =>
        "239 " + Encoding.ASCII.GetString(messageId);

    /// <summary>
    /// Formats TAKETHIS <c>439 message-id</c>. Call only after <see cref="ILogger.IsEnabled"/>.
    /// </summary>
    internal static string FormatTakeThisRejected(ReadOnlySpan<byte> messageId) =>
        "439 " + Encoding.ASCII.GetString(messageId);

    /// <summary>
    /// Formats <c>111 YYYYMMDDhhmmss</c> from the same stamp characters used for the wire line.
    /// Call only after <see cref="ILogger.IsEnabled"/>.
    /// </summary>
    internal static string FormatDate(ReadOnlySpan<char> stamp) => "111 " + stamp.ToString();
}
