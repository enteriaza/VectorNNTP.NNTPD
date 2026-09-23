using VectorNNTP.NNTPD.Networking.Transport;
using VectorNNTP.NNTPD.Session.Framing;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>Per-command execution context supplied to handlers.</summary>
public sealed class NntpCommandContext
{
    /// <summary>Initializes a new instance of the <see cref="NntpCommandContext"/> class.</summary>
    public NntpCommandContext(
        NntpSession session,
        NntpCommandDescriptor descriptor,
        string rawLine,
        IReadOnlyList<string> arguments,
        NntpResponseWriter response,
        NntpMultilineReadResult? preReadArticle = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(rawLine);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(response);
        Session = session;
        Descriptor = descriptor;
        RawLine = rawLine;
        Arguments = arguments;
        Response = response;
        PreReadArticle = preReadArticle;
    }

    /// <summary>Gets the owning session.</summary>
    public NntpSession Session { get; }

    /// <summary>Gets the matched command descriptor.</summary>
    public NntpCommandDescriptor Descriptor { get; }

    /// <summary>Gets the raw command line (without trailing CRLF).</summary>
    public string RawLine { get; }

    /// <summary>
    /// Gets arguments after the verb (and after the subcommand when the descriptor includes one).
    /// </summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Gets the response writer for this command.</summary>
    public NntpResponseWriter Response { get; }

    /// <summary>
    /// Gets an article already consumed by the continuous RX scanner, when the session
    /// pre-read a TAKETHIS body so the handler must not read the Pipe again.
    /// </summary>
    public NntpMultilineReadResult? PreReadArticle { get; }

    /// <summary>
    /// Optional TX completion detail set by the handler after successful work
    /// (for example negotiated TLS parameters after STARTTLS).
    /// </summary>
    public string? CompletionDetail { get; set; }

    /// <summary>Gets the underlying transport connection.</summary>
    public INntpConnection Connection => Session.Connection;
}
