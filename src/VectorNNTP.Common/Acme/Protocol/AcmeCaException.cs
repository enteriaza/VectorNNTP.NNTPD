namespace VectorNNTP.Common.Acme.Protocol
{
    /// <summary>
    /// Failure returned by an ACME certificate authority (problem document and/or HTTP status).
    /// Distinct from the product <see cref="VectorNNTP.Common.Acme.AcmeException"/> category hierarchy.
    /// </summary>
    internal class AcmeCaException : Exception
    {
        /// <summary>
        /// Creates a CA failure with no problem type, detail, or HTTP status.
        /// <see cref="ErrorType"/>, <see cref="Detail"/>, and <see cref="StatusCode"/> stay <see langword="null"/>.
        /// </summary>
        internal AcmeCaException()
        {
        }

        /// <summary>
        /// Creates a CA failure from <paramref name="message"/> only.
        /// <see cref="ErrorType"/>, <see cref="Detail"/>, and <see cref="StatusCode"/> stay <see langword="null"/>.
        /// </summary>
        /// <param name="message">Operator-safe summary.</param>
        internal AcmeCaException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Creates a CA failure from <paramref name="message"/> and <paramref name="innerException"/>.
        /// <see cref="ErrorType"/>, <see cref="Detail"/>, and <see cref="StatusCode"/> stay <see langword="null"/>.
        /// </summary>
        /// <param name="message">Operator-safe summary.</param>
        /// <param name="innerException">Transport or parse failure that caused this exception.</param>
        internal AcmeCaException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>Initializes a new instance of the <see cref="AcmeCaException"/> class.</summary>
        /// <param name="message">Operator-safe summary (no key material).</param>
        /// <param name="errorType">ACME problem type URN when present.</param>
        /// <param name="detail">CA-supplied detail when present.</param>
        /// <param name="statusCode">HTTP status when present.</param>
        internal AcmeCaException(string message, string? errorType, string? detail, int? statusCode)
            : base(message)
        {
            ErrorType = errorType;
            Detail = detail;
            StatusCode = statusCode;
        }

        /// <summary>Gets the ACME problem type URN when the authority supplied one.</summary>
        internal string? ErrorType { get; }

        /// <summary>Gets the human-readable detail when the authority supplied one.</summary>
        internal string? Detail { get; }

        /// <summary>Gets the HTTP status code when applicable.</summary>
        internal int? StatusCode { get; }
    }

    /// <summary>Thrown when the certificate authority rejects a request due to rate limiting.</summary>
    internal sealed class AcmeCaRateLimitException : AcmeCaException
    {
        /// <summary>
        /// Creates a rate-limit failure with no message and with <see cref="RetryAfter"/> left <see langword="null"/>.
        /// Does not set <see cref="AcmeCaException.ErrorType"/>.
        /// </summary>
        private AcmeCaRateLimitException()
        {
        }

        /// <summary>
        /// Creates a rate-limit failure from <paramref name="message"/> only.
        /// <see cref="RetryAfter"/> stays <see langword="null"/> and <see cref="AcmeCaException.ErrorType"/> is not set.
        /// </summary>
        /// <param name="message">Operator-safe summary.</param>
        private AcmeCaRateLimitException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Creates a rate-limit failure from <paramref name="message"/> and <paramref name="innerException"/>.
        /// <see cref="RetryAfter"/> stays <see langword="null"/> and <see cref="AcmeCaException.ErrorType"/> is not set.
        /// </summary>
        /// <param name="message">Operator-safe summary.</param>
        /// <param name="innerException">Failure that caused this exception.</param>
        private AcmeCaRateLimitException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// Records HTTP 429 / <see cref="AcmeErrorTypes.RateLimited"/>, the CA detail, and the parsed Retry-After time.
        /// </summary>
        /// <param name="message">Operator-safe summary passed to <see cref="AcmeCaException"/>.</param>
        /// <param name="detail">CA problem detail. <see langword="null"/> when the problem had none.</param>
        /// <param name="retryAfter">Absolute retry time. <see langword="null"/> when the response had no usable Retry-After header.</param>
        internal AcmeCaRateLimitException(string message, string? detail, DateTimeOffset? retryAfter)
            : base(message, AcmeErrorTypes.RateLimited, detail, 429)
        {
            RetryAfter = retryAfter;
        }

        /// <summary>Gets when the request may be retried, when the authority supplied Retry-After.</summary>
        private DateTimeOffset? RetryAfter { get; }
    }
}
