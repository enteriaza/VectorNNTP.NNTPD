namespace VectorNNTP.BackFiller.Nntp
{
    /// <summary>
    /// Application-wide concurrency gate for provider NNTP session establishment.
    /// </summary>
    /// <remarks>
    /// One instance is shared by every <see cref="NntpSessionPool"/>. The gate covers the
    /// expensive connect path (TCP through Ready: greeting, CAPABILITIES, optional STARTTLS, AUTHINFO).
    /// It does not limit how many sessions may remain established once Ready.
    /// <see cref="SemaphoreSlim"/> waiters are FIFO; cancelled waiters do not consume a slot.
    /// </remarks>
    internal sealed class ProviderSessionEstablishmentGate : IDisposable
    {
        /// <summary>Shared establishment permits.</summary>
        private readonly SemaphoreSlim _semaphore;

        /// <summary>Zero until <see cref="Dispose"/> runs.</summary>
        private int _disposed;

        /// <summary>
        /// Creates a gate that allows at most <paramref name="maxConcurrentEstablishments"/> concurrent establishments.
        /// </summary>
        /// <param name="maxConcurrentEstablishments">
        /// Positive permit count. Must match validated
        /// <see cref="Configuration.BackFillerNntpOptions.MaxConcurrentSessionEstablishments"/>.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="maxConcurrentEstablishments"/> is less than 1.
        /// </exception>
        internal ProviderSessionEstablishmentGate(int maxConcurrentEstablishments)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentEstablishments, 1);
            _semaphore = new SemaphoreSlim(maxConcurrentEstablishments, maxConcurrentEstablishments);
            MaxConcurrentEstablishments = maxConcurrentEstablishments;
        }

        /// <summary>Gets the configured permit count.</summary>
        internal int MaxConcurrentEstablishments { get; }

        /// <summary>
        /// Waits for an establishment permit. Cancellation does not consume a permit.
        /// </summary>
        /// <param name="cancellationToken">Cancels the wait without acquiring.</param>
        /// <returns>A task that completes when a permit is held.</returns>
        /// <exception cref="ObjectDisposedException">The gate is disposed.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
        internal Task WaitAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            return _semaphore.WaitAsync(cancellationToken);
        }

        /// <summary>Releases one establishment permit previously taken by <see cref="WaitAsync"/>.</summary>
        /// <exception cref="ObjectDisposedException">The gate is disposed.</exception>
        /// <exception cref="SemaphoreFullException">More releases than acquisitions.</exception>
        internal void Release()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            _semaphore.Release();
        }

        /// <summary>Disposes the underlying semaphore. Safe to call more than once.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _semaphore.Dispose();
        }
    }
}
