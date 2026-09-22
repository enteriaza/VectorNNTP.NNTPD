using Microsoft.Extensions.Logging;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>
/// HELP command as defined by RFC 3977, Section 7.2.
/// </summary>
/// <remarks>
/// <para>
/// Syntax: <c>HELP</c> (no arguments). Returns a multiline <c>100</c> static syntax reference
/// for registered NNTP commands. Mandatory; not a substitute for <c>CAPABILITIES</c>.
/// </para>
/// <para>
/// The body is identical for every session state (TLS, auth, COMPRESS, mode). Trailing arguments
/// are a syntax error (<c>501</c>). Internal commands such as <c>BENCHIT</c> are not listed.
/// </para>
/// <para>
/// Notation in the body (project HELP policy; not RFC ABNF brackets):
/// <c>[name]</c> required argument; <c>{name}</c> optional; <c>a | b</c> alternatives.
/// </para>
/// </remarks>
internal static class Help
{
    private static ILogger Logger => NntpCommandLoggers.For(typeof(Help));

    /// <summary>
    /// Static HELP syntax lines (ordered for stable output). Matches pyNNTPD <c>help_model.HELP_SYNTAX_LINES</c>.
    /// </summary>
    internal static readonly IReadOnlyList<string> SyntaxLines =
    [
        "ARTICLE {message-id | article-number}",
        "AUTHINFO PASS [password]",
        "AUTHINFO SASL [mechanism] {initial-response}",
        "AUTHINFO USER [username]",
        "BODY {message-id | article-number}",
        "CAPABILITIES {keyword}",
        "CHECK [message-id]",
        "COMPRESS DEFLATE",
        "DATE",
        "GROUP [newsgroup]",
        "HDR [header] {range | message-id}",
        "XHDR [header] {range | message-id}",
        "HEAD {message-id | article-number}",
        "HELP",
        "IHAVE [message-id]",
        "LAST",
        "LIST",
        "LIST ACTIVE {wildmat}",
        "LIST ACTIVE.TIMES {wildmat}",
        "LIST COUNTS {wildmat}",
        "LIST DISTRIB.PATS",
        "LIST DISTRIBUTIONS",
        "LIST HEADERS {MSGID | RANGE}",
        "LIST MODERATORS",
        "LIST MOTD",
        "LIST NEWSGROUPS {wildmat}",
        "LIST OVERVIEW.FMT",
        "LIST SUBSCRIPTIONS {wildmat}",
        "LISTGROUP {newsgroup} {range}",
        "MODE READER",
        "MODE STREAM",
        "NEXT",
        "OVER {range | message-id}",
        "XOVER {range | message-id}",
        "POST",
        "QUIT",
        "STARTTLS",
        "STAT {message-id | article-number}",
        "TAKETHIS [message-id]",
    ];

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

        foreach (var line in SyntaxLines)
        {
            await context.Response.WriteMultilineDataAsync(line, cancellationToken).ConfigureAwait(false);
        }

        await context.Response.WriteMultilineEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
