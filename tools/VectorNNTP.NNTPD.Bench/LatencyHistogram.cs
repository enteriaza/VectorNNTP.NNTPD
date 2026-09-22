namespace VectorNNTP.NNTPD.Bench;

/// <summary>
/// Compact latency histogram in 0.01 ms buckets up to 100 ms, plus an overflow bucket.
/// </summary>
internal sealed class LatencyHistogram
{
    private const int BucketCount = 10_000; // 0.00 .. 99.99 ms
    private readonly long[] _buckets = new long[BucketCount + 1];
    private long _count;
    private long _sumTicks;
    private long _minTicks = long.MaxValue;
    private long _maxTicks;

    public void Reset()
    {
        Array.Clear(_buckets);
        _count = 0;
        _sumTicks = 0;
        _minTicks = long.MaxValue;
        _maxTicks = 0;
    }

    public void Record(TimeSpan elapsed)
    {
        var ticks = elapsed.Ticks;
        if (ticks < _minTicks)
        {
            _minTicks = ticks;
        }

        if (ticks > _maxTicks)
        {
            _maxTicks = ticks;
        }

        _sumTicks += ticks;
        _count++;

        var hundredths = (int)(elapsed.TotalMilliseconds * 100.0);
        if (hundredths < 0)
        {
            hundredths = 0;
        }

        if (hundredths >= BucketCount)
        {
            _buckets[BucketCount]++;
        }
        else
        {
            _buckets[hundredths]++;
        }
    }

    public void Merge(LatencyHistogram other)
    {
        for (var i = 0; i < _buckets.Length; i++)
        {
            _buckets[i] += other._buckets[i];
        }

        _count += other._count;
        _sumTicks += other._sumTicks;
        if (other._count > 0)
        {
            if (other._minTicks < _minTicks)
            {
                _minTicks = other._minTicks;
            }

            if (other._maxTicks > _maxTicks)
            {
                _maxTicks = other._maxTicks;
            }
        }
    }

    public double MinMs => _count == 0 ? 0 : _minTicks / (double)TimeSpan.TicksPerMillisecond;
    public double MaxMs => _count == 0 ? 0 : _maxTicks / (double)TimeSpan.TicksPerMillisecond;
    public double AverageMs => _count == 0 ? 0 : (_sumTicks / (double)_count) / TimeSpan.TicksPerMillisecond;

    public double PercentileMs(double percentile)
    {
        if (_count == 0)
        {
            return 0;
        }

        var target = (long)Math.Ceiling(_count * (percentile / 100.0));
        target = Math.Clamp(target, 1, _count);
        long seen = 0;
        for (var i = 0; i < BucketCount; i++)
        {
            seen += _buckets[i];
            if (seen >= target)
            {
                return i / 100.0;
            }
        }

        return MaxMs;
    }
}
