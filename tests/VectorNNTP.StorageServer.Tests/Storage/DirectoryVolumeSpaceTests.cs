using VectorNNTP.StorageServer.Storage;
using Xunit.Abstractions;
using VectorNNTP.StorageServer.Storage.Cache.Egress;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;

namespace VectorNNTP.StorageServer.Tests.Storage;

/// <summary>
/// Capacity accounting follows the filesystem that contains the directory.
/// Simulated tests inject <see cref="IDirectoryVolumeSpace"/>. The mounted-volume fact
/// runs only when the Phase 62 letterless NTFS mount is present on this machine.
/// </summary>
public sealed class DirectoryVolumeSpaceTests
{
    /// <summary>
    /// Phase 62 retention mount: NTFS volume without a drive letter. Absent on other machines.
    /// </summary>
    private const string Phase62CacheMount =
        @"C:\Users\chrisk\source\repos\Usenet.ninja\VectorNNTP\src\VectorNNTP.StorageServer\bin\Debug\net10.0\spool\cache";

    private readonly ITestOutputHelper _output;

    public DirectoryVolumeSpaceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Read_queries_the_configured_directory_not_its_drive_root()
    {
        var cache = @"C:\app\spool\cache";
        var space = new RecordingSpace(total: 5_000_000_000_000, available: 4_900_000_000_000, identity: "hdd", mount: cache + "\\");
        var reader = new CacheDirectoryCapacityReader(cache, space);

        var snapshot = reader.Read();

        Assert.Equal(cache, space.QueriedPath);
        Assert.NotEqual(Path.GetPathRoot(cache), space.QueriedPath);
        Assert.Equal(5_000_000_000_000, snapshot.TotalBytes);
        Assert.Equal(4_900_000_000_000, snapshot.AvailableBytes);
        Assert.Equal(100_000_000_000, snapshot.UsedBytes);
    }

    [Fact]
    public void Injected_volumes_keep_capacity_and_pressure_on_the_measured_filesystem()
    {
        var retention = new CacheDirectoryCapacityReader(
            @"C:\app\spool\cache",
            new RecordingSpace(1_000, 100, "hdd", @"C:\app\spool\cache\"));
        var control = new CacheDirectoryCapacityReader(
            @"C:\app\control",
            new RecordingSpace(4_000, 2_800, "nvme", @"C:\"));

        var retentionSnapshot = retention.Read();
        var controlSnapshot = control.Read();
        var policy = BulkStoragePressurePolicy.Default;
        var retentionPressure = policy.Evaluate(
            retentionSnapshot.TotalBytes,
            retentionSnapshot.UsedBytes,
            retentionSnapshot.AvailableBytes);
        var controlPressure = policy.Evaluate(
            controlSnapshot.TotalBytes,
            controlSnapshot.UsedBytes,
            controlSnapshot.AvailableBytes);

        Assert.Equal(1_000, retentionSnapshot.TotalBytes);
        Assert.Equal(900, retentionSnapshot.UsedBytes);
        Assert.Equal(100, retentionSnapshot.AvailableBytes);
        Assert.Equal(90, retentionPressure.UsedPercent);
        Assert.Equal(BulkStoragePressureState.Critical, retentionPressure.State);
        Assert.True(retentionPressure.Measured);
        Assert.Equal(retentionSnapshot.TotalBytes, retentionPressure.TotalBytes);
        Assert.Equal(retentionSnapshot.AvailableBytes, retentionPressure.FreeBytes);
        Assert.Equal(30, controlPressure.UsedPercent);
        Assert.Equal(BulkStoragePressureState.Normal, controlPressure.State);
        Assert.NotEqual(controlPressure.TotalBytes, retentionPressure.TotalBytes);
    }

    [Fact]
    public void Unresolved_directory_throws_and_does_not_consult_another_volume()
    {
        var cache = @"C:\app\spool\cache";
        var control = new RecordingSpace(4_000_000_000_000, 2_800_000_000_000, "nvme", @"C:\");
        var retention = new RecordingSpace(succeed: false);
        var reader = new CacheDirectoryCapacityReader(cache, retention);

        var error = Assert.Throws<InvalidOperationException>(() => reader.Read());

        Assert.Contains(cache, error.Message, StringComparison.Ordinal);
        Assert.Contains("not substituted", error.Message, StringComparison.Ordinal);
        Assert.Equal(cache, retention.QueriedPath);
        Assert.Equal(0, control.CallCount);
    }

    [Fact]
    public void Non_positive_measurement_is_not_reported_as_capacity()
    {
        var reader = new CacheDirectoryCapacityReader(@"C:\app\spool\cache", new RecordingSpace(0, 0, "x", "x"));

        var error = Assert.Throws<InvalidOperationException>(() => reader.Read());

        Assert.Contains("not substituted", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Egress_sample_uses_the_measured_filesystem_or_fails_closed()
    {
        var measured = new DriveEgressVolumeSpace(
            @"C:\app\control",
            new RecordingSpace(8_000, 1_000, "nvme", @"C:\"));
        Assert.True(measured.TryRead(out var available, out var total));
        Assert.Equal(1_000, available);
        Assert.Equal(8_000, total);

        var failed = new DriveEgressVolumeSpace(@"C:\app\control", new RecordingSpace(succeed: false));
        Assert.False(failed.TryRead(out available, out total));
        Assert.Equal(0, available);
        Assert.Equal(0, total);
    }

    [Fact]
    public void Missing_directory_is_unmeasured_and_is_not_created()
    {
        var missing = Path.Combine(Path.GetTempPath(), "vectornntp-missing-volume-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(missing));

        Assert.False(OsDirectoryVolumeSpace.Shared.TryMeasure(missing, out _));
        var error = Assert.Throws<InvalidOperationException>(() => new CacheDirectoryCapacityReader(missing).Read());

        Assert.Contains(missing, error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void Invalid_path_is_unmeasured()
    {
        const string invalid = "bad\0path";

        Assert.False(OsDirectoryVolumeSpace.Shared.TryMeasure(invalid, out _));
        var error = Assert.Throws<InvalidOperationException>(() => new CacheDirectoryCapacityReader(invalid).Read());
        Assert.Contains("not substituted", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_directory_reports_its_own_filesystem()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vectornntp-volspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            {
                Assert.False(OsDirectoryVolumeSpace.Shared.TryMeasure(dir, out _));
                Assert.Throws<InvalidOperationException>(() => new CacheDirectoryCapacityReader(dir).Read());
                return;
            }

            Assert.True(OsDirectoryVolumeSpace.Shared.TryMeasure(dir, out var measured));
            var snapshot = new CacheDirectoryCapacityReader(dir).Read();
            Assert.Equal(measured.TotalBytes, snapshot.TotalBytes);
            Assert.Equal(measured.AvailableBytes, snapshot.AvailableBytes);
            Assert.Equal(Math.Max(0L, measured.TotalBytes - measured.AvailableBytes), snapshot.UsedBytes);
            Assert.True(measured.TotalBytes > 0);

            if (OperatingSystem.IsWindows())
            {
                var root = Path.GetPathRoot(Path.GetFullPath(dir))!;
                Assert.Equal(new DriveInfo(root).TotalSize, measured.TotalBytes);
                Assert.Equal(root, measured.MountPoint);
                Assert.True(OsStorageVolumeProbe.Shared.TryResolve(dir, out var identity));
                Assert.Equal(identity.Value, measured.VolumeIdentity);
            }
            else
            {
                Assert.Equal(new DriveInfo(dir).TotalSize, measured.TotalBytes);
                Assert.True(OsStorageVolumeProbe.Shared.TryResolve(dir, out var identity));
                Assert.Equal(identity.Value, measured.VolumeIdentity);
            }
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public void Windows_directory_mount_is_not_accounted_as_the_drive_letter_root()
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(Phase62CacheMount))
        {
            _output.WriteLine("Phase 62 letterless retention mount is not present. Mounted-volume assertions were not executed.");
            return;
        }

        var cache = Path.GetFullPath(Phase62CacheMount);
        var letterRoot = Path.GetPathRoot(cache)!;
        var letter = new DriveInfo(letterRoot);
        Assert.True(OsDirectoryVolumeSpace.Shared.TryMeasure(cache, out var measured));
        var snapshot = new CacheDirectoryCapacityReader(cache).Read();
        var pressure = BulkStoragePressurePolicy.Default.Evaluate(
            snapshot.TotalBytes,
            snapshot.UsedBytes,
            snapshot.AvailableBytes);

        var controlDir = Path.Combine(Path.GetTempPath(), "vectornntp-control-volume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(controlDir);
        try
        {
            Assert.True(OsDirectoryVolumeSpace.Shared.TryMeasure(controlDir, out var control));
            Assert.NotEqual(letter.TotalSize, measured.TotalBytes);
            Assert.NotEqual(letterRoot, measured.MountPoint);
            Assert.EndsWith(@"\spool\cache\", measured.MountPoint, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(@"\\?\Volume{", measured.VolumeIdentity, StringComparison.Ordinal);
            Assert.NotEqual(control.VolumeIdentity, measured.VolumeIdentity);
            Assert.Equal(letter.TotalSize, control.TotalBytes);
            Assert.Equal(measured.TotalBytes, snapshot.TotalBytes);
            Assert.Equal(measured.AvailableBytes, snapshot.AvailableBytes);
            Assert.Equal(Math.Max(0L, snapshot.TotalBytes - snapshot.AvailableBytes), snapshot.UsedBytes);
            Assert.True(pressure.Measured);
            Assert.Equal(snapshot.TotalBytes, pressure.TotalBytes);
            Assert.Equal(snapshot.AvailableBytes, pressure.FreeBytes);
            Assert.Equal(snapshot.UsedBytes, pressure.UsedBytes);
            _output.WriteLine(
                $"cacheMount={measured.MountPoint} cacheVolume={measured.VolumeIdentity} cacheTotal={measured.TotalBytes} cacheAvailable={measured.AvailableBytes} cacheUsedPercent={pressure.UsedPercent}");
            _output.WriteLine(
                $"letterRoot={letterRoot} letterTotal={letter.TotalSize} controlMount={control.MountPoint} controlVolume={control.VolumeIdentity} controlTotal={control.TotalBytes}");
        }
        finally
        {
            Directory.Delete(controlDir);
        }
    }

    private sealed class RecordingSpace : IDirectoryVolumeSpace
    {
        private readonly bool _succeed;
        private readonly long _total;
        private readonly long _available;
        private readonly string _identity;
        private readonly string _mount;

        public RecordingSpace(long total, long available, string identity, string mount)
            : this(true, total, available, identity, mount)
        {
        }

        public RecordingSpace(bool succeed)
            : this(succeed, 0, 0, "", "")
        {
        }

        private RecordingSpace(bool succeed, long total, long available, string identity, string mount)
        {
            _succeed = succeed;
            _total = total;
            _available = available;
            _identity = identity;
            _mount = mount;
        }

        public string? QueriedPath { get; private set; }

        public int CallCount { get; private set; }

        public bool TryMeasure(string directoryPath, out DirectoryVolumeSpaceMeasurement measurement)
        {
            CallCount++;
            QueriedPath = directoryPath;
            if (!_succeed)
            {
                measurement = default;
                return false;
            }

            measurement = new DirectoryVolumeSpaceMeasurement(_total, _available, _identity, _mount);
            return true;
        }
    }
}
