namespace VectorNNTP.Common.Configuration
{
    /// <summary>
    /// Shared <c>ServerId</c> bounds and required/default semantics.
    /// </summary>
    /// <remarks>
    /// Required <see cref="Nullable{T}"/>, no default, inclusive range
    /// <see cref="MinimumInclusive"/>–<see cref="MaximumInclusive"/>. A missing value remains
    /// distinguishable from an explicit <c>0</c>; both fail validation.
    /// This type does not know about applications, configuration sections, or environment variables.
    /// </remarks>
    internal static class ServerIdRules
    {
        /// <summary>Inclusive lower bound.</summary>
        public const int MinimumInclusive = 1;

        /// <summary>Inclusive upper bound.</summary>
        public const int MaximumInclusive = 255;

        /// <summary>
        /// Returns whether <paramref name="serverId"/> is unset (missing configuration).
        /// </summary>
        /// <param name="serverId">Bound server id, or <see langword="null"/> when omitted.</param>
        /// <returns><see langword="true"/> when the value was not configured.</returns>
        internal static bool IsMissing(int? serverId) => serverId is null;

        /// <summary>
        /// Returns whether a configured <paramref name="serverId"/> is inside the accepted range.
        /// </summary>
        /// <param name="serverId">Configured server id.</param>
        /// <returns><see langword="true"/> when <paramref name="serverId"/> is 1–255 inclusive.</returns>
        internal static bool IsInRange(int serverId) =>
            serverId is >= MinimumInclusive and <= MaximumInclusive;

        /// <summary>
        /// Classifies <paramref name="serverId"/> using the shared required/range semantics.
        /// </summary>
        /// <param name="serverId">Bound server id, or <see langword="null"/> when omitted.</param>
        /// <returns>Validation classification.</returns>
        internal static ServerIdValidationStatus Classify(int? serverId)
        {
            if (serverId is not { } value)
            {
                return ServerIdValidationStatus.Missing;
            }

            return IsInRange(value)
                ? ServerIdValidationStatus.Valid
                : ServerIdValidationStatus.OutOfRange;
        }

        /// <summary>
        /// Builds a generic failure message for a classified <paramref name="serverId"/>.
        /// </summary>
        /// <param name="serverId">Bound server id, or <see langword="null"/> when omitted.</param>
        /// <param name="configurationKey">Caller-supplied configuration path shown in the failure.</param>
        /// <returns>
        /// <see langword="null"/> when valid; otherwise a missing or out-of-range failure.
        /// The missing message does not mention an environment variable.
        /// </returns>
        internal static string? Validate(int? serverId, string configurationKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(configurationKey);

            return Classify(serverId) switch
            {
                ServerIdValidationStatus.Missing =>
                    $"{configurationKey} is required and must be an integer in the range {MinimumInclusive}–{MaximumInclusive} (no default; set {configurationKey}).",
                ServerIdValidationStatus.OutOfRange =>
                    $"{configurationKey} must be an integer in the range {MinimumInclusive}–{MaximumInclusive}.",
                _ => null,
            };
        }
    }

    /// <summary>
    /// Result of <see cref="ServerIdRules.Classify"/>.
    /// </summary>
    internal enum ServerIdValidationStatus
    {
        /// <summary>Configured and inside 1–255.</summary>
        Valid = 0,

        /// <summary>Omitted (<see langword="null"/>).</summary>
        Missing = 1,

        /// <summary>Configured but outside 1–255 (including <c>0</c>).</summary>
        OutOfRange = 2,
    }
}
