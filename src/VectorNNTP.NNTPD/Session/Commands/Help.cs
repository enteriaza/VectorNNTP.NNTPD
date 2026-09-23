namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// HELP command as defined by RFC 3977, Section 7.2.
/// </summary>
/// <remarks>
/// <para>
/// Syntax: <c>HELP</c> (no arguments). Returns a multiline <c>100</c> static syntax reference
/// for registered NNTP commands, followed by concise RANGE and WILDMAT explanations
/// (RFC 3977 §6.1.2 / §8.3 / §8.5 and §4). Mandatory; not a substitute for <c>CAPABILITIES</c>.
/// </para>
/// <para>
/// The body is identical for every session state (TLS, auth, COMPRESS, mode). Trailing arguments
/// are a syntax error (<c>501</c>). Internal commands such as <c>BENCHIT</c> are not listed.
/// </para>
/// <para>
/// Compact HELP syntax uses ABNF-style notation where applicable:
/// <c>[element]</c> denotes an optional element and <c>/</c> denotes alternatives.
/// Required arguments appear without brackets. These lines are compact human-readable
/// summaries of the command forms a client should use, not complete standalone ABNF productions.
/// </para>
/// </remarks>
internal static class Help
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(Help));

    /// <summary>
    /// Static HELP syntax lines for the supported public command surface.
    /// </summary>
    /// <remarks>
    /// LIST keywords ACTIVE.TIMES, COUNTS, DISTRIB.PATS, DISTRIBUTIONS, MODERATORS, and
    /// SUBSCRIPTIONS are intentionally omitted — VectorNNTP.NNTPD does not support them.
    /// </remarks>
    internal static readonly IReadOnlyList<string> SyntaxLines =
    [
        "ARTICLE [message-id / article-number]",
        "AUTHINFO PASS password",
        "AUTHINFO SASL mechanism [initial-response]",
        "AUTHINFO USER username",
        "BODY [message-id / article-number]",
        "CAPABILITIES [keyword]",
        "CHECK message-id",
        "COMPRESS DEFLATE",
        "DATE",
        "GROUP newsgroup",
        "HDR header [range / message-id]",
        "HEAD [message-id / article-number]",
        "HELP",
        "IHAVE message-id",
        "LAST",
        "LIST",
        "LIST ACTIVE [wildmat]",
        "LIST HEADERS [MSGID / RANGE]",
        "LIST MOTD",
        "LIST NEWSGROUPS [wildmat]",
        "LIST OVERVIEW.FMT",
        "LISTGROUP [newsgroup [range]]",
        "MODE READER",
        "MODE STREAM",
        "NEXT",
        "OVER [range / message-id]",
        "POST",
        "QUIT",
        "STARTTLS",
        "STAT [message-id / article-number]",
        "TAKETHIS message-id",
        "XHDR header [range / message-id]",
        "XOVER [range]",
    ];

    /// <summary>
    /// Static RANGE explanation (RFC 3977 §6.1.2, §8.3, §8.5; ABNF <c>range</c> in §9.2).
    /// </summary>
    internal static readonly IReadOnlyList<string> RangeHelpLines =
    [
        "Range formats:",
        "Where HELP shows \"range\" (LISTGROUP, HDR, OVER, XOVER):",
        "123       a single article number",
        "123-456   articles from 123 through 456 inclusive",
        "123-      article 123 and all following article numbers",
        "If the high number is less than the low number, the range contains no articles.",
    ];

    /// <summary>
    /// Static WILDMAT explanation (RFC 3977 §4; LIST ACTIVE wildmat example in §7.6.3).
    /// </summary>
    /// <remarks>
    /// Character classes and backslash escapes are intentionally omitted: RFC 3977 §4.1
    /// excludes <c>,</c>, <c>\</c>, <c>[</c>, and <c>]</c> from wildmats.
    /// </remarks>
    internal static readonly IReadOnlyList<string> WildmatHelpLines =
    [
        "Wildmat formats:",
        "Where HELP shows \"wildmat\" (e.g. LIST ACTIVE, LIST NEWSGROUPS); matches a complete name:",
        "*         matches zero or more characters",
        "?         matches exactly one character",
        "Other permitted characters match themselves (case-sensitive).",
        "Patterns may be comma-separated; a leading \"!\" negates that pattern.",
        "The rightmost matching pattern decides whether the wildmat matches.",
        "The characters comma, backslash, and square brackets are not allowed in wildmats.",
        "Examples: a* ; a*,!*b ; *.recovery",
    ];

    /// <summary>
    /// Full HELP multiline body: command syntax, then RANGE and WILDMAT sections
    /// separated by blank lines.
    /// </summary>
    internal static readonly IReadOnlyList<string> BodyLines = CreateBodyLines();

    private static IReadOnlyList<string> CreateBodyLines()
    {
        var lines = new List<string>(SyntaxLines.Count + RangeHelpLines.Count + WildmatHelpLines.Count + 2);
        lines.AddRange(SyntaxLines);
        lines.Add(string.Empty);
        lines.AddRange(RangeHelpLines);
        lines.Add(string.Empty);
        lines.AddRange(WildmatHelpLines);
        return lines;
    }

    /// <summary>Handles <c>HELP</c> (RFC 3977 §7.2).</summary>
    public static ValueTask HandleAsync(NntpCommandContext context, CancellationToken cancellationToken) =>
        NntpCommandExecution.RunAsync(Logger, context, "HELP", ExecuteAsync, cancellationToken);

    private static async ValueTask ExecuteAsync(NntpCommandContext context, CancellationToken cancellationToken)
    {
        if (context.Arguments.Count > 0)
        {
            await context.Response
                .WriteLineAsync(NntpReplyCodes.SyntaxError, "Syntax error", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await context.Response
            .WriteMultilineStartAsync(NntpReplyCodes.HelpTextFollows, "Help text follows", cancellationToken)
            .ConfigureAwait(false);

        foreach (var line in BodyLines)
        {
            await context.Response.WriteMultilineDataAsync(line, cancellationToken).ConfigureAwait(false);
        }

        await context.Response.WriteMultilineEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
