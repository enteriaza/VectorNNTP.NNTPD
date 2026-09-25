using System.Text;

namespace VectorNNTP.NNTPCancelMessage.PgpVerify;

/// <summary>
/// Builds the PGPVERIFY signed-data byte sequence hashed by INN <c>pgpverify</c>
/// detached verification.
/// </summary>
/// <remarks>
/// <para>
/// PGPVERIFY FORMAT (David Lawrence) constructs each signed header as
/// <c>Name: </c> (colon + space), including an empty Sender as
/// <c>Sender: </c> + EOL. FORMAT does not strip that space.
/// </para>
/// <para>
/// INN <c>control/pgpverify.in</c> 1.23 through 1.31 reconstructs those same
/// lines, then applies <c>$message =~ s/[ \t]+\n/\n/g</c> before detached
/// verification (documented in INN as compatibility with historical attached
/// signatures). Empty Sender therefore hashes as <c>Sender:\n</c>.
/// </para>
/// <para>
/// VectorNNTP follows that INN detached-verification rule so CANCEL controls
/// interoperate with deployed INN. This does not amend FORMAT. This is not
/// PGP/MIME and is not an RFC 5537 protocol.
/// </para>
/// </remarks>
internal static class PgpVerifyCanonicalizer
{
    /// <summary>
    /// Header names signed for VectorNNTP CANCEL, in FORMAT/X-PGP-Sig order.
    /// </summary>
    /// <remarks>
    /// FORMAT's <c>X-PGP-Sig</c> example lists
    /// <c>Subject,Control,Message-ID,Date,From,Sender</c>.
    /// <c>Newsgroups</c>, <c>Path</c>, <c>Approved</c>, <c>Injection-Date</c>,
    /// <c>Injection-Info</c>, and <c>X-Trace</c> are added after signing (or by
    /// the server) and are not in this list.
    /// </remarks>
    public static IReadOnlyList<string> CancelSignedHeaderNames { get; } =
    [
        "Subject",
        "Control",
        "Message-ID",
        "Date",
        "From",
        "Sender",
    ];

    /// <summary>Comma-separated header list written into <c>X-PGP-Sig</c> (no spaces).</summary>
    public static string CancelSignedHeaderList { get; } = string.Join(",", CancelSignedHeaderNames);

    /// <summary>
    /// Canonicalizes signed headers and body using Unix LF, FORMAT colon-space
    /// construction, missing headers as empty signed fields, then INN's
    /// trailing SP/HT-before-LF strip.
    /// </summary>
    public static byte[] Canonicalize(IReadOnlyList<string> headerNames, IReadOnlyDictionary<string, string> values, string body)
    {
        ArgumentNullException.ThrowIfNull(headerNames);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(body);

        var sb = new StringBuilder();
        sb.Append("X-Signed-Headers: ");
        sb.Append(string.Join(",", headerNames));
        sb.Append('\n');
        foreach (var name in headerNames)
        {
            sb.Append(name);
            sb.Append(": ");
            if (values.TryGetValue(name, out var value) && value.Length > 0)
            {
                sb.Append(value);
            }

            sb.Append('\n');
        }

        sb.Append('\n');
        sb.Append(NormalizeLineEndings(body));
        return Encoding.UTF8.GetBytes(StripTrailingSpAndHtBeforeLf(sb.ToString()));
    }

    /// <summary>Reconstructs signed data from an already-built NNTP article (CRLF headers).</summary>
    public static byte[] CanonicalizeArticle(string article, IReadOnlyList<string> headerNames)
    {
        ArgumentException.ThrowIfNullOrEmpty(article);
        ArgumentNullException.ThrowIfNull(headerNames);
        var parsed = PgpVerifyArticleParser.Parse(article);
        return Canonicalize(headerNames, parsed.Headers, parsed.Body);
    }

    /// <summary>
    /// Removes SP/HT runs immediately before LF. Equivalent to INN
    /// <c>$message =~ s/[ \t]+\n/\n/g</c>. Does not change leading or internal
    /// whitespace, and does not strip SP/HT that are not followed by LF.
    /// </summary>
    internal static string StripTrailingSpAndHtBeforeLf(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var buffer = new char[message.Length];
        var written = 0;
        foreach (var c in message)
        {
            if (c == '\n')
            {
                while (written > 0 && (buffer[written - 1] == ' ' || buffer[written - 1] == '\t'))
                {
                    written--;
                }
            }

            buffer[written++] = c;
        }

        return written == message.Length ? message : new string(buffer, 0, written);
    }

    private static string NormalizeLineEndings(string body)
    {
        return body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }
}
