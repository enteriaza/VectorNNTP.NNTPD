namespace VectorNNTP.BackFiller.Hosting.Systemd;

/// <summary>
/// Calculates systemd watchdog heartbeat intervals from the systemd-provided deadline.
/// </summary>
internal static class SystemdWatchdogInterval
{
    /// <summary>
    /// Derives a heartbeat interval from a fraction of the systemd watchdog deadline.
    /// </summary>
    /// <param name="watchdogTimeout">Deadline supplied by systemd (<c>WATCHDOG_USEC</c>).</param>
    /// <param name="fraction">Fraction of the deadline between heartbeats; must be in (0, 1).</param>
    /// <returns>
    /// The floored fraction of <paramref name="watchdogTimeout"/>, raised to at least one millisecond
    /// and then limited to one millisecond before the deadline when that limit is at least one millisecond.
    /// A one-millisecond deadline therefore yields a one-millisecond interval.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="watchdogTimeout"/> is not positive, or when <paramref name="fraction"/>
    /// is NaN or outside the open interval (0, 1).
    /// </exception>
    internal static TimeSpan Calculate(TimeSpan watchdogTimeout, double fraction = 0.5)
    {
        if (watchdogTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(watchdogTimeout), watchdogTimeout, "Watchdog timeout must be positive.");
        }

        if (double.IsNaN(fraction) || fraction is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "Fraction must be in the open interval (0, 1).");
        }

        var ticks = (long)Math.Floor(watchdogTimeout.Ticks * fraction);
        if (ticks < TimeSpan.TicksPerMillisecond)
        {
            ticks = TimeSpan.TicksPerMillisecond;
        }

        // Prefer a 1ms margin. A 1ms deadline cannot leave that margin, so the interval stays 1ms.
        var maxTicks = Math.Max(TimeSpan.TicksPerMillisecond, watchdogTimeout.Ticks - TimeSpan.TicksPerMillisecond);
        if (ticks > maxTicks)
        {
            ticks = maxTicks;
        }

        return TimeSpan.FromTicks(ticks);
    }
}
