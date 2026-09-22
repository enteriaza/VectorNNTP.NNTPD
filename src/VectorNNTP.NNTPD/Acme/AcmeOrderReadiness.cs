namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// Bounded poll of ACME order / authorization state until the order is ready to finalize,
/// becomes invalid, times out, or is cancelled.
/// </summary>
/// <remarks>
/// Authoritative DNS visibility is not equivalent to ACME validation. Callers must trigger
/// challenges first, then wait here before finalizing.
/// </remarks>
internal static class AcmeOrderReadiness
{
    /// <summary>Default overall readiness wait (aligned with DNS-01 propagation budget).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Default delay between readiness polls.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    /// <summary>Outcome of evaluating one poll snapshot.</summary>
    public enum Decision
    {
        /// <summary>Still pending; keep polling.</summary>
        Continue,

        /// <summary>Order is ready for finalization.</summary>
        Ready,

        /// <summary>Authorization or order is invalid; issuance must fail.</summary>
        Invalid,
    }

    /// <summary>One authorization snapshot used for readiness decisions.</summary>
    /// <param name="Domain">DNS identifier value when known.</param>
    /// <param name="Status">Authorization status string (e.g. pending, valid, invalid).</param>
    /// <param name="ErrorType">ACME problem type from a failed challenge, when present.</param>
    /// <param name="ErrorDetail">ACME problem detail from a failed challenge, when present.</param>
    /// <param name="ErrorStatus">HTTP status from the ACME problem, when present.</param>
    public sealed record AuthorizationView(
        string? Domain,
        string Status,
        string? ErrorType,
        string? ErrorDetail,
        int? ErrorStatus);

    /// <summary>Order plus authorization snapshots for one poll.</summary>
    /// <param name="Status">Order status string.</param>
    /// <param name="Authorizations">Authorization snapshots for the order.</param>
    public sealed record OrderView(
        string Status,
        IReadOnlyList<AuthorizationView> Authorizations);

    /// <summary>
    /// Evaluates a poll snapshot. When <see cref="Decision.Invalid"/>, <paramref name="invalidDiagnostic"/>
    /// is set to a sanitized explanation.
    /// </summary>
    public static Decision Evaluate(OrderView view, out string? invalidDiagnostic)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(view.Authorizations);
        invalidDiagnostic = null;

        foreach (var authz in view.Authorizations)
        {
            if (AcmeProblemDiagnostics.NormalizeStatus(authz.Status) == "invalid")
            {
                invalidDiagnostic = AcmeProblemDiagnostics.FormatAuthorizationFailure(authz);
                return Decision.Invalid;
            }
        }

        var orderStatus = AcmeProblemDiagnostics.NormalizeStatus(view.Status);
        if (orderStatus == "invalid")
        {
            invalidDiagnostic = AcmeProblemDiagnostics.FormatOrderInvalid(view);
            return Decision.Invalid;
        }

        if (orderStatus is "ready" or "valid")
        {
            return Decision.Ready;
        }

        return Decision.Continue;
    }

    /// <summary>
    /// Polls until the order is ready, invalid, the timeout elapses, or cancellation is requested.
    /// </summary>
    /// <param name="pollAsync">Fetches the current order/authz view (Certes Resource() mapping).</param>
    /// <param name="timeout">Overall readiness deadline.</param>
    /// <param name="interval">Delay between polls.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <param name="delayAsync">Injectable delay (tests); defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public static async Task WaitUntilReadyAsync(
        Func<CancellationToken, Task<OrderView>> pollAsync,
        TimeSpan timeout,
        TimeSpan interval,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(pollAsync);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        delayAsync ??= static (delay, token) => Task.Delay(delay, token);
        var deadline = DateTimeOffset.UtcNow + timeout;
        OrderView? last = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await pollAsync(cancellationToken).ConfigureAwait(false);
            var decision = Evaluate(last, out var invalidDiagnostic);
            switch (decision)
            {
                case Decision.Ready:
                    return;
                case Decision.Invalid:
                    throw new AcmeOrderException(
                        "authorization_invalid",
                        invalidDiagnostic ?? "authorization invalid");
                case Decision.Continue:
                    break;
                default:
                    throw new AcmeOrderException(
                        "authorization_unexpected",
                        "decision=" + decision);
            }

            var now = DateTimeOffset.UtcNow;
            if (now >= deadline)
            {
                throw new AcmeOrderException(
                    "authorization_timeout",
                    AcmeProblemDiagnostics.FormatTimeout(last));
            }

            var remaining = deadline - now;
            var sleep = remaining < interval ? remaining : interval;
            if (sleep <= TimeSpan.Zero)
            {
                throw new AcmeOrderException(
                    "authorization_timeout",
                    AcmeProblemDiagnostics.FormatTimeout(last));
            }

            await delayAsync(sleep, cancellationToken).ConfigureAwait(false);
        }
    }
}
