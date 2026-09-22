using System.Diagnostics.CodeAnalysis;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>Extracts AUTHINFO USER/PASS arguments from the raw command line (preserves internal spaces).</summary>
internal static class AuthinfoArgument
{
    /// <summary>
    /// Extracts the argument after <c>AUTHINFO {subcommand}</c>.
    /// Returns <see langword="false"/> when the argument is missing or empty.
    /// </summary>
    public static bool TryGet(string rawLine, string subcommand, [NotNullWhen(true)] out string? argument)
    {
        ArgumentNullException.ThrowIfNull(rawLine);
        ArgumentException.ThrowIfNullOrWhiteSpace(subcommand);
        argument = null;

        var span = rawLine.AsSpan().TrimStart();
        if (!span.StartsWith("AUTHINFO", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        span = span[8..].TrimStart();
        if (!span.StartsWith(subcommand, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        span = span[subcommand.Length..];
        if (span.IsEmpty || !char.IsWhiteSpace(span[0]))
        {
            return false;
        }

        span = span.TrimStart();
        if (span.IsEmpty)
        {
            return false;
        }

        argument = span.ToString();
        return true;
    }
}
