namespace VectorNNTP.Common.Cloudflare
{
    /// <summary>
    /// Shared wall-clock deadline for a single Cloudflare reconcile or clean-up operation.
    /// </summary>
    /// <remarks>
    /// Nested HTTP 429 retries and reconciler attempt backoffs must observe this deadline so
    /// Retry-After cannot extend past the remaining operation budget. The earlier of this
    /// deadline and the caller's <see cref="CancellationToken"/> wins. The budget is created once
    /// per reconcile/clean-up call and is not reset on individual HTTP attempts or reconciler retries.
    /// </remarks>
    internal sealed class CloudflareOperationBudget : IDisposable
    {
        /// <summary>Budget visible to nested HTTP retries on the current async flow.</summary>
        private static readonly AsyncLocal<CloudflareOperationBudget?> CurrentBudget = new();

        /// <summary>Budget that was current when this one began. Restored by <see cref="Dispose"/>.</summary>
        private readonly CloudflareOperationBudget? _previous;

        /// <summary><see langword="true"/> after <see cref="Dispose"/>. A second dispose is a no-op.</summary>
        private bool _disposed;

        /// <summary>
        /// Publishes this budget as <see cref="Current"/> and remembers the previous budget.
        /// </summary>
        /// <param name="deadlineUtc">Absolute UTC time when the operation budget expires.</param>
        private CloudflareOperationBudget(DateTimeOffset deadlineUtc)
        {
            DeadlineUtc = deadlineUtc;
            _previous = CurrentBudget.Value;
            CurrentBudget.Value = this;
        }

        /// <summary>Gets the active budget for the current async flow, if any.</summary>
        internal static CloudflareOperationBudget? Current => CurrentBudget.Value;

        /// <summary>Gets the absolute UTC deadline for this operation.</summary>
        private DateTimeOffset DeadlineUtc { get; }

        /// <summary>Gets the remaining time until <see cref="DeadlineUtc"/> (zero when expired).</summary>
        internal TimeSpan Remaining
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
        /// <param name="linkedToken">Token cancelled when either the caller cancels or the timeout elapses.</param>
        /// <returns>A scope that restores the previous budget when disposed.</returns>
        internal static CloudflareOperationBudget Begin(
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

        /// <summary>
        /// Caller token linked with <see cref="CancellationTokenSource.CancelAfter(System.TimeSpan)"/>.
        /// Disposed with the budget. Null only for a budget not created by <see cref="Begin"/>.
        /// </summary>
        private CancellationTokenSource? LinkedCts { get; init; }

        /// <summary>
        /// Throws <see cref="OperationCanceledException"/> when the budget has expired or the token is cancelled.
        /// </summary>
        internal void ThrowIfExpired(CancellationToken cancellationToken)
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
}
