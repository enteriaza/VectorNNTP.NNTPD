using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Per-spool caps, shared filesystem reserves, and write placement.
/// Samples are supplied by the test. Nothing reads a live disk.
/// </summary>
public sealed class SegmentPoolCapacityTests
{
    [Fact]
    public void Hard_cap_rejects_a_copy_that_would_pass_the_configured_limit()
    {
        var pools = Create(("a", 100));
        pools.SetAllocated([50]);
        var sample = Room("a");

        Assert.False(pools.TryReserve(60, activePool: 0, [sample], out _, out var reason));
        Assert.Equal(SegmentPoolCapacity.HardLimitReason, reason);
        Assert.Equal(50, pools.AccountedBytes(0));
    }

    [Fact]
    public void Concurrent_reservations_cannot_exceed_the_cap()
    {
        var pools = Create(("a", 100));
        var sample = Room("a");
        var wins = 0;

        Parallel.For(0, 2, _ =>
        {
            if (pools.TryReserve(60, activePool: 0, [sample], out SegmentPoolReservation _, out string? _))
            {
                Interlocked.Increment(ref wins);
            }
        });

        Assert.Equal(1, wins);
        Assert.Equal(60, pools.ReservedBytes(0));
    }

    [Fact]
    public void Shared_volume_reservations_are_one_filesystem_budget()
    {
        var pools = Create(("vol", 100_000), ("vol", 100_000));
        var crowded = new SegmentPoolVolumeSample("vol", TotalBytes: 1_000, AvailableBytes: 1_000, Measured: true);
        Assert.True(pools.TryReserve(500, activePool: 0, [crowded, crowded], out var first, out _));
        Assert.Equal(0, first.PoolIndex);

        Assert.False(pools.TryReserve(400, activePool: 0, [crowded, crowded], out _, out var reason));
        Assert.Equal(SegmentPoolCapacity.BackpressureReason, reason);
        Assert.Equal(500, pools.ReservedBytes(0));
        Assert.Equal(0, pools.ReservedBytes(1));
    }

    [Fact]
    public void Distinct_volumes_do_not_share_a_reservation_budget()
    {
        var pools = Create(("a", 100_000), ("b", 100_000));
        var crowded = new SegmentPoolVolumeSample("a", TotalBytes: 1_000, AvailableBytes: 1_000, Measured: true);
        var open = Room("b");
        Assert.True(pools.TryReserve(500, activePool: 0, [crowded, open], out _, out _));

        Assert.True(pools.TryReserve(400, activePool: 0, [crowded, open], out var second, out _));
        Assert.Equal(1, second.PoolIndex);
        Assert.Equal(400, pools.ReservedBytes(1));
    }

    [Fact]
    public void Critical_spool_is_avoided_while_a_healthier_spool_can_accept()
    {
        var pools = Create(("a", 1_000), ("b", 1_000));
        pools.SetAllocated([900, 0]);
        var samples = new[] { Room("a"), Room("b") };

        Assert.Equal(BulkStoragePressureState.Critical, pools.SpoolEvaluation(0).State);
        Assert.True(pools.TryReserve(50, activePool: 0, samples, out var reservation, out _));
        Assert.Equal(1, reservation.PoolIndex);
    }

    [Fact]
    public void Pressure_spool_is_left_while_a_healthier_spool_can_accept()
    {
        var pools = Create(("a", 1_000), ("b", 1_000));
        pools.SetAllocated([820, 0]);
        var samples = new[] { Room("a"), Room("b") };

        Assert.Equal(BulkStoragePressureState.Pressure, pools.SpoolEvaluation(0).State);
        Assert.True(pools.TryReserve(50, activePool: 0, samples, out var reservation, out _));
        Assert.Equal(1, reservation.PoolIndex);
        Assert.Equal(50, pools.ReservedBytes(1));
        Assert.Equal(0, pools.ReservedBytes(0));
    }

    [Fact]
    public void A_pressured_spool_stays_out_until_it_is_below_the_recovery_target()
    {
        var pools = Create(("a", 1_000), ("b", 1_000));
        var samples = new[] { Room("a"), Room("b") };
        pools.SetAllocated([820, 0]);
        Assert.True(pools.TryReserve(50, activePool: 0, samples, out var left, out _));
        Assert.Equal(1, left.PoolIndex);
        pools.Rollback(left);

        pools.SetAllocated([760, 0]);
        Assert.Equal(BulkStoragePressureState.Warning, pools.SpoolEvaluation(0).State);
        Assert.True(pools.TryReserve(50, activePool: 0, samples, out var held, out _));
        Assert.Equal(1, held.PoolIndex);
        pools.Rollback(held);

        pools.SetAllocated([740, 0]);
        Assert.True(pools.TryReserve(50, activePool: 0, samples, out var returned, out _));
        Assert.Equal(0, returned.PoolIndex);
    }

    [Fact]
    public void Active_spool_is_kept_while_it_is_below_critical()
    {
        var pools = Create(("a", 1_000), ("b", 1_000));
        pools.SetAllocated([100, 0]);
        var samples = new[] { Room("a"), Room("b") };

        Assert.True(pools.TryReserve(50, activePool: 0, samples, out var reservation, out _));
        Assert.Equal(0, reservation.PoolIndex);
    }

    [Fact]
    public void Unmeasured_spool_fails_closed_and_a_measured_peer_remains_usable()
    {
        var unmeasured = new SegmentPoolVolumeSample("a", 0, 0, Measured: false);
        var alone = Create(("a", 1_000));
        Assert.False(alone.TryReserve(50, activePool: 0, [unmeasured], out _, out var only));
        Assert.Equal(SegmentPoolCapacity.UnmeasuredReason, only);

        var pools = Create(("a", 1_000), ("b", 1_000));

        Assert.True(pools.TryReserve(50, activePool: 0, [unmeasured, Room("b")], out var reservation, out _));
        Assert.Equal(1, reservation.PoolIndex);
    }

    [Fact]
    public void Deleted_file_bytes_are_released_before_the_next_reservation()
    {
        var pools = Create(("a", 100));
        pools.SetAllocated([100]);
        Assert.False(pools.TryReserve(20, activePool: 0, [Room("a")], out _, out _));

        pools.NoteFileDeleted(0, fileBytes: 40, filesystemUsedBefore: 100, filesystemUsedAfter: 100);
        Assert.Equal(60, pools.AccountedBytes(0));
        Assert.True(pools.TryReserve(20, activePool: 0, [Room("a")], out _, out _));
    }

    [Fact]
    public void Persisted_copy_is_counted_once()
    {
        var pools = Create(("a", 1_000));
        Assert.True(pools.TryReserve(50, activePool: null, [Room("a")], out var reservation, out _));
        pools.Bind(reservation, sequence: 7);
        pools.NotePersisted(7, _ => 50);

        Assert.Equal(0, pools.ReservedBytes(0));
        Assert.Equal(50, pools.AccountedBytes(0));
        pools.NotePersisted(7, _ => 999);
        Assert.Equal(50, pools.AccountedBytes(0));
    }

    [Fact]
    public void Rollback_releases_the_reservation()
    {
        var pools = Create(("a", 1_000));
        Assert.True(pools.TryReserve(50, activePool: 0, [Room("a")], out var reservation, out _));
        pools.Rollback(reservation);
        Assert.Equal(0, pools.ReservedBytes(0));
        Assert.Equal(0, pools.AccountedBytes(0));
    }

    [Fact]
    public void Single_uncapped_spool_does_not_track_reservations()
    {
        var pools = SegmentPoolCapacity.Create(
            [new SegmentPoolDefinition(0, "cache", SegmentPoolCapacity.Uncapped, "vol")]);
        Assert.False(pools.TracksReservations);
        Assert.True(pools.TryReserve(1, activePool: null, [], out var reservation, out _));
        Assert.True(reservation.IsNone);
    }

    [Fact]
    public void More_severe_evaluation_prefers_a_measured_spool_over_an_empty_filesystem()
    {
        var pools = Create(("a", 100));
        pools.SetAllocated([80]);
        var spool = pools.WorstSpoolEvaluation();
        var filesystem = BulkStoragePressurePolicy.Unmeasured();

        var chosen = SegmentPoolCapacity.MoreSevere(filesystem, spool);
        Assert.Equal(BulkStoragePressureState.Pressure, chosen.State);
        Assert.True(chosen.Measured);
    }

    private static SegmentPoolCapacity Create(params (string Volume, long Max)[] specs)
    {
        var definitions = new SegmentPoolDefinition[specs.Length];
        for (var i = 0; i < specs.Length; i++)
        {
            definitions[i] = new SegmentPoolDefinition(i, "spool-" + i, specs[i].Max, specs[i].Volume);
        }

        return SegmentPoolCapacity.Create(
            definitions,
            policy: null,
            maximumUtilization: 80);
    }

    private static SegmentPoolVolumeSample Room(string volume) =>
        new(volume, TotalBytes: 1_000_000, AvailableBytes: 1_000_000, Measured: true);
}
