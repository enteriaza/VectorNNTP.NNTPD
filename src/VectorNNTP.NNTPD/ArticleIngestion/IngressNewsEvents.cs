using System.Text;
using VectorNNTP.NNTPD.Session;

namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>
/// Builds already-decided news events at ingress decision points.
/// </summary>
internal static class IngressNewsEvents
{
    /// <summary>WantTrash=false reason for unknown/non-carried Newsgroups.</summary>
    internal const string NewsgroupNotCarried = IngressNewsReasons.NewsgroupNotCarried;

    /// <summary>Writes a rejection news event. Failures do not change protocol state.</summary>
    public static void TryWriteRejected(
        NntpSession session,
        string? messageId,
        int responseCode,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.TryWriteNews(
            new NewsLogEvent(
                NewsLogDisposition.Rejected,
                Ascii(messageId),
                timestamp: session.Time.GetLocalNow(),
                responseCode: responseCode,
                reason: Ascii(reason)));
    }

    /// <summary>Writes an accepted news event for a path that never enters the queue.</summary>
    public static void TryWriteAccepted(NntpSession session, string? messageId)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.TryWriteNews(
            new NewsLogEvent(
                NewsLogDisposition.Accepted,
                Ascii(messageId),
                timestamp: session.Time.GetLocalNow()));
    }

    /// <summary>Writes a moderated news event for a successful POST moderation submission.</summary>
    public static void TryWriteModerated(NntpSession session, string? messageId)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.TryWriteNews(
            new NewsLogEvent(
                NewsLogDisposition.Moderated,
                Ascii(messageId),
                timestamp: session.Time.GetLocalNow()));
    }

    private static ReadOnlyMemory<byte> Ascii(string? value) =>
        string.IsNullOrEmpty(value) ? default : Encoding.ASCII.GetBytes(value);
}
