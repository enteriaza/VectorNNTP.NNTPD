using System.Runtime.ExceptionServices;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.Engine;

namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

/// <summary>
/// Shares one in-flight physical article read among concurrent callers of the same
/// <see cref="ArticleId"/>.
/// </summary>
/// <remarks>
/// <para>
/// The registry holds only reads that have not finished. Completion, failure, and an owner
/// that is cancelled before the read starts, with no waiters left, remove the entry. A finished
/// read is not retained, and a failure is not remembered as absence. A caller that arrives after
/// removal starts a new read.
/// </para>
/// <para>
/// The bound matches <see cref="VectorNNTP.StorageServer.Configuration.StorageServerListenerOptions.MaxActiveConnections"/>
/// unless a caller supplies another limit. Past either the in-flight limit or the per-article
/// waiter limit, the caller performs the existing direct read and does not enlarge the registry.
/// Cancelling one waiter does not cancel the shared read. The underlying segment read is
/// synchronous and is not cancelled mid-flight. Outstanding journal copies are not registered here.
/// </para>
/// </remarks>
internal sealed class ArticlePhysicalReadCoalescer
{
    /// <summary>Default cap, equal to the listener's default <c>MaxActiveConnections</c>.</summary>
    internal const int DefaultMaxInFlight = 1024;

    /// <summary>Guards <see cref="_flights"/> and the waiter gauge.</summary>
    private readonly object _gate = new();

    /// <summary>Article ids whose physical read has not finished. Completed entries are removed.</summary>
    private readonly Dictionary<ArticleId, Flight> _flights = new();

    /// <summary>Maximum entries in <see cref="_flights"/>.</summary>
    private int _maxInFlight = DefaultMaxInFlight;

    /// <summary>Maximum waiters on one <see cref="Flight"/>, excluding the owner.</summary>
    private int _maxWaiters = DefaultMaxInFlight;

    /// <summary>Waiters blocked across all in-flight reads.</summary>
    private int _waiterCount;

    /// <summary>Largest <see cref="Flight.Waiters"/> value observed.</summary>
    private int _maxObservedWaiters;

    /// <summary>Waiters that joined an in-flight read instead of starting their own.</summary>
    private long _coalescedCount;

    /// <summary>Times the physical read callback ran, including a failed attempt and a direct fallback.</summary>
    private long _physicalReadCount;

    /// <summary>Serializes rethrow of one shared failure so waiters do not publish it concurrently.</summary>
    private readonly object _rethrowGate = new();

    /// <summary>Maximum simultaneous article ids with an in-flight physical read.</summary>
    internal int MaxInFlight
    {
        get
        {
            lock (_gate)
            {
                return _maxInFlight;
            }
        }

        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            lock (_gate)
            {
                _maxInFlight = value;
            }
        }
    }

    /// <summary>Maximum waiters joined to one in-flight read, excluding the owner.</summary>
    internal int MaxWaiters
    {
        get
        {
            lock (_gate)
            {
                return _maxWaiters;
            }
        }

        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            lock (_gate)
            {
                _maxWaiters = value;
            }
        }
    }

    /// <summary>Waiters that joined an in-flight read instead of starting their own.</summary>
    internal long CoalescedCount => Volatile.Read(ref _coalescedCount);

    /// <summary>Times the physical read callback actually ran (owner or direct fallback).</summary>
    internal long PhysicalReadCount => Volatile.Read(ref _physicalReadCount);

    /// <summary>Waiters currently blocked on an in-flight read.</summary>
    internal int WaiterCount
    {
        get
        {
            lock (_gate)
            {
                return _waiterCount;
            }
        }
    }

    /// <summary>Article ids with a registered in-flight read.</summary>
    internal int InFlightCount
    {
        get
        {
            lock (_gate)
            {
                return _flights.Count;
            }
        }
    }

    /// <summary>Largest waiter count observed for any single read.</summary>
    internal int MaxObservedWaiters
    {
        get
        {
            lock (_gate)
            {
                return _maxObservedWaiters;
            }
        }
    }

    /// <summary>
    /// Invoked after a waiter joins, outside the registry lock.
    /// Must not block and must not call back into this coalescer.
    /// </summary>
    internal Action<int>? WaitersChanged { get; set; }

    /// <summary>
    /// Runs <paramref name="read"/> once for <paramref name="articleId"/> while concurrent callers wait.
    /// </summary>
    /// <param name="articleId">Article whose physical read may be shared.</param>
    /// <param name="cancellationToken">
    /// Cancels this caller only. A cancelled waiter leaves the shared read running.
    /// An owner that is already inside <paramref name="read"/> finishes that call, publishes the
    /// outcome, then observes cancellation for itself.
    /// </param>
    /// <param name="read">
    /// Owner callback. Returns null when the existing read path did not produce an article.
    /// Exceptions are shared with current waiters and are not remembered.
    /// </param>
    /// <param name="result">Verified read when this returns true.</param>
    /// <returns>True when <paramref name="read"/> produced an article.</returns>
    internal bool Execute(
        ArticleId articleId,
        CancellationToken cancellationToken,
        Func<ArticleReadResult?> read,
        out ArticleReadResult result)
    {
        ArgumentNullException.ThrowIfNull(read);
        result = default;
        Flight? owned = null;
        Flight? joined = null;
        var joinedWaiters = 0;
        lock (_gate)
        {
            if (_flights.TryGetValue(articleId, out var existing))
            {
                if (existing.Waiters < _maxWaiters)
                {
                    existing.Waiters++;
                    existing.Consumers++;
                    _waiterCount++;
                    Interlocked.Increment(ref _coalescedCount);
                    if (existing.Waiters > _maxObservedWaiters)
                    {
                        _maxObservedWaiters = existing.Waiters;
                    }

                    joined = existing;
                    joinedWaiters = existing.Waiters;
                }
            }
            else if (_flights.Count < _maxInFlight)
            {
                owned = new Flight();
                _flights.Add(articleId, owned);
            }
        }

        if (joined is not null)
        {
            WaitersChanged?.Invoke(joinedWaiters);
            return WaitFor(joined, cancellationToken, out result);
        }

        if (owned is null)
        {
            return RunDirect(read, out result);
        }

        return RunOwner(articleId, owned, cancellationToken, read, out result);
    }

    /// <summary>Performs the read without registering a flight. Used when a bound is already reached.</summary>
    private bool RunDirect(Func<ArticleReadResult?> read, out ArticleReadResult result)
    {
        Interlocked.Increment(ref _physicalReadCount);
        var produced = read();
        if (produced is not ArticleReadResult article)
        {
            result = default;
            return false;
        }

        result = article;
        return true;
    }

    /// <summary>Owns the physical read and publishes one result to current waiters.</summary>
    private bool RunOwner(
        ArticleId articleId,
        Flight flight,
        CancellationToken cancellationToken,
        Func<ArticleReadResult?> read,
        out ArticleReadResult result)
    {
        result = default;
        PhysicalReadOutcome outcome;
        try
        {
            if (TryAbandonSoleCancelledOwner(articleId, flight, cancellationToken))
            {
                outcome = PhysicalReadOutcome.Canceled(cancellationToken);
            }
            else
            {
                Interlocked.Increment(ref _physicalReadCount);
                var produced = read();
                outcome = produced is ArticleReadResult article
                    ? PhysicalReadOutcome.Hit(article)
                    : PhysicalReadOutcome.Miss();
            }
        }
        catch (Exception ex)
        {
            outcome = PhysicalReadOutcome.Fault(ex);
        }

        RetireFlight(articleId, flight);
        flight.Done.TrySetResult(outcome);
        if (outcome.Error is null && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return ApplyOutcome(outcome, out result);
    }

    /// <summary>
    /// Removes <paramref name="flight"/> when this owner is cancelled and no waiter is attached.
    /// The check and removal hold the registry lock so a join cannot land on a skipped flight.
    /// </summary>
    /// <returns>True when the physical read must not start.</returns>
    private bool TryAbandonSoleCancelledOwner(
        ArticleId articleId,
        Flight flight,
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        lock (_gate)
        {
            if (flight.Consumers != 1)
            {
                return false;
            }

            if (_flights.TryGetValue(articleId, out var current) && ReferenceEquals(current, flight))
            {
                _flights.Remove(articleId);
            }

            return true;
        }
    }

    /// <summary>Waits for the owner's outcome. Cancellation detaches this waiter only.</summary>
    private bool WaitFor(Flight flight, CancellationToken cancellationToken, out ArticleReadResult result)
    {
        try
        {
            flight.Done.Task.Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            DetachWaiter(flight);
            throw;
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1
            && ex.InnerExceptions[0] is OperationCanceledException
            && cancellationToken.IsCancellationRequested)
        {
            DetachWaiter(flight);
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
        }

        return ApplyOutcome(flight.Done.Task.GetAwaiter().GetResult(), out result);
    }

    /// <summary>Drops one waiter. Does not complete or cancel the shared read.</summary>
    private void DetachWaiter(Flight flight)
    {
        lock (_gate)
        {
            if (flight.Waiters > 0)
            {
                flight.Waiters--;
                _waiterCount--;
            }

            if (flight.Consumers > 1)
            {
                flight.Consumers--;
            }
        }
    }

    /// <summary>
    /// Removes <paramref name="flight"/> when it is still the registered read and drops its waiters
    /// from the gauge. Safe if <see cref="TryAbandonSoleCancelledOwner"/> already removed it.
    /// </summary>
    private void RetireFlight(ArticleId articleId, Flight flight)
    {
        lock (_gate)
        {
            if (_flights.TryGetValue(articleId, out var current) && ReferenceEquals(current, flight))
            {
                _flights.Remove(articleId);
            }

            if (flight.Waiters > 0)
            {
                _waiterCount -= flight.Waiters;
                flight.Waiters = 0;
            }
        }
    }

    /// <summary>Copies a shared outcome into the caller's result or rethrows the shared failure.</summary>
    private bool ApplyOutcome(PhysicalReadOutcome outcome, out ArticleReadResult result)
    {
        if (outcome.Error is not null)
        {
            result = default;
            RethrowShared(outcome.Error);
        }

        result = outcome.Result;
        return outcome.Found;
    }

    /// <summary>
    /// Rethrows <paramref name="error"/> on this caller. The lock keeps concurrent waiters from
    /// capturing the same instance at the same time. The lock is released as the throw unwinds.
    /// </summary>
    private void RethrowShared(Exception error)
    {
        lock (_rethrowGate)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    /// <summary>One physical read and the waiters attached to it.</summary>
    /// <remarks>Mutated only while the coalescer gate is held, except <see cref="Done"/> completion.</remarks>
    private sealed class Flight
    {
        /// <summary>Owner plus waiters that have not detached.</summary>
        public int Consumers = 1;

        /// <summary>Callers waiting on <see cref="Done"/>, excluding the owner.</summary>
        public int Waiters;

        /// <summary>Completed once by the owner. Not retained by the registry after removal.</summary>
        public TaskCompletionSource<PhysicalReadOutcome> Done { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>One shared physical-read result. Failures are delivered to current waiters only.</summary>
    private readonly struct PhysicalReadOutcome
    {
        private PhysicalReadOutcome(bool found, ArticleReadResult result, Exception? error)
        {
            Found = found;
            Result = result;
            Error = error;
        }

        /// <summary>True when the owner read produced an article.</summary>
        public bool Found { get; }

        /// <summary>Verified article when <see cref="Found"/> is true.</summary>
        public ArticleReadResult Result { get; }

        /// <summary>Shared failure. Null for a hit or a miss. Not stored in the registry.</summary>
        public Exception? Error { get; }

        /// <summary>Creates a successful shared read.</summary>
        public static PhysicalReadOutcome Hit(ArticleReadResult result) => new(true, result, null);

        /// <summary>Creates a shared miss. The article id is not poisoned.</summary>
        public static PhysicalReadOutcome Miss() => new(false, default, null);

        /// <summary>Creates a shared failure.</summary>
        public static PhysicalReadOutcome Fault(Exception error) => new(false, default, error);

        /// <summary>Creates a cancellation observed by an owner that never started the read.</summary>
        public static PhysicalReadOutcome Canceled(CancellationToken cancellationToken) =>
            new(false, default, new OperationCanceledException(cancellationToken));
    }
}
