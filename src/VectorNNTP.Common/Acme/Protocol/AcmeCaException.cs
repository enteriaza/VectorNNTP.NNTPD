namespace VectorNNTP.Common.Acme.Protocol
{
    /// <summary>
    /// Failure returned by an ACME certificate authority (problem document and/or HTTP status).
    /// Distinct from the product <see cref="VectorNNTP.Common.Acme.AcmeException"/> category hierarchy.
    /// </summary>
    internal class AcmeCaException : Exception
    {
        /// <summary>Initializes a new instance of the <see cref="AcmeCaException"/> class.</summary>
        internal AcmeCaException()
        {
        }

        /// <summary>Initializes a new instance of the <see cref="AcmeCaException"/> class.</summary>
        internal AcmeCaException(string message)
            : base(message)
        {
        }

        /// <summary>Initializes a new instance of the <see cref="AcmeCaException"/> class.</summary>
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
        /// <summary>Initializes a new instance of the <see cref="AcmeCaRateLimitException"/> class.</summary>
        private AcmeCaRateLimitException()
        {
        }

        /// <summary>Initializes a new instance of the <see cref="AcmeCaRateLimitException"/> class.</summary>
        private AcmeCaRateLimitException(string message)
            : base(message)
        {
        }

        /// <summary>Initializes a new instance of the <see cref="AcmeCaRateLimitException"/> class.</summary>
        private AcmeCaRateLimitException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>Initializes a new instance of the <see cref="AcmeCaRateLimitException"/> class.</summary>
        internal AcmeCaRateLimitException(string message, string? detail, DateTimeOffset? retryAfter)
            : base(message, AcmeErrorTypes.RateLimited, detail, 429)
        {
            RetryAfter = retryAfter;
        }

        /// <summary>Gets when the request may be retried, when the authority supplied Retry-After.</summary>
        private DateTimeOffset? RetryAfter { get; }
    }
}
