namespace VectorNNTP.BackFiller.Logging
{
    /// <summary>
    /// Process switches that change the code-defined logging pipeline.
    /// </summary>
    /// <param name="Console">When <see langword="true"/>, the host logger includes the Serilog console sink.</param>
    /// <param name="EnrichFromLogContext">
    /// When <see langword="true"/>, the host logger calls <c>Enrich.FromLogContext</c> and text output templates
    /// render <c>{SourceContext}</c>. This is not a configuration setting.
    /// </param>
    internal readonly record struct BackFillerLoggingCommandLine(bool Console, bool EnrichFromLogContext)
    {
        /// <summary>Reads logging switches from <paramref name="args"/> without changing other arguments.</summary>
        /// <param name="args">The process arguments, or <see langword="null"/>.</param>
        /// <returns>The switches that were present.</returns>
        /// <remarks>
        /// <c>--console</c> and <c>--log-context</c> match with <see cref="StringComparison.OrdinalIgnoreCase"/>.
        /// <c>--log-context</c> is a process switch, not a configuration setting. It enables ambient context
        /// enrichment and <c>{SourceContext}</c> in text output. Any other argument is ignored. A null
        /// <paramref name="args"/> returns the default value, with both switches false.
        /// </remarks>
        internal static BackFillerLoggingCommandLine FromArguments(IReadOnlyList<string>? args)
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
}
