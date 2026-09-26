namespace VectorNNTP.BackFiller.Tests.TestDoubles;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _utcNow;
    private readonly List<ManualTimer> _timers = [];

    public ManualTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public bool HasPendingTimer
    {
        get
        {
            lock (_gate)
            {
                return _timers.Exists(static timer => timer.NextDue is not null);
            }
        }
    }

    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delta.Ticks);
        DateTimeOffset target;
        lock (_gate)
        {
            target = _utcNow + delta;
        }

        while (true)
        {
            ManualTimer? due = null;
            DateTimeOffset dueAt = default;
            lock (_gate)
            {
                foreach (var timer in _timers)
                {
                    if (timer.NextDue is { } next && next <= target && (due is null || next < dueAt))
                    {
                        due = timer;
                        dueAt = next;
                    }
                }

                if (due is null)
                {
                    _utcNow = target;
                    return;
                }

                _utcNow = dueAt;
            }

            due.Fire();
        }
    }

    public void SetUtcNow(DateTimeOffset utcNow)
    {
        lock (_gate)
        {
            _utcNow = utcNow;
        }
    }

    internal void Register(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Add(timer);
        }
    }

    internal void Unregister(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    internal DateTimeOffset Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }
    }

    internal sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            owner.Register(this);
        }

        public DateTimeOffset? NextDue { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            NextDue = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : _owner.Snapshot + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
            return true;
        }

        public void Fire()
        {
            NextDue = _period == Timeout.InfiniteTimeSpan ? null : _owner.Snapshot + _period;
            _callback(_state);
        }

        public void Dispose()
        {
            NextDue = null;
            _owner.Unregister(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
