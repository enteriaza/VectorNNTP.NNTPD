namespace VectorNNTP.NNTPD.Cloudflare;

/// <summary>
/// Shared wall-clock deadline for a single Cloudflare reconcile or cleanup operation.
/// </summary>
/// <remarks>
/// Nested HTTP 429 retries and reconciler attempt backoffs must observe this deadline so
/// Retry-After cannot extend past the remaining operation budget. The earlier of this
/// deadline and the caller's <see cref="CancellationToken"/> wins. The budget is created once
/// per reconcile/cleanup call and is not reset on individual HTTP attempts or reconciler retries.
/// </remarks>
internal sealed class CloudflareOperationBudget : IDisposable
{
    private static readonly AsyncLocal<CloudflareOperationBudget?> CurrentBudget = new();

    private readonly CloudflareOperationBudget? _previous;
    private bool _disposed;

    private CloudflareOperationBudget(DateTimeOffset deadlineUtc)
    {
        DeadlineUtc = deadlineUtc;
        _previous = CurrentBudget.Value;
        CurrentBudget.Value = this;
    }

    /// <summary>Gets the active budget for the current async flow, if any.</summary>
    public static CloudflareOperationBudget? Current => CurrentBudget.Value;

    /// <summary>Gets the absolute UTC deadline for this operation.</summary>
    public DateTimeOffset DeadlineUtc { get; }

    /// <summary>Gets the remaining time until <see cref="DeadlineUtc"/> (zero when expired).</summary>
    public TimeSpan Remaining
    {
        get
        {
            var remaining = DeadlineUtc - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Begins an operation budget that links the caller token with a timeout and publishes
    /// the deadline for nested HTTP retries.
    /// </summary>
    /// <param name="timeout">Maximum wall-clock duration for the operation.</param>
    /// <param name="callerToken">Caller / lifecycle cancellation (earlier deadline wins).</param>
    /// <param name="linkedToken">Token canceled when either the caller cancels or the timeout elapses.</param>
    /// <returns>A scope that restores the previous budget when disposed.</returns>
    public static CloudflareOperationBudget Begin(
        TimeSpan timeout,
        CancellationToken callerToken,
        out CancellationToken linkedToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var deadline = DateTimeOffset.UtcNow + timeout;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        cts.CancelAfter(timeout);
        linkedToken = cts.Token;

        var budget = new CloudflareOperationBudget(deadline)
        {
            LinkedCts = cts,
        };
        return budget;
    }

    private CancellationTokenSource? LinkedCts { get; init; }

    /// <summary>
    /// Throws <see cref="OperationCanceledException"/> when the budget has expired or the token is canceled.
    /// </summary>
    public void ThrowIfExpired(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Remaining <= TimeSpan.Zero)
        {
            throw new OperationCanceledException(
                "Cloudflare DNS operation budget has expired.",
                cancellationToken);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (ReferenceEquals(CurrentBudget.Value, this))
        {
            CurrentBudget.Value = _previous;
        }

        LinkedCts?.Dispose();
    }
}
