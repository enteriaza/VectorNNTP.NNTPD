namespace VectorNNTP.BackFiller.Logging;

/// <summary>
/// Process switches that change the code-defined logging pipeline.
/// </summary>
/// <param name="Console">When <see langword="true"/>, the host logger includes the Serilog console sink.</param>
/// <param name="EnrichFromLogContext">When <see langword="true"/>, the host logger calls <c>Enrich.FromLogContext</c>.</param>
internal readonly record struct BackFillerLoggingCommandLine(bool Console, bool EnrichFromLogContext)
{
    /// <summary>Reads logging switches from <paramref name="args"/> without changing other arguments.</summary>
    /// <param name="args">The process arguments, or <see langword="null"/>.</param>
    /// <returns>The switches that were present.</returns>
    public static BackFillerLoggingCommandLine FromArguments(IReadOnlyList<string>? args)
    {
        var console = false;
        var enrichFromLogContext = false;
        if (args is null)
        {
            return default;
        }

        foreach (var arg in args)
        {
            if (string.Equals(arg, "--console", StringComparison.OrdinalIgnoreCase))
            {
                console = true;
            }
            else if (string.Equals(arg, "--log-context", StringComparison.OrdinalIgnoreCase))
            {
                enrichFromLogContext = true;
            }
        }

        return new BackFillerLoggingCommandLine(console, enrichFromLogContext);
    }
}
