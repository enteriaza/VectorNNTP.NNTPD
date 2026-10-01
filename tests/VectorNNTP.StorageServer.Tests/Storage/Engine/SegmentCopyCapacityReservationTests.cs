using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using Serilog.Extensions.Logging;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;
using VectorNNTP.StorageServer.Tests.Logging;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Segment-copy reservations stay on the segment ledger after PhysicalWritten.
/// Each physical append reserves ArtSize+56 before the write. Adoption does not.
/// </summary>
public sealed class SegmentCopyCapacityReservationTests
{
    [Fact]
    public async Task PhysicalWritten_keeps_the_segment_copy_reservation()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = Open(dir, capacity);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<seg-keep@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(1, engine.ProcessLocalSegmentCopyCount);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(1, engine.PhysicalAppendCount);
    }

    [Fact]
    public async Task ExactFit_stays_full_after_PhysicalWritten()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<seg-fit@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var occupied = required
            + ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize)
            + ArticleIndexRecordCodec.RecordLength;
        const long total = 10_000;
        const int util = 80;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, util);
        var capacity = new MutableCapacityReader(total, used: ceiling - occupied);
        await using var engine = Open(dir, capacity, util);

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);

        var second = await engine.AcceptAsync(CreateRecord("<seg-fit-2@seg.test>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, second.Outcome);
        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(1, engine.ProcessLocalSegmentCopyCount);
    }

    [Fact]
    public async Task Duplicate_and_conflict_do_not_add_a_reservation()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = Open(dir, capacity);
        var first = CreateRecord("<seg-dup@seg.test>", "a\r\n");
        var required = SegmentRecordCodec.RecordLengthForArtSize(first.ArtSize);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(first, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Equal(ArticleAcceptOutcome.Duplicate, (await engine.AcceptAsync(first, CancellationToken.None)).Outcome);
        var conflict = await engine.AcceptAsync(CreateRecord("<seg-dup@seg.test>", "b\r\n"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Conflict, conflict.Outcome);
        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(1, engine.ProcessLocalSegmentCopyCount);
        Assert.Equal(1, engine.PhysicalAppendCount);
    }

    [Fact]
    public async Task Journal_reject_rolls_back_because_no_segment_was_written()
    {
        var probe = CreateRecord("<seg-jp-probe@seg.test>", "x\r\n");
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options) with
            {
                JournalSoftLimitBytes = Math.Max(1, probe.ArtSize / 2),
                JournalHardLimitBytes = probe.ArtSize,
            },
            capacityReader: capacity);
        engine.SuspendBackgroundPersist = true;
        var probeRequired = SegmentRecordCodec.RecordLengthForArtSize(probe.ArtSize);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(probe, CancellationToken.None)).Outcome);

        var rejected = await engine.AcceptAsync(CreateRecord("<seg-jp-reject@seg.test>", "y\r\n"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedPressure, rejected.Outcome);
        Assert.Equal(probeRequired, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(1, engine.ProcessLocalReservationCount);
        Assert.Equal(0, engine.PhysicalAppendCount);
    }

    [Fact]
    public async Task Retryable_failure_after_write_keeps_that_copy_and_adopt_does_not_add_another()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = Open(dir, capacity);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<seg-retry-keep@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        _ = await engine.AcceptAsync(record, CancellationToken.None);

        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.AfterSataAppend;
        _ = await Assert.ThrowsAsync<IOException>(() => engine.RecoverAsync(CancellationToken.None));
        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(1, engine.PhysicalAppendCount);

        await engine.RecoverAsync(CancellationToken.None);
        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(1, engine.ProcessLocalSegmentCopyCount);
        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Seal_retry_second_append_reserves_a_second_copy()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = Open(dir, capacity);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<seg-seal-2@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        SealNextAppends(engine, count: 1);

        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(2, engine.PhysicalAppendCount);
        Assert.Equal(2, engine.ProcessLocalSegmentCopyCount);
        Assert.Equal(required * 2, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(1, engine.ProcessLocalReservationCount);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task NonRetryable_before_write_releases_the_attempt()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        var sink = new CollectingSink();
        await using var engine = Open(dir, capacity, logger: CreateLogger(sink));
        engine.TestPersistRetryDelay = TimeSpan.FromHours(1);
        engine.TestPersistFaultExceptionKind = FileArticleStorageEngine.PersistFaultExceptionKind.InvalidOperation;
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;
        var record = CreateRecord("<seg-nr-before@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        _ = await engine.AcceptAsync(record, CancellationToken.None);

        await WaitUntilAsync(() => engine.PersistBlockedRetryScheduledCount >= 1);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, engine.PhysicalAppendCount);
        Assert.False(engine.TryRead(record.ArtId, out _));
        var failure = AssertNonRetryableReservationEvent(sink);
        Assert.Equal(required.ToString(CultureInfo.InvariantCulture), Scalar(failure, "ReleasedUnwrittenSegmentBytes"));
        Assert.Equal("0", Scalar(failure, "RetainedWrittenSegmentBytes"));
        Assert.Equal(ArticleIndexRecordCodec.RecordLength.ToString(CultureInfo.InvariantCulture), Scalar(failure, "ReleasedUnboundIndexBytes"));
        Assert.Equal(engine.ProcessLocalJournalReservedBytes.ToString(CultureInfo.InvariantCulture), Scalar(failure, "RetainedJournalBytes"));
        Assert.True(engine.ProcessLocalJournalReservedBytes > 0);
        AssertBlockedRetry(sink, Scalar(failure, "Sequence"));
    }

    [Fact]
    public async Task NonRetryable_after_write_keeps_that_copy()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        var sink = new CollectingSink();
        await using var engine = Open(dir, capacity, logger: CreateLogger(sink));
        engine.TestPersistRetryDelay = TimeSpan.FromHours(1);
        engine.TestPersistFaultExceptionKind = FileArticleStorageEngine.PersistFaultExceptionKind.InvalidOperation;
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.AfterSataAppend;
        var record = CreateRecord("<seg-nr-after@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        _ = await engine.AcceptAsync(record, CancellationToken.None);

        await WaitUntilAsync(() => engine.PersistBlockedRetryScheduledCount >= 1);
        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(1, engine.ProcessLocalSegmentCopyCount);
        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.False(engine.TryRead(record.ArtId, out _));
        var failure = AssertNonRetryableReservationEvent(sink);
        Assert.Equal("0", Scalar(failure, "ReleasedUnwrittenSegmentBytes"));
        Assert.Equal(required.ToString(CultureInfo.InvariantCulture), Scalar(failure, "RetainedWrittenSegmentBytes"));
        Assert.Equal(engine.ProcessLocalJournalReservedBytes.ToString(CultureInfo.InvariantCulture), Scalar(failure, "RetainedJournalBytes"));
        Assert.True(engine.ProcessLocalJournalReservedBytes > 0);
        AssertBlockedRetry(sink, Scalar(failure, "Sequence"));
    }

    [Fact]
    public async Task Every_sealed_append_has_its_own_reservation_until_one_registers()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = Open(dir, capacity);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<seg-seal-n@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        SealNextAppends(engine, count: 2);

        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(3, engine.PhysicalAppendCount);
        Assert.Equal(3, engine.ProcessLocalSegmentCopyCount);
        Assert.Equal(required * 3, engine.ProcessLocalArticleReservedBytes);
    }

    [Fact]
    public async Task Capacity_rejection_prevents_the_next_sealed_append()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<seg-seal-deny@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var occupied = required
            + ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize)
            + ArticleIndexRecordCodec.RecordLength;
        const long total = 10_000;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, 80);
        var capacity = new MutableCapacityReader(total, used: ceiling - occupied);
        await using var engine = Open(dir, capacity, 80);
        engine.SuspendBackgroundPersist = true;
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        SealNextAppends(engine, count: int.MaxValue);

        var denied = await Assert.ThrowsAsync<PersistCompletionDeferredException>(
            () => engine.RecoverAsync(CancellationToken.None));
        Assert.Contains("cannot reserve", denied.Message, StringComparison.Ordinal);
        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.Equal(1, engine.ProcessLocalSegmentCopyCount);
        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);
        Assert.False(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task Seal_retry_stops_at_the_existing_maximum_and_keeps_every_copy()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = Open(dir, capacity);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<seg-seal-max@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        SealNextAppends(engine, count: int.MaxValue);

        var failed = await Assert.ThrowsAsync<IOException>(() => engine.RecoverAsync(CancellationToken.None));
        Assert.Contains("could not register", failed.Message, StringComparison.Ordinal);
        Assert.Equal(FileArticleStorageEngine.MaxPrePhysicalWrittenAppendAttempts, engine.PhysicalAppendCount);
        Assert.Equal(FileArticleStorageEngine.MaxPrePhysicalWrittenAppendAttempts, engine.ProcessLocalSegmentCopyCount);
        Assert.Equal(
            required * FileArticleStorageEngine.MaxPrePhysicalWrittenAppendAttempts,
            engine.ProcessLocalArticleReservedBytes);
    }

    [Fact]
    public async Task Adoption_does_not_reserve_another_copy()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        var record = CreateRecord("<seg-adopt@seg.test>");
        StoredArticleLocation prior;
        await using (var engineA = Open(dir, capacity))
        {
            engineA.SuspendBackgroundPersist = true;
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
            var appender = await engineA.Segments.GetActiveAppenderAsync(CancellationToken.None);
            prior = await appender.AppendAsync(record.ArtData, CancellationToken.None);
        }

        await using var engine = Open(dir, capacity);
        engine.SuspendBackgroundPersist = true;
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(0, engine.PhysicalAppendCount);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, engine.ProcessLocalSegmentCopyCount);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.Equal(prior.SegmentId.Value, read.Metadata.Location.SegmentId.Value);
        Assert.Equal(prior.Offset, read.Metadata.Location.Offset);
    }

    [Fact]
    public async Task Restart_recovery_append_reserves_one_segment_copy_before_write()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        var record = CreateRecord("<seg-recover@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        await using (var engineA = Open(dir, capacity))
        {
            engineA.SuspendBackgroundPersist = true;
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
        }

        await using var engineB = Open(dir, capacity);
        Assert.Equal(0, engineB.ProcessLocalArticleReservedBytes);
        engineB.SuspendBackgroundPersist = true;
        await engineB.RecoverAsync(CancellationToken.None);

        Assert.Equal(1, engineB.PhysicalAppendCount);
        Assert.Equal(required, engineB.ProcessLocalArticleReservedBytes);
        Assert.Equal(1, engineB.ProcessLocalSegmentCopyCount);
    }

    [Fact]
    public async Task Split_volume_segment_copy_does_not_touch_the_control_ledger()
    {
        using var dir = TempStorageDir.Create();
        var segment = new MutableCapacityReader(total: 10_000_000, used: 0);
        var control = new MutableCapacityReader(total: 10_000_000, used: 0);
        var probe = ScriptedVolumeProbe.Split(dir);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            volumeProbe: probe,
            capacityReader: segment,
            controlCapacityReader: control);
        var record = CreateRecord("<seg-split@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);

        long controlCheckpoint = 0;
        long segmentCheckpoint = 0;
        long segmentArticle = 0;
        long controlArticle = 0;
        engine.Journal.CheckpointTestFault = point =>
        {
            if (point != FileArticleJournal.CheckpointFaultPoint.AfterTempFlushed)
            {
                return;
            }

            segmentArticle = engine.ProcessLocalArticleReservedBytes;
            controlArticle = engine.ControlCapacity!.WithLedger(static ledger => ledger.ArticleReservedBytes);
            segmentCheckpoint = engine.SegmentCapacity!.WithLedger(static ledger => ledger.CheckpointReservedBytes);
            controlCheckpoint = engine.ProcessLocalCheckpointReservedBytes;
        };

        Assert.True(engine.CheckpointTruncateCommitted() > 0);
        Assert.NotSame(engine.SegmentCapacity, engine.ControlCapacity);
        Assert.Equal(required, segmentArticle);
        Assert.Equal(0, controlArticle);
        Assert.Equal(0, segmentCheckpoint);
        Assert.True(controlCheckpoint > 0);
        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, engine.ControlCapacity!.WithLedger(static ledger => ledger.ArticleReservedBytes));
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
    }

    [Fact]
    public async Task Capacity_always_on_full_volume_rejects_segment_copy()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 100, used: 100);
        await using var engine = FileArticleStorageEngine.Open(
            dir.Options,
            capacityReader: capacity);
        var record = CreateRecord("<seg-off@seg.test>");
        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, accepted.Outcome);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, engine.ProcessLocalSegmentCopyCount);
        Assert.NotNull(engine.SegmentCapacity);
        Assert.NotNull(engine.ControlCapacity);
        Assert.False(engine.TryRead(record.ArtId, out _));
    }

    private static void SealNextAppends(FileArticleStorageEngine engine, int count)
    {
        const ulong sealedId = 9_001;
        engine.TestSealSegmentForPrePhysicalWritten(sealedId);
        var forced = 0;
        engine.TestRewriteSealedPhysicalLocation = location =>
        {
            if (forced >= count)
            {
                return null;
            }

            forced++;
            return new StoredArticleLocation(new SegmentId(sealedId), location.Offset, location.Length);
        };
    }

    private static FileArticleStorageEngine Open(
        TempStorageDir dir,
        MutableCapacityReader capacity,
        int maximumUtilization = ArticleCapacityOptions.DefaultMaximumUtilization,
        ILogger? logger = null) =>
        FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization),
            logger,
            capacityReader: capacity);

    private static ILogger CreateLogger(CollectingSink sink) =>
        new SerilogLoggerFactory(
            new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger(),
            dispose: true).CreateLogger<FileArticleStorageEngine>();

    private static LogEvent AssertNonRetryableReservationEvent(CollectingSink sink)
    {
        var failure = Assert.Single(sink.Events, static e => IsEvent(e, 3419));
        var text = failure.RenderMessage(CultureInfo.InvariantCulture);
        Assert.Contains("unwritten segment-copy", text, StringComparison.Ordinal);
        Assert.Contains("remain held", text, StringComparison.Ordinal);
        Assert.DoesNotContain("reservation released", text, StringComparison.Ordinal);
        return failure;
    }

    private static void AssertBlockedRetry(CollectingSink sink, string sequence)
    {
        var blocked = Assert.Single(sink.Events, static e => IsEvent(e, 3420));
        Assert.Equal(sequence, Scalar(blocked, "Sequence"));
    }

    private static bool IsEvent(LogEvent logEvent, int eventId) =>
        logEvent.Properties.TryGetValue("EventId", out var value)
        && value.ToString().Contains(eventId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static string Scalar(LogEvent logEvent, string name)
    {
        var property = Assert.Contains(name, logEvent.Properties);
        return property.ToString().Trim('"');
    }

    private static ArticleStorageRuntimeOptions WithCapacity(
        ArticleStorageRuntimeOptions options,
        int maximumUtilization = ArticleCapacityOptions.DefaultMaximumUtilization) =>
        options with
        {
            CapacityMaximumUtilization = maximumUtilization,
        };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met.");
            }

            await Task.Delay(10);
        }
    }

    private static ArticleRecord CreateRecord(string messageId, string body = "line1\r\nline2\r\n")
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: segment-copy\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class ScriptedVolumeProbe : IStorageVolumeProbe
    {
        private readonly string _segmentDir;
        private readonly string _controlDir;

        private ScriptedVolumeProbe(string segmentDir, string controlDir)
        {
            _segmentDir = Path.GetFullPath(segmentDir);
            _controlDir = Path.GetFullPath(controlDir);
        }

        public static ScriptedVolumeProbe Split(TempStorageDir dir) =>
            new(dir.Options.SegmentDir, dir.Options.ControlDir);

        public bool TryResolve(string directoryPath, out StorageVolumeIdentity identity)
        {
            var full = Path.GetFullPath(directoryPath);
            if (string.Equals(full, _segmentDir, StringComparison.OrdinalIgnoreCase))
            {
                identity = new StorageVolumeIdentity("volume-segment");
                return true;
            }

            if (string.Equals(full, _controlDir, StringComparison.OrdinalIgnoreCase))
            {
                identity = new StorageVolumeIdentity("volume-control");
                return true;
            }

            identity = default;
            return false;
        }
    }

    private sealed class MutableCapacityReader : IStorageCapacityReader
    {
        public MutableCapacityReader(long total, long used)
        {
            TotalBytes = total;
            UsedBytes = used;
        }

        public long TotalBytes { get; set; }

        public long UsedBytes { get; set; }

        public StorageCapacitySnapshot Read() =>
            new(TotalBytes, UsedBytes, Math.Max(0L, TotalBytes - UsedBytes));
    }

    private sealed class TempStorageDir : IDisposable
    {
        private TempStorageDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public string Root { get; }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempStorageDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-segcopy-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(cache);
            return new TempStorageDir(
                root,
                new ArticleStorageRuntimeOptions(
                    ControlDir: control,
                    SegmentDir: cache,
                    JournalSoftLimitBytes: ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                    JournalHardLimitBytes: ArticleStorageOptions.DefaultJournalHardLimitBytes,
                    SegmentTargetSizeBytes: ArticleStorageOptions.DefaultSegmentTargetSizeBytes));
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
