using System.Globalization;

namespace VectorNNTP.StorageSmoke;

/// <summary>Parses smoke and soak arguments. Invalid input does not substitute another target or directory.</summary>
internal static class CommandLine
{
    internal static string HelpText { get; } =
        """
        VectorNNTP.StorageSmoke — direct VATP client for a StorageServer.

        The client offers articles to the listener. VATP Accepted means the server admitted the
        article to its in-memory queue. It is not durable spool publication and it is not a SQLite
        index insert. Those counts are not available to this client. On Windows, monitor-windows.ps1
        can sample the server log and the physical disk into the same results directory.

        Commands
          help                         Show this text.
          smoke --results <dir>       Offer a bounded number of articles, then print a summary.
          soak --results <dir> --seconds <n>
                                       Offer until the duration elapses, then print a summary.

        The soak also accepts the positional form used by the earlier harness:
          soak <seconds> <sessions> <bodyBytes> <resultsDirectory>
        Positional form uses the outstanding, host, and port defaults below. Do not mix it with flags.

        Options
          --results <dir>              Required. Created if missing. Refused if client.csv, run.txt,
                                       or stop.request is already there. Keep this off the spool and
                                       the SQLite directory.
          --seconds <n>                Soak only. Whole seconds from 1 to 21600. No default.
          --articles <n>               Smoke only. Offered articles from 1 to 10000. Default 4.
          --sessions <n>               Parallel VATP sessions from 1 to 16. Default 4.
          --outstanding <n>            Outstanding STORE operations on each session. Only 1 or 2.
                                       Default 2.
          --body-bytes <n>             Article body size in bytes, from 64 to 2000000. Default 768000.
          --host <fqdn>                TLS and TCP target. Default cache01.usenet.ninja.
                                       An omitted host uses that default and prints it. An invalid
                                       host or port fails. The client does not try another server.
          --port <n>                   VATP TLS port from 1 to 65535. Default 1191.

        Units and files
          Timestamps in client.csv and run.txt are UTC (round-trip ISO-8601).
          client.csv counters are cumulative totals, not interval rates. A row is written at start,
          about every 10 seconds, and again at exit. Subtract two rows and divide by the timestamp
          gap for an interval rate. acceptedBytes is the sum of article sizes for VATP Accepted
          results only.
          Columns: utc, offered, accepted, duplicate, rejected, failed, ambiguous, cancelled,
          acceptedBytes.
          accepted    VATP queue admission.
          duplicate   The server already held the same article.
          rejected    Capacity, pressure, invalid, or conflict.
          failed      Local construction failure, transport failure, or no dial.
          ambiguous   END was written and RESULT was not observed. The queue may already hold it.
          cancelled   The attempt was cancelled before acceptance.
          Creating stop.request in the results directory ends the run. A second Ctrl+C exits immediately.
          The process keeps its own deadline. It does not stop because a terminal host timed out.

        Path
          Generated articles start as Path: peer.example. Normalize rewrites the bytes offered to
          VATP as Path: news.usenet.ninja!peer.example. The parser identity is that organizational
          tracker and is not written as an application hop. StorageServer traverse is expected to
          store Path: cache01.usenet.ninja!news.usenet.ninja!peer.example when its runtime identity
          is cache01.usenet.ninja. This client does not insert nntpd01.usenet.ninja.

        Examples
          dotnet run -c Release --project tools/storage-smoke -- help
          dotnet run -c Release --project tools/storage-smoke -- smoke --results D:\vatp-smoke\run1
          dotnet run -c Release --project tools/storage-smoke -- soak --seconds 18000 --sessions 4 --outstanding 2 --body-bytes 768000 --results D:\vatp-soak\run1
          dotnet run -c Release --project tools/storage-smoke -- soak 18000 4 768000 D:\vatp-soak\run1

        Start a long soak in its own process so it is not tied to a short command timeout:
          Start-Process dotnet -ArgumentList 'run','-c','Release','--project','tools/storage-smoke','--','soak','18000','4','768000','D:\vatp-soak\run1'
        """;

    internal static ParseResult Parse(string[] args, TextWriter error)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            return ParseResult.Help();
        }

        if (string.Equals(args[0], "smoke", StringComparison.Ordinal))
        {
            return ParseNamed(SmokeMode.Smoke, args.AsSpan(1), error);
        }

        if (string.Equals(args[0], "soak", StringComparison.Ordinal))
        {
            if (args.Length == 5 && !args[1].StartsWith('-'))
            {
                return ParsePositionalSoak(args, error);
            }

            return ParseNamed(SmokeMode.Soak, args.AsSpan(1), error);
        }

        error.WriteLine($"Unknown command '{args[0]}'. Run with help.");
        return ParseResult.Invalid();
    }

    private static bool IsHelp(string value) =>
        string.Equals(value, "help", StringComparison.Ordinal)
        || string.Equals(value, "--help", StringComparison.Ordinal)
        || string.Equals(value, "-h", StringComparison.Ordinal);

    private static ParseResult ParsePositionalSoak(string[] args, TextWriter error)
    {
        if (!TryInt(args[1], SmokeLimits.MinSeconds, SmokeLimits.MaxSeconds, "seconds", error, out var seconds)
            || !TryInt(args[2], SmokeLimits.MinSessions, SmokeLimits.MaxSessions, "sessions", error, out var sessions)
            || !TryInt(args[3], SmokeLimits.MinBodyBytes, SmokeLimits.MaxBodyBytes, "bodyBytes", error, out var bodyBytes))
        {
            return ParseResult.Invalid();
        }

        if (!TryResults(args[4], error, out var results))
        {
            return ParseResult.Invalid();
        }

        return ParseResult.Ok(new SmokeCommand(
            SmokeMode.Soak,
            seconds,
            0,
            sessions,
            SmokeLimits.DefaultOutstandingStores,
            bodyBytes,
            results,
            SmokeLimits.DefaultHost,
            SmokeLimits.DefaultPort));
    }

    private static ParseResult ParseNamed(SmokeMode mode, ReadOnlySpan<string> args, TextWriter error)
    {
        int? seconds = null;
        int? articles = null;
        int? sessions = null;
        int? outstanding = null;
        int? bodyBytes = null;
        int? port = null;
        string? results = null;
        string? host = null;

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith('-'))
            {
                error.WriteLine($"Unexpected positional argument '{token}'. Use flags, or the positional soak form.");
                return ParseResult.Invalid();
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
            {
                error.WriteLine($"Missing value for {token}.");
                return ParseResult.Invalid();
            }

            var value = args[++i];
            switch (token)
            {
                case "--seconds":
                    if (!AssignInt(ref seconds, value, SmokeLimits.MinSeconds, SmokeLimits.MaxSeconds, "seconds", error))
                    {
                        return ParseResult.Invalid();
                    }

                    break;
                case "--articles":
                    if (!AssignInt(ref articles, value, SmokeLimits.MinArticles, SmokeLimits.MaxArticles, "articles", error))
                    {
                        return ParseResult.Invalid();
                    }

                    break;
                case "--sessions":
                    if (!AssignInt(ref sessions, value, SmokeLimits.MinSessions, SmokeLimits.MaxSessions, "sessions", error))
                    {
                        return ParseResult.Invalid();
                    }

                    break;
                case "--outstanding":
                    if (!TryInt(value, 1, 2, "outstanding", error, out var parsedOutstanding))
                    {
                        return ParseResult.Invalid();
                    }

                    if (outstanding is not null)
                    {
                        error.WriteLine("outstanding was specified more than once.");
                        return ParseResult.Invalid();
                    }

                    outstanding = parsedOutstanding;
                    break;
                case "--body-bytes":
                    if (!AssignInt(ref bodyBytes, value, SmokeLimits.MinBodyBytes, SmokeLimits.MaxBodyBytes, "body-bytes", error))
                    {
                        return ParseResult.Invalid();
                    }

                    break;
                case "--port":
                    if (!AssignInt(ref port, value, 1, 65535, "port", error))
                    {
                        return ParseResult.Invalid();
                    }

                    break;
                case "--results":
                    if (results is not null)
                    {
                        error.WriteLine("results was specified more than once.");
                        return ParseResult.Invalid();
                    }

                    if (!TryResults(value, error, out results))
                    {
                        return ParseResult.Invalid();
                    }

                    break;
                case "--host":
                    if (host is not null)
                    {
                        error.WriteLine("host was specified more than once.");
                        return ParseResult.Invalid();
                    }

                    if (!TryHost(value, error, out host))
                    {
                        return ParseResult.Invalid();
                    }

                    break;
                default:
                    error.WriteLine($"Unknown option '{token}'.");
                    return ParseResult.Invalid();
            }
        }

        if (results is null)
        {
            error.WriteLine("results directory is required.");
            return ParseResult.Invalid();
        }

        if (mode == SmokeMode.Soak)
        {
            if (articles is not null)
            {
                error.WriteLine("soak does not take --articles. Use smoke for a bounded article count.");
                return ParseResult.Invalid();
            }

            if (seconds is null)
            {
                error.WriteLine("soak requires --seconds.");
                return ParseResult.Invalid();
            }
        }
        else if (seconds is not null)
        {
            error.WriteLine("smoke does not take --seconds. Use soak for a timed run.");
            return ParseResult.Invalid();
        }

        return ParseResult.Ok(new SmokeCommand(
            mode,
            seconds ?? 0,
            articles ?? SmokeLimits.DefaultSmokeArticles,
            sessions ?? SmokeLimits.DefaultSessions,
            outstanding ?? SmokeLimits.DefaultOutstandingStores,
            bodyBytes ?? SmokeLimits.DefaultBodyBytes,
            results,
            host ?? SmokeLimits.DefaultHost,
            port ?? SmokeLimits.DefaultPort));
    }

    private static bool AssignInt(ref int? slot, string value, int min, int max, string name, TextWriter error)
    {
        if (slot is not null)
        {
            error.WriteLine($"{name} was specified more than once.");
            return false;
        }

        if (!TryInt(value, min, max, name, error, out var parsed))
        {
            return false;
        }

        slot = parsed;
        return true;
    }

    private static bool TryInt(string value, int min, int max, string name, TextWriter error, out int parsed)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) || parsed < min || parsed > max)
        {
            error.WriteLine($"{name} must be an integer from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}.");
            parsed = 0;
            return false;
        }

        return true;
    }

    private static bool TryResults(string value, TextWriter error, out string results)
    {
        results = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error.WriteLine("results directory is required.");
            return false;
        }

        try
        {
            results = Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error.WriteLine($"results directory is not a usable path ({ex.GetType().Name}).");
            return false;
        }

        return true;
    }

    private static bool TryHost(string value, TextWriter error, out string host)
    {
        host = value.Trim();
        if (host.Length is < 1 or > 253 || host.Contains(' ', StringComparison.Ordinal) || host.Contains('/', StringComparison.Ordinal))
        {
            error.WriteLine("host must be a non-empty DNS name without spaces.");
            host = string.Empty;
            return false;
        }

        return true;
    }
}

/// <summary>Outcome of <see cref="CommandLine.Parse"/>.</summary>
internal readonly record struct ParseResult(int ExitCode, SmokeCommand? Command, bool ShowHelp)
{
    internal static ParseResult Help() => new(0, null, true);

    internal static ParseResult Invalid() => new(2, null, false);

    internal static ParseResult Ok(SmokeCommand command) => new(0, command, false);
}
