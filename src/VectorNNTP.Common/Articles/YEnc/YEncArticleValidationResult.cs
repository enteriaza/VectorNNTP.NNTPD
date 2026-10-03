namespace VectorNNTP.Common.Articles.YEnc
{
    /// <summary>
    /// Terminal yEnc validation classification for a raw NNTP article body.
    /// </summary>
    internal enum YEncArticleValidationStatus
    {
        /// <summary>No yEnc section was found and no corruption was detected.</summary>
        ValidNonYEnc = 0,

        /// <summary>One or more single-part yEnc sections validated successfully.</summary>
        ValidSinglePart = 1,

        /// <summary>
        /// One or more multipart yEnc sections validated successfully as independent per-section validations.
        /// </summary>
        /// <remarks>
        /// This status does not imply complete-file reconstruction or proof that all sections required for final file assembly are present.
        /// </remarks>
        ValidMultiPart = 2,

        /// <summary>yEnc structure is malformed.</summary>
        MalformedYEnc = 3,

        /// <summary>yEnc trailer or control-line metadata is invalid.</summary>
        InvalidMetadata = 4,

        /// <summary>The decoded payload CRC does not match declared CRC metadata.</summary>
        CrcMismatch = 5,

        /// <summary>The decoded payload size does not match declared metadata.</summary>
        DecodedSizeMismatch = 6,

        /// <summary>The yEnc data is truncated or incomplete.</summary>
        Truncated = 7,

        /// <summary>An invalid yEnc escape sequence was encountered.</summary>
        InvalidEscapeSequence = 8,
    }

    /// <summary>
    /// Compact, allocation-free validation result for yEnc article verification.
    /// </summary>
    /// <param name="Status">Terminal validation status.</param>
    /// <param name="SectionsValidated">Number of independently validated yEnc sections encountered during the forward scan.</param>
    internal readonly record struct YEncArticleValidationResult(
        YEncArticleValidationStatus Status,
        int SectionsValidated)
    {
        /// <summary>
        /// Gets a value indicating whether the article payload is valid for yEnc correctness purposes.
        /// </summary>
        internal bool IsValid => Status is YEncArticleValidationStatus.ValidNonYEnc
            or YEncArticleValidationStatus.ValidSinglePart
            or YEncArticleValidationStatus.ValidMultiPart;

        /// <summary>
        /// Gets a value indicating whether at least one yEnc section was identified.
        /// </summary>
        private bool IsYEnc => Status is not YEncArticleValidationStatus.ValidNonYEnc;

        /// <summary>
        /// Gets a value indicating whether the caller should report this result as yEnc decoding failed.
        /// </summary>
        internal bool ShouldTreatAsYEncDecodingFailed => Status is not YEncArticleValidationStatus.ValidNonYEnc
            && !IsValid;

        /// <summary>
        /// Creates the canonical success result used when no <c>=ybegin</c> section was validated.
        /// </summary>
        /// <returns>A non-yEnc success result with zero validated sections.</returns>
        internal static YEncArticleValidationResult ValidNonYEnc()
            => new(YEncArticleValidationStatus.ValidNonYEnc, 0);
    }
}
