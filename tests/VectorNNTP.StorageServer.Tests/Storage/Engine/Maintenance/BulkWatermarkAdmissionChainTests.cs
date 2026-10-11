using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;
using VectorNNTP.StorageServer.Storage.Engine.Policy;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine.Maintenance;

/// <summary>
/// Retention-volume bytes from <see cref="CacheDirectoryCapacityReader"/> drive watermark
/// classification and Accept. A separate control-volume reader must not be substituted.
/// </summary>
/// <remarks>
/// Shipped <c>VectorNNTP.StorageServer.json</c> sets MaximumUtilization to 70 and
/// MaximumUsageCapacity to 50. Watermark defaults are 75/80/85/90/95. Classification uses
/// <see cref="BulkStoragePressurePolicy.AtOrAbove"/> (<c>used * 100 &gt;= total * percent</c>).
/// <see cref="BulkStoragePressureEvaluation.UsedPercent"/> truncates. Warning and Pressure do
/// not apply the recovery-reserve floor. The utilization ceiling is a separate guard and is
/// checked after that floor. Watermark class itself has no hysteresis. Pressure-expiration
/// winding-down stops only once used percent is below Pressure minus the recovery margin (75).
/// </remarks>
public sealed class BulkWatermarkAdmissionChainTests
{
    private const long RetentionTotal = 10_000_000;
    private const long ControlTotal = 4_000_000_000;
    private const long ControlUsed = 1_200_000_000;

    [Theory]
    [InlineData(1_000_000, BulkStoragePressureState.Normal, ArticleAcceptOutcome.Accepted, null)]
    [InlineData(7_000_000, BulkStoragePressureState.Normal, ArticleAcceptOutcome.RejectedCapacity, "storage-capacity")]
    [InlineData(7_499_999, BulkStoragePressureState.Normal, ArticleAcceptOutcome.RejectedCapacity, "storage-capacity")]
    [InlineData(7_500_000, BulkStoragePressureState.Warning, ArticleAcceptOutcome.RejectedCapacity, "storage-capacity")]
    [InlineData(8_000_000, BulkStoragePressureState.Pressure, ArticleAcceptOutcome.RejectedCapacity, "storage-capacity")]
    [InlineData(8_500_000, BulkStoragePressureState.High, ArticleAcceptOutcome.RejectedCapacity, "storage-capacity")]
    [InlineData(9_000_000, BulkStoragePressureState.Critical, ArticleAcceptOutcome.RejectedCapacity, "storage-capacity")]
    [InlineData(9_500_000, BulkStoragePressureState.Emergency, ArticleAcceptOutcome.RejectedCapacity, "bulk-recovery-reserve")]
    public async Task Retention_measurement_selects_watermark_and_admission(
        long used,
        BulkStoragePressureState expectedState,
        ArticleAcceptOutcome expectedOutcome,
        string? expectedReason)
    {
        using var dir = TempDir.Create();
        var retention = new MutableSpace(RetentionTotal, RetentionTotal - used);
        var control = new CountingReader(ControlTotal, ControlUsed);
        await using var engine = Open(dir, retention, control);
        var controlReadsAfterOpen = control.Calls;
        var article = Article("<watermark-chain@example>", bodyBytes: 32);

        var result = await engine.AcceptAsync(article, CancellationToken.None);
        var observed = engine.ObserveCapacityAdmissionPressure();
        var evaluation = BulkStoragePressurePolicy.Default.Evaluate(
            observed.TotalBytes,
            observed.UsedBytes,
            observed.AvailableBytes);

        Assert.Equal(dir.SegmentDir, retention.LastPath);
        Assert.NotEqual(Path.GetPathRoot(dir.SegmentDir), retention.LastPath);
        Assert.Equal(RetentionTotal, observed.TotalBytes);
        Assert.Equal(RetentionTotal - used, observed.AvailableBytes);
        Assert.Equal(used, observed.UsedBytes);
        Assert.NotEqual(ControlTotal, observed.TotalBytes);
        Assert.Equal(expectedState, evaluation.State);
        Assert.True(evaluation.Measured);
        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedReason, result.Reason);
        if (expectedOutcome == ArticleAcceptOutcome.Accepted)
        {
            Assert.True(control.Calls > controlReadsAfterOpen);
        }
        else
        {
            Assert.Equal(controlReadsAfterOpen, control.Calls);
        }

        Assert.Equal(expectedOutcome == ArticleAcceptOutcome.Accepted, engine.TryRead(article.ArtId, out _));
    }

    [Fact]
    public async Task Critical_large_copy_is_rejected_by_the_retention_reserve_not_the_control_volume()
    {
        using var dir = TempDir.Create();
        const long used = 9_000_000;
        var retention = new MutableSpace(RetentionTotal, RetentionTotal - used);
        var control = new CountingReader(ControlTotal, ControlUsed);
        await using var engine = Open(dir, retention, control);
        var article = Article("<critical-reserve@example>", bodyBytes: 600_000);
        var copy = SegmentRecordCodec.RecordLengthForArtSize(article.ArtSize);
        var reserve = BulkStoragePressurePolicy.ReserveBytes(RetentionTotal, 5);
        Assert.True(RetentionTotal - used - copy < reserve);
        var controlReadsAfterOpen = control.Calls;

        var result = await engine.AcceptAsync(article, CancellationToken.None);
        var observed = engine.ObserveCapacityAdmissionPressure();

        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, result.Outcome);
        Assert.Equal(BulkStoragePressurePolicy.RecoveryReserveRejectionReason, result.Reason);
        Assert.Equal(RetentionTotal, observed.TotalBytes);
        Assert.Equal(BulkStoragePressureState.Critical, BulkStoragePressurePolicy.Default.Evaluate(
            observed.TotalBytes,
            observed.UsedBytes,
            observed.AvailableBytes).State);
        Assert.Equal(controlReadsAfterOpen, control.Calls);
        Assert.False(engine.TryRead(article.ArtId, out _));
    }

    [Fact]
    public async Task Elevated_retention_measurement_recovers_when_the_next_sample_is_below_the_expiration_target()
    {
        using var dir = TempDir.Create();
        var retention = new MutableSpace(RetentionTotal, 500_000);
        var control = new CountingReader(ControlTotal, ControlUsed);
        await using var engine = Open(dir, retention, control);
        var blocked = Article("<recover-blocked@example>", bodyBytes: 32);
        var blockedResult = await engine.AcceptAsync(blocked, CancellationToken.None);
        Assert.Equal(BulkStoragePressurePolicy.RecoveryReserveRejectionReason, blockedResult.Reason);

        var options = BulkStoragePressurePolicy.Default.Options;
        var emergency = BulkStoragePressurePolicy.Default.Evaluate(RetentionTotal, 9_500_000, 500_000);
        var atWarning = BulkStoragePressurePolicy.Default.Evaluate(RetentionTotal, 7_500_000, 2_500_000);
        var belowTarget = BulkStoragePressurePolicy.Default.Evaluate(RetentionTotal, 7_499_999, 2_500_001);
        Assert.Equal(95, emergency.UsedPercent);
        Assert.Equal(PressureExpirationMode.Active, ArticlePressureRetentionPolicy.Mode(emergency, alreadyLatched: false, options));
        Assert.Equal(PressureExpirationMode.WindingDown, ArticlePressureRetentionPolicy.Mode(atWarning, alreadyLatched: true, options));
        Assert.Equal(74, belowTarget.UsedPercent);
        Assert.Equal(PressureExpirationMode.None, ArticlePressureRetentionPolicy.Mode(belowTarget, alreadyLatched: true, options));

        retention.Available = 9_000_000;
        var allowed = Article("<recover-allowed@example>", bodyBytes: 32);
        var allowedResult = await engine.AcceptAsync(allowed, CancellationToken.None);
        var observed = engine.ObserveCapacityAdmissionPressure();
        var recovered = BulkStoragePressurePolicy.Default.Evaluate(
            observed.TotalBytes,
            observed.UsedBytes,
            observed.AvailableBytes);

        Assert.Equal(ArticleAcceptOutcome.Accepted, allowedResult.Outcome);
        Assert.Equal(BulkStoragePressureState.Normal, recovered.State);
        Assert.Equal(10, recovered.UsedPercent);
        Assert.Equal(PressureExpirationMode.None, ArticlePressureRetentionPolicy.Mode(recovered, alreadyLatched: true, options));
        Assert.Equal(RetentionTotal, observed.TotalBytes);
        Assert.NotEqual(ControlTotal, observed.TotalBytes);
        Assert.False(engine.TryRead(blocked.ArtId, out _));
        Assert.True(engine.TryRead(allowed.ArtId, out _));
    }

    [Fact]
    public async Task Unmeasured_retention_volume_rejects_without_reading_the_control_volume_again()
    {
        using var dir = TempDir.Create();
        var retention = new MutableSpace(RetentionTotal, 9_000_000);
        var control = new CountingReader(ControlTotal, ControlUsed);
        await using var engine = Open(dir, retention, control);
        var kept = Article("<unmeasured-kept@example>", bodyBytes: 32);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(kept, CancellationToken.None)).Outcome);
        var controlReadsAfterOpen = control.Calls;
        retention.Succeed = false;
        var blocked = Article("<unmeasured-blocked@example>", bodyBytes: 32);

        var result = await engine.AcceptAsync(blocked, CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, result.Outcome);
        Assert.Equal(BulkStoragePressurePolicy.UnmeasuredRejectionReason, result.Reason);
        Assert.Equal(controlReadsAfterOpen, control.Calls);
        Assert.True(engine.TryRead(kept.ArtId, out _));
        Assert.False(engine.TryRead(blocked.ArtId, out _));
    }

    [Fact]
    public async Task Journal_hard_limit_rejects_while_the_retention_volume_is_normal()
    {
        using var dir = TempDir.Create(journalHardLimitBytes: 64);
        var retention = new MutableSpace(RetentionTotal, 9_000_000);
        var control = new CountingReader(ControlTotal, ControlUsed);
        await using var engine = Open(dir, retention, control);
        var first = Article("<journal-first@example>", bodyBytes: 32);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(first, CancellationToken.None)).Outcome);
        var second = Article("<journal-second@example>", bodyBytes: 32);

        var result = await engine.AcceptAsync(second, CancellationToken.None);
        var observed = engine.ObserveCapacityAdmissionPressure();

        Assert.Equal(StorageWritePressure.Critical, engine.GetWritePressure());
        Assert.Equal(ArticleAcceptOutcome.RejectedPressure, result.Outcome);
        Assert.Equal("journal-pressure", result.Reason);
        Assert.Equal(BulkStoragePressureState.Normal, BulkStoragePressurePolicy.Default.Evaluate(
            observed.TotalBytes,
            observed.UsedBytes,
            observed.AvailableBytes).State);
        Assert.Equal(RetentionTotal, observed.TotalBytes);
        Assert.True(engine.TryRead(first.ArtId, out _));
        Assert.False(engine.TryRead(second.ArtId, out _));
    }

    private static FileArticleStorageEngine Open(TempDir dir, MutableSpace retention, CountingReader control)
    {
        var reader = new CacheDirectoryCapacityReader(dir.SegmentDir, retention);
        var engine = FileArticleStorageEngine.Open(
            dir.Options with
            {
                CapacityMaximumUtilization = 70,
                CapacityCompactionHeadroom = 10,
                CapacityMaximumUsageCapacity = 50,
                CapacityFreeCapacity = 5,
            },
            volumeProbe: new SplitVolumeProbe(),
            capacityReader: reader,
            controlCapacityReader: control,
            logger: NullLogger.Instance,
            timeProvider: TimeProvider.System);
        _ = new StorageMaintenanceCoordinator(
            engine,
            new ArticleSegmentPolicy(new ArticleCompactionPolicyOptions()),
            logger: NullLogger.Instance);
        return engine;
    }

    private static ArticleRecord Article(string messageId, int bodyBytes)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: phase65\r\n\r\n");
        var remaining = bodyBytes;
        while (remaining > 0)
        {
            var take = Math.Min(900, remaining);
            _ = builder.Append('x', take).Append("\r\n");
            remaining -= take;
        }

        var created = ArticleRecordFactory.TryCreate(
            new NntpArticleParser("cache01.usenet.ninja"),
            Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class SplitVolumeProbe : IStorageVolumeProbe
    {
        public bool TryResolve(string directoryPath, out StorageVolumeIdentity identity)
        {
            identity = new StorageVolumeIdentity(directoryPath);
            return true;
        }
    }

    private sealed class CountingReader : IStorageCapacityReader
    {
        private readonly StorageCapacitySnapshot _snapshot;

        public CountingReader(long total, long used)
        {
            _snapshot = new StorageCapacitySnapshot(total, used, total - used);
        }

        public int Calls { get; private set; }

        public StorageCapacitySnapshot Read()
        {
            Calls++;
            return _snapshot;
        }
    }

    private sealed class MutableSpace : IDirectoryVolumeSpace
    {
        public MutableSpace(long total, long available)
        {
            Total = total;
            Available = available;
        }

        public long Total { get; }

        public long Available { get; set; }

        public bool Succeed { get; set; } = true;

        public string? LastPath { get; private set; }

        public bool TryMeasure(string directoryPath, out DirectoryVolumeSpaceMeasurement measurement)
        {
            LastPath = directoryPath;
            if (!Succeed)
            {
                measurement = default;
                return false;
            }

            measurement = new DirectoryVolumeSpaceMeasurement(Total, Available, "retention-volume", directoryPath);
            return true;
        }
    }

    private sealed class TempDir : IDisposable
    {
        private TempDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public ArticleStorageRuntimeOptions Options { get; }

        public string SegmentDir => Options.SegmentDir;

        private string Root { get; }

        public static TempDir Create(long journalHardLimitBytes = 0)
        {
            var hard = journalHardLimitBytes > 0
                ? journalHardLimitBytes
                : ArticleStorageOptions.DefaultJournalHardLimitBytes;
            var soft = journalHardLimitBytes > 0
                ? Math.Min(ArticleStorageOptions.DefaultJournalSoftLimitBytes, hard / 2)
                : ArticleStorageOptions.DefaultJournalSoftLimitBytes;
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase65-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "control"));
            Directory.CreateDirectory(Path.Combine(root, "cache"));
            return new TempDir(
                root,
                new ArticleStorageRuntimeOptions(
                    ControlDir: Path.Combine(root, "control"),
                    SegmentDir: Path.Combine(root, "cache"),
                    JournalSoftLimitBytes: soft,
                    JournalHardLimitBytes: hard,
                    SegmentTargetSizeBytes: ArticleStorageOptions.DefaultSegmentTargetSizeBytes,
                    MaxSegmentSealDelay: TimeSpan.FromHours(1)));
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
