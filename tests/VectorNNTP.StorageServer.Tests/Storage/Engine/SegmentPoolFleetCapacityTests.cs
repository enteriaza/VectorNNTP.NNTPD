using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// The fleet triple counts every spool, once per filesystem, and does not invent free bytes.
/// </summary>
public sealed class SegmentPoolFleetCapacityTests
{
    [Fact]
    public void One_spool_advertises_remaining_cap_when_the_disk_is_larger()
    {
        var snapshot = Combine(
            Sample("vol", max: 1_000, accounted: 200, reserved: 0, measured: true, total: 10_000, available: 9_000));

        Assert.Equal(200, snapshot.UsedBytes);
        Assert.Equal(800, snapshot.AvailableBytes);
        Assert.Equal(1_000, snapshot.TotalBytes);
    }

    [Fact]
    public void Independent_volumes_add_their_remaining_room()
    {
        var snapshot = Combine(
            Sample("a", 100, 40, 0, true, 10_000, 9_000),
            Sample("b", 100, 10, 0, true, 20_000, 18_000));

        Assert.Equal(50, snapshot.UsedBytes);
        Assert.Equal(150, snapshot.AvailableBytes);
        Assert.Equal(200, snapshot.TotalBytes);
    }

    [Fact]
    public void Directories_on_one_volume_share_one_free_space_budget()
    {
        var snapshot = Combine(
            Sample("vol", 1_000, 0, 0, true, 200, 100),
            Sample("vol", 1_000, 0, 0, true, 200, 100));

        Assert.Equal(0, snapshot.UsedBytes);
        Assert.Equal(100, snapshot.AvailableBytes);
        Assert.Equal(100, snapshot.TotalBytes);
    }

    [Fact]
    public void A_failed_volume_adds_accounted_bytes_and_no_free_space()
    {
        var snapshot = Combine(
            Sample("a", 10_000, 0, 0, true, 200, 80),
            Sample("b", 100, 25, 0, measured: false, total: 0, available: 0));

        Assert.Equal(25, snapshot.UsedBytes);
        Assert.Equal(80, snapshot.AvailableBytes);
        Assert.Equal(105, snapshot.TotalBytes);
    }

    [Fact]
    public void Every_measurement_failing_does_not_advertise_free_space()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Combine(
            Sample("a", 100, 10, 0, false, 0, 0),
            Sample("b", 100, 10, 0, false, 0, 0)));
        Assert.Contains("measure", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_or_non_positive_configuration_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SegmentPoolFleetCapacity.Combine([], maximumUtilization: 100, Policy()));
        Assert.Throws<InvalidOperationException>(() => Combine(
            Sample("vol", max: 0, accounted: 0, reserved: 0, measured: true, total: 100, available: 100)));
    }

    [Fact]
    public void High_watermark_keeps_the_recovery_reserve_out_of_advertised_room()
    {
        var snapshot = Combine(
            Sample("vol", max: 10_000, accounted: 0, reserved: 0, measured: true, total: 1_000, available: 100));

        Assert.Equal(0, snapshot.UsedBytes);
        Assert.Equal(50, snapshot.AvailableBytes);
        Assert.Equal(50, snapshot.TotalBytes);
    }

    [Fact]
    public void Engine_advertisement_matches_the_spool_collection()
    {
        using var dir = FleetDir.Create(2);
        var readers = new IStorageCapacityReader[]
        {
            new FixedReader(200, 100),
            new FixedReader(200, 100),
        };
        var options = dir.Options(maxCapacity: 5_000) with { CapacityMaximumUtilization = 100 };
        using var engine = FileArticleStorageEngine.Open(
            options,
            new SharedProbe("vol"),
            logger: NullLogger.Instance,
            segmentPoolReaders: readers);

        var snapshot = engine.ReadFleetAdvertisement();
        Assert.Equal(0, snapshot.UsedBytes);
        Assert.Equal(100, snapshot.AvailableBytes);
        Assert.Equal(100, snapshot.TotalBytes);
    }

    private static StorageCapacitySnapshot Combine(params SegmentPoolFleetCapacity.SpoolSample[] samples) =>
        SegmentPoolFleetCapacity.Combine(samples, maximumUtilization: 100, Policy());

    private static BulkStoragePressurePolicy Policy() => new(new BulkStoragePressureOptions());

    private static SegmentPoolFleetCapacity.SpoolSample Sample(
        string volume,
        long max,
        long accounted,
        long reserved,
        bool measured,
        long total,
        long available) =>
        new(volume, max, accounted, reserved, measured, total, available);

    private sealed class FixedReader(long total, long available) : IStorageCapacityReader
    {
        public StorageCapacitySnapshot Read() => new(total, Math.Max(0, total - available), available);
    }

    private sealed class SharedProbe(string key) : IStorageVolumeProbe
    {
        public bool TryResolve(string directoryPath, out StorageVolumeIdentity identity)
        {
            identity = new StorageVolumeIdentity(key);
            return true;
        }
    }

    private sealed class FleetDir : IDisposable
    {
        private FleetDir(string root, string control, string[] pools)
        {
            Root = root;
            Control = control;
            Pools = pools;
        }

        public string Root { get; }

        public string Control { get; }

        public string[] Pools { get; }

        public static FleetDir Create(int poolCount)
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-fleet-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            Directory.CreateDirectory(control);
            var pools = new string[poolCount];
            for (var i = 0; i < poolCount; i++)
            {
                pools[i] = Path.Combine(root, "pool-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(pools[i]);
            }

            return new FleetDir(root, control, pools);
        }

        public ArticleStorageRuntimeOptions Options(long maxCapacity)
        {
            var runtimes = new SegmentPoolRuntime[Pools.Length];
            for (var i = 0; i < runtimes.Length; i++)
            {
                runtimes[i] = new SegmentPoolRuntime(i, Pools[i], maxCapacity);
            }

            return new ArticleStorageRuntimeOptions(
                Control,
                Pools[0],
                ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                ArticleStorageOptions.DefaultJournalHardLimitBytes,
                ArticleStorageOptions.DefaultSegmentTargetSizeBytes,
                SegmentPools: runtimes);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
