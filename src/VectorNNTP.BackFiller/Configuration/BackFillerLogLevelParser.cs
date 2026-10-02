using Serilog.Events;

namespace VectorNNTP.BackFiller.Configuration;

/// <summary>
/// Parses <see cref="BackFillerLoggingOptions.LogLevel"/> into a Serilog level.
/// </summary>
/// <remarks>
/// Accepts only the Serilog names <c>Verbose</c>, <c>Debug</c>, <c>Information</c>,
/// <c>Warning</c>, <c>Error</c>, and <c>Fatal</c>. Matching is case-insensitive.
/// Microsoft.Extensions.Logging names such as <c>Trace</c> and <c>Critical</c> are rejected.
/// </remarks>
internal static class BackFillerLogLevelParser
{
    /// <summary>
    /// Parses <paramref name="text"/> when it names a Serilog level.
    /// </summary>
    /// <param name="text">Configured level text.</param>
    /// <param name="level">The parsed level when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is a supported level.</returns>
    public static bool TryParse(string? text, out LogEventLevel level)
    {
        level = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!Enum.TryParse(text.Trim(), ignoreCase: true, out level) || !Enum.IsDefined(level))
        {
            level = default;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Parses <paramref name="text"/>, using <see cref="BackFillerLoggingOptions.DefaultLogLevel"/> when it is omitted.
    /// </summary>
    /// <param name="text">Configured level text, or <see langword="null"/> when the key is absent.</param>
    /// <returns>The Serilog minimum level.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="text"/> is present and not a Serilog level.</exception>
    public static LogEventLevel ParseOrDefault(string? text)
    {
        if (text is null)
        {
            if (!TryParse(BackFillerLoggingOptions.DefaultLogLevel, out var fallback))
            {
                throw new InvalidOperationException("BackFiller:Logging:LogLevel default is not a Serilog level.");
            }

            return fallback;
        }

        if (!TryParse(text, out var level))
        {
            throw new InvalidOperationException(
                $"BackFiller:Logging:LogLevel '{text}' is not valid. Use Verbose, Debug, Information, Warning, Error, or Fatal.");
        }

        return level;
    }
}
