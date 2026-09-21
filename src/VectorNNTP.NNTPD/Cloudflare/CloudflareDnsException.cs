namespace VectorNNTP.NNTPD.Cloudflare;

/// <summary>
/// Raised when a Cloudflare DNS API operation fails.
/// </summary>
/// <remarks>
/// Messages must never include API credentials. HTTP authorization headers are not stored.
/// An <see cref="IsOutcomeUncertain"/> failure means the remote DNS state may already include
/// the attempted mutation; callers must re-read Cloudflare state rather than assume no change.
/// <see cref="IsPermanentFailure"/> failures must not be retried as if they were transient.
/// </remarks>
public sealed class CloudflareDnsException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CloudflareDnsException"/> class.
    /// </summary>
    /// <param name="message">Safe diagnostic message without secrets.</param>
    public CloudflareDnsException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CloudflareDnsException"/> class.
    /// </summary>
    /// <param name="message">Safe diagnostic message without secrets.</param>
    /// <param name="innerException">Inner exception.</param>
    public CloudflareDnsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Gets the HTTP status code when the failure originated from an HTTP response.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Gets Cloudflare error codes from the response body, when present.</summary>
    public IReadOnlyList<int> CloudflareErrorCodes { get; init; } = [];

    /// <summary>Gets a value indicating whether the failure was an HTTP 429 rate limit.</summary>
    public bool IsRateLimited => StatusCode == 429;

    /// <summary>
    /// Gets a value indicating whether the remote side may have applied a mutation despite the local failure
    /// (for example timeout or transport error during create/update/delete).
    /// </summary>
    public bool IsOutcomeUncertain { get; init; }

    /// <summary>
    /// Gets a value indicating whether the failure is permanent (auth/config/validation) and must not be
    /// retried as a transient error.
    /// </summary>
    public bool IsPermanentFailure { get; init; }

    /// <summary>Gets a short label for the failed operation (for diagnostics).</summary>
    public string? FailedOperation { get; init; }

    /// <summary>
    /// Returns whether an HTTP status should be treated as a permanent Cloudflare API failure.
    /// </summary>
    /// <remarks>429 is transient. Other 4xx (including 401/403) are permanent for this host.</remarks>
    public static bool IsPermanentHttpStatus(int? statusCode) =>
        statusCode is >= 400 and < 500 and not 429;
}
