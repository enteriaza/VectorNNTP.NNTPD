namespace VectorNNTP.Common.Articles.DateParser
{
    /// <summary>
    /// Controls the guardrails and normalization steps applied before date canonicalization.
    /// </summary>
    /// <param name="MaxInputLength">Maximum number of trimmed bytes accepted for one candidate date header value.</param>
    /// <param name="RequireKnownTimezoneAbbreviation">Whether a trailing alphabetic timezone token must be recognized before exact parsing is attempted.</param>
    /// <param name="NormalizeInteriorWhitespace">Whether runs of interior ASCII spaces are collapsed before exact parsing.</param>
    internal readonly record struct DateParseOptions(
        int MaxInputLength,
        bool RequireKnownTimezoneAbbreviation,
        bool NormalizeInteriorWhitespace)
    {
        /// <summary>
        /// Gets the repository default date-parse options.
        /// </summary>
        /// <value>Accepts up to 512 bytes, tolerates unknown timezone abbreviations, and normalizes repeated interior spaces.</value>
        internal static DateParseOptions Default { get; } = new(
            MaxInputLength: 512,
            RequireKnownTimezoneAbbreviation: false,
            NormalizeInteriorWhitespace: true);
    }
}
