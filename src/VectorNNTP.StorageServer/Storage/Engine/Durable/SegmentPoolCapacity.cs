using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;

namespace VectorNNTP.StorageServer.Storage.Engine.Durable;

/// <summary>One configured segment spool passed to <see cref="SegmentPoolCapacity"/>.</summary>
/// <param name="Index">Zero-based index in configuration order.</param>
/// <param name="Directory">Resolved segment directory.</param>
/// <param name="MaxCapacity">Hard byte ceiling. <see cref="long.MaxValue"/> means the engine is not enforcing a configured cap.</param>
/// <param name="VolumeKey">Filesystem identity. Equal keys share one physical free-space budget.</param>
internal readonly record struct SegmentPoolDefinition(
    int Index,
    string Directory,
    long MaxCapacity,
    string VolumeKey);

/// <summary>One filesystem sample for a spool. <see cref="Measured"/> false fails that spool closed.</summary>
/// <param name="VolumeKey">Filesystem identity. Must match the spool definition when measured.</param>
/// <param name="TotalBytes">Filesystem total bytes.</param>
/// <param name="AvailableBytes">Bytes available to this caller.</param>
/// <param name="Measured">False when the query failed or the totals are unusable.</param>
internal readonly record struct SegmentPoolVolumeSample(
    string VolumeKey,
    long TotalBytes,
    long AvailableBytes,
    bool Measured);

/// <summary>A process-local spool reservation. <see cref="Token"/> zero is not a reservation.</summary>
/// <param name="Token">Identity of this reservation. Zero means none.</param>
/// <param name="PoolIndex">Spool that holds the bytes.</param>
/// <param name="Bytes">Reserved segment-copy bytes.</param>
internal readonly record struct SegmentPoolReservation(long Token, int PoolIndex, long Bytes)
{
    /// <summary>No reservation.</summary>
    public static SegmentPoolReservation None { get; } = new(0, 0, 0);

    /// <summary>True when <see cref="Token"/> is zero.</summary>
    public bool IsNone => Token == 0;
}

/// <summary>
/// Per-spool hard capacity and shared filesystem admission for segment copies.
/// </summary>
/// <remarks>
/// <para>
/// Accounted bytes for a spool are <c>AllocatedBytes + ReservedBytes</c>. Allocated bytes are
/// segment file lengths on that spool. Reserved bytes are copies whose durable flush has not
/// returned. The sum must not exceed <see cref="SegmentPoolDefinition.MaxCapacity"/>. A logical
/// eviction does not change either counter. <see cref="NoteFileDeleted"/> does.
/// </para>
/// <para>
/// Filesystem admission is separate. Spools with the same <see cref="SegmentPoolDefinition.VolumeKey"/>
/// share one sample. Their reservations are added to that sample's used bytes before the
/// utilization ceiling and the recovery reserve are checked. A failed sample makes that spool
/// ineligible. An index tombstone is not recovered capacity.
/// </para>
/// <para>
/// New bytes stay on the active spool while that spool can accept them and is below Critical,
/// or while every eligible spool is at Critical or worse. Otherwise the least-loaded eligible
/// spool is chosen, compared by accounted bytes over max capacity.
/// </para>
/// </remarks>
internal sealed class SegmentPoolCapacity
{
    /// <summary>Sentinel max capacity meaning this process is not enforcing a configured cap.</summary>
    public const long Uncapped = long.MaxValue;

    /// <summary>Admission reason when the configured cap would be crossed.</summary>
    public const string HardLimitReason = "segment-pool-capacity";

    /// <summary>Admission reason when every candidate spool's filesystem sample failed.</summary>
    public const string UnmeasuredReason = "segment-pool-unmeasured";

    /// <summary>Admission reason when filesystem reserves or pressure leave no eligible spool.</summary>
    public const string BackpressureReason = "segment-pool-backpressure";

    private readonly object _gate = new();
    private readonly Pool[] _pools;
    private readonly BulkStoragePressurePolicy _policy;
    private readonly int _maximumUtilization;
    private readonly bool _tracksReservations;
    private readonly Dictionary<long, Reservation> _byToken = new();
    private readonly Dictionary<ulong, long> _tokenBySequence = new();
    private readonly Dictionary<string, long> _uncreditedDeletedBytes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastFilesystemUsed = new(StringComparer.Ordinal);
    private long _nextToken = 1;

    private SegmentPoolCapacity(
        IReadOnlyList<SegmentPoolDefinition> pools,
        BulkStoragePressurePolicy policy,
        int maximumUtilization)
    {
        ArgumentNullException.ThrowIfNull(pools);
        ArgumentNullException.ThrowIfNull(policy);
        if (pools.Count == 0)
        {
            throw new ArgumentException("At least one segment pool is required.", nameof(pools));
        }

        _policy = policy;
        _maximumUtilization = maximumUtilization;
        _pools = new Pool[pools.Count];
        var enforces = true;
        for (var i = 0; i < pools.Count; i++)
        {
            var definition = pools[i];
            if (definition.MaxCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pools), "Segment pool capacity must be positive.");
            }

            if (definition.MaxCapacity == Uncapped)
            {
                enforces = false;
            }

            _pools[i] = new Pool(definition);
        }

        _tracksReservations = enforces || pools.Count > 1;
    }

    /// <summary>Creates the capacity set for <paramref name="pools"/>.</summary>
    /// <param name="pools">Configured spools in index order.</param>
    /// <param name="policy">Watermarks. Null uses the defaults.</param>
    /// <param name="maximumUtilization">Filesystem utilization ceiling, 1 to 100.</param>
    /// <returns>The capacity set.</returns>
    public static SegmentPoolCapacity Create(
        IReadOnlyList<SegmentPoolDefinition> pools,
        BulkStoragePressureOptions? policy = null,
        int maximumUtilization = ArticleCapacityOptions.DefaultMaximumUtilization) =>
        new(pools, new BulkStoragePressurePolicy(policy), maximumUtilization);

    /// <summary>Number of spools.</summary>
    public int PoolCount => _pools.Length;

    /// <summary>
    /// True when a configured cap or more than one spool is being enforced.
    /// A single uncapped spool does not track reservations.
    /// </summary>
    public bool TracksReservations => _tracksReservations;

    /// <summary>Resolved directory for <paramref name="poolIndex"/>.</summary>
    /// <param name="poolIndex">Spool index.</param>
    /// <returns>The directory path.</returns>
    public string Directory(int poolIndex) => _pools[poolIndex].Definition.Directory;

    /// <summary>Filesystem identity for <paramref name="poolIndex"/>.</summary>
    /// <param name="poolIndex">Spool index.</param>
    /// <returns>The volume key passed at construction.</returns>
    public string VolumeKey(int poolIndex) => _pools[poolIndex].Definition.VolumeKey;

    /// <summary>Hard capacity for <paramref name="poolIndex"/>.</summary>
    /// <param name="poolIndex">Spool index.</param>
    /// <returns>The configured ceiling.</returns>
    public long MaxCapacity(int poolIndex) => _pools[poolIndex].Definition.MaxCapacity;

    /// <summary>Replaces allocated file bytes for every spool. Reservations are unchanged.</summary>
    /// <param name="allocatedBytes">One length per spool, in index order.</param>
    public void SetAllocated(IReadOnlyList<long> allocatedBytes)
    {
        ArgumentNullException.ThrowIfNull(allocatedBytes);
        if (allocatedBytes.Count != _pools.Length)
        {
            throw new ArgumentException("Allocated bytes must cover every spool.", nameof(allocatedBytes));
        }

        lock (_gate)
        {
            for (var i = 0; i < _pools.Length; i++)
            {
                if (allocatedBytes[i] < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(allocatedBytes));
                }

                _pools[i].AllocatedBytes = allocatedBytes[i];
            }
        }
    }

    /// <summary>Accounted bytes for <paramref name="poolIndex"/>: file lengths plus unflushed reservations.</summary>
    /// <param name="poolIndex">Spool index.</param>
    /// <returns>The accounted total.</returns>
    public long AccountedBytes(int poolIndex)
    {
        lock (_gate)
        {
            return AccountedUnlocked(poolIndex);
        }
    }

    /// <summary>Reserved bytes whose flush has not returned.</summary>
    /// <param name="poolIndex">Spool index.</param>
    /// <returns>The reservation total.</returns>
    public long ReservedBytes(int poolIndex)
    {
        lock (_gate)
        {
            return _pools[poolIndex].ReservedBytes;
        }
    }

    /// <summary>
    /// Classifies <paramref name="poolIndex"/> against its configured capacity.
    /// An uncapped spool is unmeasured so filesystem classification remains the only signal.
    /// </summary>
    /// <param name="poolIndex">Spool index.</param>
    /// <returns>The spool evaluation.</returns>
    public BulkStoragePressureEvaluation SpoolEvaluation(int poolIndex)
    {
        lock (_gate)
        {
            return SpoolEvaluationUnlocked(poolIndex);
        }
    }

    /// <summary>The most severe spool evaluation. Unmeasured when no spool has a configured cap.</summary>
    /// <returns>The worst spool class.</returns>
    public BulkStoragePressureEvaluation WorstSpoolEvaluation()
    {
        lock (_gate)
        {
            BulkStoragePressureEvaluation worst = BulkStoragePressurePolicy.Unmeasured();
            for (var i = 0; i < _pools.Length; i++)
            {
                worst = MoreSevere(worst, SpoolEvaluationUnlocked(i));
            }

            return worst;
        }
    }

    /// <summary>
    /// Reserves <paramref name="bytes"/> on an eligible spool.
    /// </summary>
    /// <param name="bytes">Segment-copy bytes. Must be positive when tracking.</param>
    /// <param name="activePool">Spool of the current active segment, or null when there is none.</param>
    /// <param name="samples">One filesystem sample per spool, in index order.</param>
    /// <param name="reservation">The reservation when this returns true. <see cref="SegmentPoolReservation.None"/> when tracking is off.</param>
    /// <param name="reason">Rejection reason when this returns false.</param>
    /// <returns>False when no spool can accept the bytes without crossing a cap or reserve.</returns>
    public bool TryReserve(
        long bytes,
        int? activePool,
        IReadOnlyList<SegmentPoolVolumeSample> samples,
        out SegmentPoolReservation reservation,
        out string? reason)
    {
        reservation = SegmentPoolReservation.None;
        reason = null;
        if (!_tracksReservations)
        {
            return true;
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count != _pools.Length)
        {
            throw new ArgumentException("A filesystem sample is required for every spool.", nameof(samples));
        }

        lock (_gate)
        {
            var chosen = SelectUnlocked(bytes, activePool, samples, out reason);
            if (chosen < 0)
            {
                return false;
            }

            var token = _nextToken++;
            _pools[chosen].ReservedBytes = checked(_pools[chosen].ReservedBytes + bytes);
            _byToken.Add(token, new Reservation(chosen, bytes));
            reservation = new SegmentPoolReservation(token, chosen, bytes);
            reason = null;
            return true;
        }
    }

    /// <summary>Associates <paramref name="reservation"/> with a journal sequence.</summary>
    /// <param name="reservation">Reservation returned by <see cref="TryReserve"/>.</param>
    /// <param name="sequence">Journal sequence.</param>
    public void Bind(SegmentPoolReservation reservation, ulong sequence)
    {
        if (reservation.IsNone)
        {
            return;
        }

        lock (_gate)
        {
            if (!_byToken.ContainsKey(reservation.Token))
            {
                throw new InvalidOperationException($"Segment pool reservation {reservation.Token} is not outstanding.");
            }

            _tokenBySequence[sequence] = reservation.Token;
        }
    }

    /// <summary>
    /// Drops the reservation bound to <paramref name="sequence"/> without counting it as allocated.
    /// A sequence that was never bound or was already persisted does nothing.
    /// </summary>
    /// <param name="sequence">Journal sequence passed to <see cref="Bind"/>.</param>
    public void RollbackSequence(ulong sequence)
    {
        lock (_gate)
        {
            if (!_tokenBySequence.ContainsKey(sequence))
            {
                return;
            }

            ReleaseTokenUnlocked(_tokenBySequence[sequence]);
        }
    }

    /// <summary>Releases a reservation that was not persisted.</summary>
    /// <param name="reservation">Reservation returned by <see cref="TryReserve"/>.</param>
    public void Rollback(SegmentPoolReservation reservation)
    {
        if (reservation.IsNone)
        {
            return;
        }

        lock (_gate)
        {
            ReleaseTokenUnlocked(reservation.Token);
        }
    }

    /// <summary>
    /// Drops <paramref name="reservation"/> and replaces that spool's allocated bytes with the
    /// callback result. Used when the durable bytes are a compaction destination rather than an
    /// Accept sequence. A second call for the same token does nothing.
    /// </summary>
    /// <param name="reservation">Reservation returned by <see cref="TryReserve"/>.</param>
    /// <param name="allocatedBytes">File bytes for one spool index. Runs under the pool lock.</param>
    public void NoteReservationPersisted(SegmentPoolReservation reservation, Func<int, long> allocatedBytes)
    {
        if (reservation.IsNone)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(allocatedBytes);
        lock (_gate)
        {
            if (!RemoveReservationUnlocked(reservation.Token, out var stored))
            {
                return;
            }

            ApplyPersistedUnlocked(stored, allocatedBytes);
        }
    }

    /// <summary>
    /// Drops the reservation for <paramref name="sequence"/> and replaces that spool's allocated
    /// bytes with <paramref name="allocatedBytes"/>. The callback runs under the pool lock and
    /// must return the spool's current segment-file bytes, including the copy just flushed.
    /// A second call for the same sequence does nothing.
    /// </summary>
    /// <param name="sequence">Journal sequence passed to <see cref="Bind"/>.</param>
    /// <param name="allocatedBytes">File bytes for one spool index. Runs under the pool lock.</param>
    public void NotePersisted(ulong sequence, Func<int, long> allocatedBytes)
    {
        ArgumentNullException.ThrowIfNull(allocatedBytes);
        lock (_gate)
        {
            if (!_tokenBySequence.Remove(sequence, out var token))
            {
                return;
            }

            if (!RemoveReservationUnlocked(token, out var reservation))
            {
                return;
            }

            ApplyPersistedUnlocked(reservation, allocatedBytes);
        }
    }

    private bool RemoveReservationUnlocked(long token, out Reservation reservation)
    {
        if (!_byToken.Remove(token, out reservation))
        {
            return false;
        }

        if (_tokenBySequence.Count > 0)
        {
            ulong? bound = null;
            foreach (var pair in _tokenBySequence)
            {
                if (pair.Value == token)
                {
                    bound = pair.Key;
                    break;
                }
            }

            if (bound is ulong sequence)
            {
                _tokenBySequence.Remove(sequence);
            }
        }

        return true;
    }

    private void ApplyPersistedUnlocked(Reservation reservation, Func<int, long> allocatedBytes)
    {
        var pool = _pools[reservation.Pool];
        pool.ReservedBytes -= reservation.Bytes;
        if (pool.ReservedBytes < 0)
        {
            pool.ReservedBytes = 0;
        }

        var allocated = allocatedBytes(reservation.Pool);
        if (allocated < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(allocatedBytes));
        }

        pool.AllocatedBytes = allocated;
    }

    /// <summary>Gets the spool a sequence was bound to.</summary>
    /// <param name="sequence">Journal sequence.</param>
    /// <param name="poolIndex">The spool, when this returns true.</param>
    /// <returns>False when the sequence has no outstanding or persisted binding.</returns>
    public bool TryGetPool(ulong sequence, out int poolIndex)
    {
        lock (_gate)
        {
            if (_tokenBySequence.TryGetValue(sequence, out var token)
                && _byToken.TryGetValue(token, out var reservation))
            {
                poolIndex = reservation.Pool;
                return true;
            }
        }

        poolIndex = 0;
        return false;
    }

    /// <summary>
    /// Drops <paramref name="fileBytes"/> from the spool after the segment file has been deleted.
    /// Bytes the filesystem sample has not yet released stay uncredited so a later cycle does not
    /// treat the stale used-space as a reason to evict again.
    /// </summary>
    /// <param name="poolIndex">Spool that owned the file.</param>
    /// <param name="fileBytes">Deleted file length.</param>
    /// <param name="filesystemUsedBefore">Filesystem used bytes before the delete.</param>
    /// <param name="filesystemUsedAfter">Filesystem used bytes after the delete.</param>
    public void NoteFileDeleted(int poolIndex, long fileBytes, long filesystemUsedBefore, long filesystemUsedAfter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileBytes);
        lock (_gate)
        {
            var pool = _pools[poolIndex];
            pool.AllocatedBytes = Math.Max(0, pool.AllocatedBytes - fileBytes);
            var observed = Math.Max(0L, filesystemUsedBefore - filesystemUsedAfter);
            var unseen = fileBytes - observed;
            if (unseen > 0)
            {
                var key = pool.Definition.VolumeKey;
                _uncreditedDeletedBytes[key] = _uncreditedDeletedBytes.GetValueOrDefault(key) + unseen;
            }

            _lastFilesystemUsed[pool.Definition.VolumeKey] = filesystemUsedAfter;
        }
    }

    /// <summary>
    /// Applies a later filesystem sample to pending delete credit.
    /// A drop reduces the uncredited remainder. A sample that does not drop does not clear it.
    /// </summary>
    /// <param name="volumeKey">Filesystem identity.</param>
    /// <param name="measuredUsedBytes">Current used bytes from that filesystem.</param>
    public void ObserveFilesystem(string volumeKey, long measuredUsedBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeKey);
        lock (_gate)
        {
            if (_lastFilesystemUsed.TryGetValue(volumeKey, out var previous) && measuredUsedBytes < previous)
            {
                var drop = previous - measuredUsedBytes;
                var credit = _uncreditedDeletedBytes.GetValueOrDefault(volumeKey);
                _uncreditedDeletedBytes[volumeKey] = Math.Max(0, credit - drop);
            }

            _lastFilesystemUsed[volumeKey] = measuredUsedBytes;
        }
    }

    /// <summary>
    /// Used bytes that still require eviction after pending deletes are credited.
    /// </summary>
    /// <param name="volumeKey">Filesystem identity.</param>
    /// <param name="measuredUsedBytes">Current used bytes.</param>
    /// <returns>Measured used bytes minus deletes the sample has not released.</returns>
    public long EffectiveFilesystemUsed(string volumeKey, long measuredUsedBytes)
    {
        lock (_gate)
        {
            var credit = _uncreditedDeletedBytes.GetValueOrDefault(volumeKey);
            return Math.Max(0, measuredUsedBytes - credit);
        }
    }

    /// <summary>
    /// Returns the more severe of two evaluations. An unmeasured value loses to a measured one.
    /// Equal states keep the higher used percent.
    /// </summary>
    /// <param name="left">First evaluation.</param>
    /// <param name="right">Second evaluation.</param>
    /// <returns>The evaluation maintenance should act on.</returns>
    public static BulkStoragePressureEvaluation MoreSevere(
        in BulkStoragePressureEvaluation left,
        in BulkStoragePressureEvaluation right)
    {
        if (!left.Measured)
        {
            return right;
        }

        if (!right.Measured)
        {
            return left;
        }

        if (right.State > left.State)
        {
            return right;
        }

        if (left.State > right.State)
        {
            return left;
        }

        return right.UsedPercent > left.UsedPercent ? right : left;
    }

    private int SelectUnlocked(
        long bytes,
        int? activePool,
        IReadOnlyList<SegmentPoolVolumeSample> samples,
        out string? reason)
    {
        Span<bool> eligible = stackalloc bool[_pools.Length];
        var anyEligible = false;
        var anyMeasured = false;
        var anyHardLimit = false;
        for (var i = 0; i < _pools.Length; i++)
        {
            var block = ClassifyUnlocked(i, bytes, samples[i]);
            if (block == Block.None)
            {
                eligible[i] = true;
                anyEligible = true;
                anyMeasured = true;
            }
            else if (block == Block.Unmeasured)
            {
            }
            else
            {
                anyMeasured = true;
                anyHardLimit |= block == Block.HardLimit;
            }
        }

        if (!anyEligible)
        {
            reason = !anyMeasured
                ? UnmeasuredReason
                : anyHardLimit
                    ? HardLimitReason
                    : BackpressureReason;
            return -1;
        }

        Span<bool> leaving = stackalloc bool[_pools.Length];
        var restrictPressured = false;
        for (var i = 0; i < _pools.Length; i++)
        {
            leaving[i] = LeaveRotationUnlocked(i, samples[i]);
            if (eligible[i] && !leaving[i])
            {
                restrictPressured = true;
            }
        }

        if (activePool is int active
            && (uint)active < (uint)_pools.Length
            && eligible[active]
            && (!restrictPressured || !leaving[active]))
        {
            reason = null;
            return active;
        }

        var best = -1;
        for (var i = 0; i < _pools.Length; i++)
        {
            if (!eligible[i])
            {
                continue;
            }

            if (restrictPressured && leaving[i])
            {
                continue;
            }

            if (best < 0 || LoadCompare(i, best) < 0)
            {
                best = i;
            }
        }

        reason = null;
        return best;
    }

    private Block ClassifyUnlocked(int pool, long bytes, in SegmentPoolVolumeSample sample)
    {
        if (!sample.Measured || sample.TotalBytes <= 0 || sample.AvailableBytes < 0)
        {
            return Block.Unmeasured;
        }

        var accounted = AccountedUnlocked(pool);
        if (accounted > _pools[pool].Definition.MaxCapacity - bytes)
        {
            return Block.HardLimit;
        }

        var reservedOnVolume = ReservedOnVolumeUnlocked(_pools[pool].Definition.VolumeKey);
        var used = Math.Max(0L, sample.TotalBytes - sample.AvailableBytes);
        var evaluation = _policy.Evaluate(sample.TotalBytes, used, sample.AvailableBytes);
        if (!_policy.AllowsNewAccept(in evaluation, bytes, reservedOnVolume))
        {
            return Block.Backpressure;
        }

        if (!ProcessLocalCapacityLedger.WouldFit(
                used,
                reservedOnVolume,
                compactionReservedBytes: 0,
                sample.TotalBytes,
                bytes,
                _maximumUtilization))
        {
            return Block.Backpressure;
        }

        var spool = SpoolEvaluationUnlocked(pool);
        if (spool.Measured && !_policy.AllowsNewAccept(in spool, bytes, _pools[pool].ReservedBytes))
        {
            return Block.Backpressure;
        }

        return Block.None;
    }

    /// <summary>
    /// True when new writes should leave this spool. Entering Pressure latches the spool.
    /// The latch clears only after used space falls below the recovery target, so a spool
    /// does not return while it is still inside the recovery margin.
    /// </summary>
    private bool LeaveRotationUnlocked(int pool, in SegmentPoolVolumeSample sample)
    {
        if (IsPressureOrWorseUnlocked(pool, sample))
        {
            _pools[pool].RotationLatched = true;
            return true;
        }

        if (!_pools[pool].RotationLatched)
        {
            return false;
        }

        if (RotationPercentUnlocked(pool, sample) < RecoveryTargetPercent())
        {
            _pools[pool].RotationLatched = false;
            return false;
        }

        return true;
    }

    private int RecoveryTargetPercent()
    {
        var target = _policy.Options.PressurePercent - _policy.Options.PressureRecoveryMarginPercent;
        return target < 0 ? 0 : target;
    }

    private int RotationPercentUnlocked(int pool, in SegmentPoolVolumeSample sample)
    {
        var spool = SpoolEvaluationUnlocked(pool);
        var percent = spool.Measured ? spool.UsedPercent : 0;
        if (!sample.Measured || sample.TotalBytes <= 0)
        {
            return percent;
        }

        var used = Math.Max(0L, sample.TotalBytes - sample.AvailableBytes);
        var filesystem = _policy.Evaluate(sample.TotalBytes, used, sample.AvailableBytes);
        return filesystem.Measured && filesystem.UsedPercent > percent
            ? filesystem.UsedPercent
            : percent;
    }

    /// <summary>
    /// True when this spool or its filesystem is at Pressure or worse right now.
    /// An unmeasured filesystem stays out of rotation.
    /// </summary>
    private bool IsPressureOrWorseUnlocked(int pool, in SegmentPoolVolumeSample sample)
    {
        var spool = SpoolEvaluationUnlocked(pool);
        if (spool.Measured
            && spool.State is BulkStoragePressureState.Pressure
                or BulkStoragePressureState.High
                or BulkStoragePressureState.Critical
                or BulkStoragePressureState.Emergency)
        {
            return true;
        }

        if (!sample.Measured || sample.TotalBytes <= 0)
        {
            return true;
        }

        var used = Math.Max(0L, sample.TotalBytes - sample.AvailableBytes);
        var filesystem = _policy.Evaluate(sample.TotalBytes, used, sample.AvailableBytes);
        return filesystem.State is BulkStoragePressureState.Pressure
            or BulkStoragePressureState.High
            or BulkStoragePressureState.Critical
            or BulkStoragePressureState.Emergency;
    }

    private int LoadCompare(int left, int right)
    {
        var leftAccounted = AccountedUnlocked(left);
        var rightAccounted = AccountedUnlocked(right);
        var leftMax = _pools[left].Definition.MaxCapacity;
        var rightMax = _pools[right].Definition.MaxCapacity;
        var leftRatio = (double)leftAccounted / leftMax;
        var rightRatio = (double)rightAccounted / rightMax;
        var compared = leftRatio.CompareTo(rightRatio);
        return compared != 0 ? compared : left.CompareTo(right);
    }

    private long ReservedOnVolumeUnlocked(string volumeKey)
    {
        long reserved = 0;
        foreach (var pool in _pools)
        {
            if (string.Equals(pool.Definition.VolumeKey, volumeKey, StringComparison.Ordinal))
            {
                reserved += pool.ReservedBytes;
            }
        }

        return reserved;
    }

    private long AccountedUnlocked(int pool) =>
        checked(_pools[pool].AllocatedBytes + _pools[pool].ReservedBytes);

    private BulkStoragePressureEvaluation SpoolEvaluationUnlocked(int pool)
    {
        var max = _pools[pool].Definition.MaxCapacity;
        if (max == Uncapped)
        {
            return BulkStoragePressurePolicy.Unmeasured();
        }

        var accounted = AccountedUnlocked(pool);
        var available = Math.Max(0L, max - accounted);
        return _policy.Evaluate(max, accounted, available);
    }

    private void ReleaseTokenUnlocked(long token)
    {
        if (!_byToken.Remove(token, out var reservation))
        {
            return;
        }

        foreach (var pair in _tokenBySequence)
        {
            if (pair.Value == token)
            {
                _tokenBySequence.Remove(pair.Key);
                break;
            }
        }

        var pool = _pools[reservation.Pool];
        pool.ReservedBytes -= reservation.Bytes;
        if (pool.ReservedBytes < 0)
        {
            pool.ReservedBytes = 0;
        }
    }

    private enum Block
    {
        None = 0,
        Unmeasured = 1,
        HardLimit = 2,
        Backpressure = 3,
    }

    private sealed class Pool(SegmentPoolDefinition definition)
    {
        public SegmentPoolDefinition Definition { get; } = definition;

        public long AllocatedBytes { get; set; }

        public long ReservedBytes { get; set; }

        /// <summary>True after this spool has entered Pressure until it falls below the recovery target.</summary>
        public bool RotationLatched { get; set; }
    }

    private readonly record struct Reservation(int Pool, long Bytes);
}
