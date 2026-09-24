using System.Text;
using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Session.Commands;

namespace VectorNNTP.NNTPD.Tests.Session;

internal static class NntpCommandTestParse
{
    public static (NntpCommand Command, byte[] Line) Parse(string text)
    {
        var line = Encoding.ASCII.GetBytes(text);
        return (NntpCommandParser.Parse(line), line);
    }

    public static NntpCommand ParseCommand(string text) => Parse(text).Command;

    public static ValueTask DispatchAsync(
        NntpCommandDispatcher dispatcher,
        NntpSession session,
        NntpResponseWriter response,
        string text,
        CancellationToken cancellationToken = default)
    {
        var (command, line) = Parse(text);
        return dispatcher.DispatchAsync(session, command, line, response, cancellationToken);
    }
}
