namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>Parsed NNTP command line (verb + tokens).</summary>
public readonly struct NntpParsedCommand
{
    /// <summary>Initializes a new instance of the <see cref="NntpParsedCommand"/> struct.</summary>
    public NntpParsedCommand(string rawLine, string verb, IReadOnlyList<string> tokens)
    {
        RawLine = rawLine;
        Verb = verb;
        Tokens = tokens;
    }

    /// <summary>Gets the raw line without CRLF.</summary>
    public string RawLine { get; }

    /// <summary>Gets the command verb in uppercase.</summary>
    public string Verb { get; }

    /// <summary>Gets all tokens after the verb (original casing preserved for arguments).</summary>
    public IReadOnlyList<string> Tokens { get; }
}

/// <summary>Splits an NNTP command line into verb and tokens (RFC 3977 basic tokenization).</summary>
public static class NntpCommandParser
{
    /// <summary>
    /// Parses a single command line. Empty lines and lines that are only whitespace are invalid.
    /// </summary>
    public static bool TryParse(string line, out NntpParsedCommand command)
    {
        command = default;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        // Strip a single trailing CR if a bare LF reader left it.
        if (line.EndsWith('\r'))
        {
            line = line[..^1];
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var verb = parts[0].ToUpperInvariant();
        if (verb.Length == 0)
        {
            return false;
        }

        IReadOnlyList<string> tokens = parts.Length == 1
            ? Array.Empty<string>()
            : parts.AsSpan(1).ToArray();
        command = new NntpParsedCommand(line, verb, tokens);
        return true;
    }
}
