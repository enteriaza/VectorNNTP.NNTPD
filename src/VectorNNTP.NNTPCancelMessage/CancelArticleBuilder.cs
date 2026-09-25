using System.Text;
using VectorNNTP.NNTPD.Session.Commands.Posting;

namespace VectorNNTP.NNTPCancelMessage;

/// <summary>
/// Builds a cancel control article that targets an original Message-ID.
/// </summary>
/// <remarks>
/// RFC 5536 defines Control syntax. RFC 5537 §5.3 defines CANCEL:
/// <c>Control: cancel &lt;message-id&gt;</c>. That header is authoritative.
/// RFC 5537 removed the obsolete RFC 1036 convention that <c>cmsg</c> in Subject
/// caused control-message interpretation; this builder does not emit <c>cmsg</c>.
/// RFC 1036 is retained in <c>docs/standards/rfcs/</c> for historical reference only.
/// The cancel article is itself a Netnews article with its own Message-ID from
/// <see cref="PostMessageIdFactory"/> and MUST NOT reuse the target Message-ID.
/// Newsgroups is copied from the original HEAD response so the cancel transits
/// the same groups (RFC 5537 SHOULD). Server-owned headers (Path, Injection-Date,
/// Injection-Info, X-Trace) are omitted so POST normalization can generate them.
/// </remarks>
internal static class CancelArticleBuilder
{
    public static bool TryBuild(
        string originalMessageId,
        string newsgroups,
        string from,
        DateTimeOffset nowUtc,
        out string article,
        out string cancelMessageId,
        out string error)
    {
        article = string.Empty;
        cancelMessageId = string.Empty;
        error = string.Empty;

        if (!MessageIdArgument.TryNormalize(originalMessageId, out var target, out error))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(newsgroups))
        {
            error = "Cancel refused: original Newsgroups header is absent or invalid.";
            return false;
        }

        var trimmedGroups = newsgroups.Trim();
        var parsed = new List<string>();
        if (!PostFieldSyntax.TryParseNewsgroupList(
                Encoding.ASCII.GetBytes(trimmedGroups),
                parsed,
                maxCount: 256,
                allowPoster: false)
            || parsed.Count == 0)
        {
            error = "Cancel refused: original Newsgroups header is absent or invalid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(from) || !PostFieldSyntax.IsMailbox(Encoding.ASCII.GetBytes(from)))
        {
            error = "Cancel From mailbox is invalid.";
            return false;
        }

        cancelMessageId = PostMessageIdFactory.Create();
        if (string.Equals(cancelMessageId, target, StringComparison.Ordinal))
        {
            error = "Generated cancel Message-ID collided with the target; refusing to send.";
            return false;
        }

        var date = PostRfcDate.Format(nowUtc);
        article =
            $"From: {from}\r\n" +
            $"Newsgroups: {trimmedGroups}\r\n" +
            $"Subject: cancel {target}\r\n" +
            $"Control: cancel {target}\r\n" +
            $"Message-ID: {cancelMessageId}\r\n" +
            $"Date: {date}\r\n" +
            "\r\n" +
            $"This is an administrative cancellation of {target}.\r\n";
        return true;
    }
}
