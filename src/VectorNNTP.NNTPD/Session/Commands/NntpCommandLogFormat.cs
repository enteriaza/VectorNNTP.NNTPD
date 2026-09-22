using System.Globalization;
using VectorNNTP.NNTPD.Session;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>Formats effective client identity and redacts secrets for NNTP command RX/TX logs.</summary>
internal static class NntpCommandLogFormat
{
    /// <summary>Formats <c>src-ip:src-port</c> from the session's effective client identity.</summary>
    public static string Client(NntpSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{session.ClientAddress}:{session.ClientPort}");
    }

    /// <summary>
    /// Returns a log-safe RX command line. Passwords and SASL credential material are redacted.
    /// </summary>
    public static string RedactRxLine(string rawLine)
    {
        ArgumentNullException.ThrowIfNull(rawLine);
        if (rawLine.Length == 0)
        {
            return rawLine;
        }

        // AUTHINFO PASS <secret> → AUTHINFO PASS <redacted>
        if (StartsWithCommand(rawLine, "AUTHINFO PASS"))
        {
            return "AUTHINFO PASS <redacted>";
        }

        // AUTHINFO SASL may carry an initial response with credentials.
        if (StartsWithCommand(rawLine, "AUTHINFO SASL"))
        {
            return "AUTHINFO SASL <redacted>";
        }

        return rawLine;
    }

    /// <summary>Extracts a display command name from a raw line when resolution is unavailable.</summary>
    public static string DisplayNameFromRawLine(string rawLine)
    {
        ArgumentNullException.ThrowIfNull(rawLine);
        if (rawLine.Length == 0)
        {
            return "INVALID";
        }

        if (StartsWithCommand(rawLine, "AUTHINFO PASS"))
        {
            return "AUTHINFO PASS";
        }

        if (StartsWithCommand(rawLine, "AUTHINFO SASL"))
        {
            return "AUTHINFO SASL";
        }

        if (StartsWithCommand(rawLine, "AUTHINFO USER"))
        {
            return "AUTHINFO USER";
        }

        if (StartsWithCommand(rawLine, "MODE READER"))
        {
            return "MODE READER";
        }

        if (StartsWithCommand(rawLine, "MODE STREAM"))
        {
            return "MODE STREAM";
        }

        if (StartsWithCommand(rawLine, "COMPRESS"))
        {
            return "COMPRESS";
        }

        var span = rawLine.AsSpan().Trim();
        var space = span.IndexOf(' ');
        return space <= 0 ? span.ToString().ToUpperInvariant() : span[..space].ToString().ToUpperInvariant();
    }

    private static bool StartsWithCommand(string line, string command)
    {
        if (line.Length < command.Length)
        {
            return false;
        }

        if (!line.AsSpan(0, command.Length).Equals(command, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return line.Length == command.Length || char.IsWhiteSpace(line[command.Length]);
    }
}
