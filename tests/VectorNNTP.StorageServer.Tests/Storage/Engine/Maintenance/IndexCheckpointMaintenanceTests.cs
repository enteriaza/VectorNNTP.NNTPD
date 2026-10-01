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
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;
using VectorNNTP.StorageServer.Storage.Engine.Policy;
using VectorNNTP.StorageServer.Tests.Logging;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine.Maintenance;

/// <summary>
/// Maintenance invokes the existing index checkpoint from physical frame history only.
/// </summary>
public sealed class IndexCheckpointMaintenanceTests
{
    [Fact]
    public async Task ThresholdZero_DoesNotCheckpoint()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await CommitAsync(engine, "<idx-ck-off@seg.test>");
        var physical = engine.Index.IndexPhysicalBytes;
        Assert.True(physical > 0);
        var sink = new CollectingSink();
        using var logs = CreateLoggerFactory(sink);

        var result = await CreateCoordinator(engine, indexThreshold: 0, logs.CreateLogger<StorageMaintenanceCoordinator>())
            .RunOnceAsync(CancellationToken.None, maintenanceRunId: 3);

        Assert.Equal(StorageMaintenanceOutcome.NoWork, result.Outcome);
        Assert.Equal(physical, engine.Index.IndexPhysicalBytes);
        Assert.False(File.Exists(SnapshotPath(dir)));
        Assert.DoesNotContain(sink.Events, static e => IsEvent(e, 3025));
    }

    [Fact]
    public async Task BelowThreshold_DoesNotCheckpoint()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await CommitAsync(engine, "<idx-ck-below@seg.test>");
        var physical = engine.Index.IndexPhysicalBytes;
        var sink = new CollectingSink();
        using var logs = CreateLoggerFactory(sink);

        var result = await CreateCoordinator(engine, physical + 1, logs.CreateLogger<StorageMaintenanceCoordinator>())
            .RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.NoWork, result.Outcome);
        Assert.Equal(physical, engine.Index.IndexPhysicalBytes);
        Assert.False(File.Exists(SnapshotPath(dir)));
        Assert.DoesNotContain(sink.Events, static e => IsEvent(e, 3025));
    }

    [Fact]
    public async Task ExactThreshold_Checkpoints()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sequence = await CommitAsync(engine, "<idx-ck-exact@seg.test>");
        var physical = engine.Index.IndexPhysicalBytes;

        var result = await CreateCoordinator(engine, physical).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.NoWork, result.Outcome);
        Assert.Equal(0, engine.Index.IndexPhysicalBytes);
        Assert.True(engine.Index.TryGet(CreateRecord("<idx-ck-exact@seg.test>").ArtId, out var row));
        Assert.Equal(ArticleStorageState.Present, row.State);
        Assert.Equal(sequence, row.Sequence);
        Assert.True(File.Exists(SnapshotPath(dir)));
    }

    [Fact]
    public async Task AboveThreshold_Checkpoints()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await CommitAsync(engine, "<idx-ck-above@seg.test>");
        Assert.True(engine.Index.IndexPhysicalBytes > 1);

        await CreateCoordinator(engine, indexThreshold: 1).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, engine.Index.IndexPhysicalBytes);
        Assert.True(File.Exists(SnapshotPath(dir)));
    }

    [Fact]
    public async Task PresentEvictedInvalidRelocated_AndSequence_Survive()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var presentId = (await CommitRowAsync(engine, "<idx-ck-present@seg.test>")).ArtId;
        var evicted = await CommitRowAsync(engine, "<idx-ck-evicted@seg.test>");
        Assert.True(engine.TryEvict(evicted.ArtId));
        var invalid = await CommitRowAsync(engine, "<idx-ck-invalid@seg.test>");
        Assert.True(engine.TryInvalidate(invalid.ArtId));
        var relocated = await CommitRowAsync(engine, "<idx-ck-reloc@seg.test>");
        var moved = new StoredArticleLocation(
            relocated.Location.SegmentId,
            relocated.Location.Offset + 8,
            relocated.Location.Length);
        Assert.Equal(
            ArticleRelocateOutcome.Relocated,
            engine.Index.TryRelocate(
                relocated.ArtId,
                relocated.Location,
                moved,
                relocated.ArtHash,
                relocated.ArtSize));
        Assert.True(engine.Index.TryGet(evicted.ArtId, out evicted));
        Assert.True(engine.Index.TryGet(invalid.ArtId, out invalid));
        Assert.True(engine.Index.TryGet(relocated.ArtId, out relocated));
        var before = engine.Index.Snapshot().ToArray();

        await CreateCoordinator(engine, engine.Index.IndexPhysicalBytes).RunOnceAsync(CancellationToken.None);

        var after = engine.Index.Snapshot();
        Assert.Equal(before.Length, after.Count);
        AssertRow(engine, presentId, ArticleStorageState.Present, before.Single(r => r.ArtId == presentId).Sequence);
        AssertRow(engine, evicted.ArtId, ArticleStorageState.Evicted, evicted.Sequence, evicted.Location);
        AssertRow(engine, invalid.ArtId, ArticleStorageState.Invalid, invalid.Sequence, invalid.Location);
        AssertRow(engine, relocated.ArtId, ArticleStorageState.Present, relocated.Sequence, moved);
        Assert.Equal(0, engine.Index.IndexPhysicalBytes);
    }

    [Fact]
    public async Task RepeatedMutations_AreRetired_AndReopenMatches()
    {
        using var dir = TempStorageDir.Create();
        StoredArticleMetadata final;
        {
            await using var engine = FileArticleStorageEngine.Open(dir.Options);
            var first = await CommitRowAsync(engine, "<idx-ck-hist@seg.test>");
            Assert.True(engine.TryEvict(first.ArtId));
            final = await CommitRowAsync(engine, "<idx-ck-hist@seg.test>");
            Assert.True(engine.Index.IndexPhysicalBytes >= ArticleIndexRecordCodec.RecordLength * 3);
            Assert.NotEqual(first.Sequence, final.Sequence);

            var sink = new CollectingSink();
            using var logs = CreateLoggerFactory(sink);
            var coordinator = CreateCoordinator(
                engine,
                engine.Index.IndexPhysicalBytes,
                logs.CreateLogger<StorageMaintenanceCoordinator>());
            var firstRun = await coordinator.RunOnceAsync(CancellationToken.None, maintenanceRunId: 9);
            Assert.Equal(StorageMaintenanceOutcome.NoWork, firstRun.Outcome);
            Assert.Equal(0, engine.Index.IndexPhysicalBytes);
            AssertDurationMsNonNegative(Assert.Single(sink.Events, static e => IsEvent(e, 3026)));

            var second = await coordinator.RunOnceAsync(CancellationToken.None, maintenanceRunId: 10);
            Assert.Equal(StorageMaintenanceOutcome.NoWork, second.Outcome);
            Assert.Equal(0, engine.Index.IndexPhysicalBytes);
            Assert.Single(sink.Events, static e => IsEvent(e, 3025));
            Assert.Equal(0, engine.CheckpointIndex());
            Assert.Equal(0, engine.Index.IndexPhysicalBytes);
        }

        await using var reopened = FileArticleStorageEngine.Open(dir.Options);
        Assert.True(reopened.Index.TryGet(final.ArtId, out var row));
        Assert.Equal(ArticleStorageState.Present, row.State);
        Assert.Equal(final.Sequence, row.Sequence);
        Assert.Equal(final.Location, row.Location);
        Assert.Equal(final.ArtHash, row.ArtHash);
        Assert.Equal(final.ArtSize, row.ArtSize);
        Assert.Equal(0, reopened.Index.IndexPhysicalBytes);
    }

    [Fact]
    public async Task MutationDuringReplacement_RemainsInTheTail()
    {
        using var dir = TempStorageDir.Create();
        StoredArticleMetadata present;
        {
            await using var engine = FileArticleStorageEngine.Open(dir.Options);
            present = await CommitRowAsync(engine, "<idx-ck-tail@seg.test>");
            engine.Index.TestDuringReplacementWrite = () => Assert.True(engine.TryEvict(present.ArtId));

            await CreateCoordinator(engine, engine.Index.IndexPhysicalBytes).RunOnceAsync(CancellationToken.None);

            Assert.True(engine.Index.TryGet(present.ArtId, out var evicted));
            Assert.Equal(ArticleStorageState.Evicted, evicted.State);
            Assert.Equal(present.Sequence, evicted.Sequence);
            Assert.Equal(ArticleIndexRecordCodec.RecordLength, engine.Index.IndexPhysicalBytes);
        }

        await using var reopened = FileArticleStorageEngine.Open(dir.Options);
        Assert.True(reopened.Index.TryGet(present.ArtId, out var row));
        Assert.Equal(ArticleStorageState.Evicted, row.State);
        Assert.Equal(present.Sequence, row.Sequence);
        Assert.Equal(ArticleIndexRecordCodec.RecordLength, reopened.Index.IndexPhysicalBytes);
    }

    [Fact]
    public async Task Capacity_ReleasesRetiredFrames_AndKeepsSnapshotReservation()
    {
        using var dir = TempStorageDir.Create();
        var reader = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = OpenWithCapacity(dir, reader);
        var row = await CommitRowAsync(engine, "<idx-ck-cap@seg.test>");
        Assert.True(engine.TryEvict(row.ArtId));
        Assert.Equal(2, engine.ProcessLocalIndexFrameCount);
        Assert.Equal(ArticleIndexRecordCodec.RecordLength * 2, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);

        await CreateCoordinator(engine, engine.Index.IndexPhysicalBytes).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, engine.ProcessLocalIndexFrameCount);
        Assert.Equal(ArticleIndexRecordCodec.RecordLength, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(ArticleIndexSnapshotCodec.EncodedLength(1), engine.ProcessLocalCheckpointReservedBytes);
        Assert.Equal(0, engine.Index.IndexPhysicalBytes);
        Assert.True(engine.Index.TryGet(row.ArtId, out var evicted));
        Assert.Equal(ArticleStorageState.Evicted, evicted.State);
        Assert.Equal(row.Sequence, evicted.Sequence);
    }

    [Fact]
    public async Task FailedCheckpoint_DoesNotReleaseFrameReservations()
    {
        using var dir = TempStorageDir.Create();
        var reader = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = OpenWithCapacity(dir, reader);
        await CommitAsync(engine, "<idx-ck-fail@seg.test>");
        var frames = engine.ProcessLocalIndexFrameCount;
        var reserved = engine.ProcessLocalIndexReservedBytes;
        var physical = engine.Index.IndexPhysicalBytes;
        engine.Index.TestBeforeSnapshotFlush = () => throw new IOException("index-checkpoint-failed");
        var sink = new CollectingSink();
        using var logs = CreateLoggerFactory(sink);

        var ex = await Assert.ThrowsAsync<IOException>(() =>
            CreateCoordinator(engine, physical, logs.CreateLogger<StorageMaintenanceCoordinator>())
                .RunOnceAsync(CancellationToken.None, maintenanceRunId: 12));

        Assert.Equal("index-checkpoint-failed", ex.Message);
        Assert.Equal(frames, engine.ProcessLocalIndexFrameCount);
        Assert.Equal(reserved, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(physical, engine.Index.IndexPhysicalBytes);
        Assert.False(File.Exists(SnapshotPath(dir)));
        Assert.Contains(sink.Events, static e => IsEvent(e, 3025));
        Assert.Contains(sink.Events, static e => IsEvent(e, 3028));
        Assert.DoesNotContain(sink.Events, static e => IsEvent(e, 3026));
    }

    [Fact]
    public async Task NothingToRetire_DoesNotFailTheCycle()
    {
        using var dir = TempStorageDir.Create();
        var reader = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = OpenWithCapacity(dir, reader);
        await CommitAsync(engine, "<idx-ck-noop@seg.test>");
        var frames = engine.ProcessLocalIndexFrameCount;
        var reserved = engine.ProcessLocalIndexReservedBytes;
        var physical = engine.Index.IndexPhysicalBytes;
        reader.UsedBytes = reader.TotalBytes;
        var sink = new CollectingSink();
        using var logs = CreateLoggerFactory(sink);

        var result = await CreateCoordinator(engine, physical, logs.CreateLogger<StorageMaintenanceCoordinator>())
            .RunOnceAsync(CancellationToken.None, maintenanceRunId: 13);

        Assert.NotEqual(StorageMaintenanceOutcome.Failed, result.Outcome);
        Assert.Equal(frames, engine.ProcessLocalIndexFrameCount);
        Assert.Equal(reserved, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(physical, engine.Index.IndexPhysicalBytes);
        Assert.False(File.Exists(SnapshotPath(dir)));
        AssertDurationMsNonNegative(Assert.Single(sink.Events, static e => IsEvent(e, 3027)));
        Assert.DoesNotContain(sink.Events, static e => IsEvent(e, 3028));
    }

    [Fact]
    public async Task Restart_ReconstructsPostCheckpointFrameReservations()
    {
        using var dir = TempStorageDir.Create();
        var reader = new MutableCapacityReader(total: 10_000_000, used: 0);
        ArticleId artId;
        ulong sequence;
        {
            await using var engine = OpenWithCapacity(dir, reader);
            var row = await CommitRowAsync(engine, "<idx-ck-restart@seg.test>");
            artId = row.ArtId;
            sequence = row.Sequence;
            Assert.True(engine.TryEvict(artId));
            await CreateCoordinator(engine, engine.Index.IndexPhysicalBytes).RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, engine.ProcessLocalIndexFrameCount);
            Assert.Equal(ArticleIndexRecordCodec.RecordLength, engine.ProcessLocalIndexReservedBytes);
            Assert.Equal(ArticleIndexSnapshotCodec.EncodedLength(1), engine.ProcessLocalCheckpointReservedBytes);
        }

        await using var reopened = OpenWithCapacity(dir, reader);
        Assert.True(reopened.Index.TryGet(artId, out var rowAfter));
        Assert.Equal(ArticleStorageState.Evicted, rowAfter.State);
        Assert.Equal(sequence, rowAfter.Sequence);
        Assert.Equal(1, reopened.ProcessLocalIndexFrameCount);
        Assert.Equal(ArticleIndexRecordCodec.RecordLength, reopened.ProcessLocalIndexReservedBytes);
        Assert.Equal(0, reopened.ProcessLocalCheckpointReservedBytes);
        Assert.Equal(0, reopened.Index.IndexPhysicalBytes);
    }

    [Fact]
    public async Task JournalCheckpoint_StillOmitsCommittedSequences()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sequence = await CommitAsync(engine, "<idx-ck-journal@seg.test>");
        var indexPhysical = engine.Index.IndexPhysicalBytes;
        var coordinator = new StorageMaintenanceCoordinator(
            engine,
            new ArticleSegmentPolicy(minimumDeadBytes: long.MaxValue, minimumDeadRatio: 100),
            journalCheckpointThresholdBytes: 1,
            indexCheckpointThresholdBytes: 0);

        await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.False(engine.Journal.TryGetSequenceRetention(sequence, out _));
        Assert.Equal(indexPhysical, engine.Index.IndexPhysicalBytes);
        Assert.False(File.Exists(SnapshotPath(dir)));
    }

    private static void AssertRow(
        FileArticleStorageEngine engine,
        ArticleId artId,
        ArticleStorageState state,
        ulong sequence,
        StoredArticleLocation? location = null)
    {
        Assert.True(engine.Index.TryGet(artId, out var row));
        Assert.Equal(state, row.State);
        Assert.Equal(sequence, row.Sequence);
        if (location is { } expected)
        {
            Assert.Equal(expected, row.Location);
        }
    }

    private static StorageMaintenanceCoordinator CreateCoordinator(
        FileArticleStorageEngine engine,
        long indexThreshold,
        ILogger? logger = null) =>
        new(
            engine,
            new ArticleSegmentPolicy(minimumDeadBytes: long.MaxValue, minimumDeadRatio: 100),
            journalCheckpointThresholdBytes: 0,
            logger,
            indexThreshold);

    private static async Task<ulong> CommitAsync(FileArticleStorageEngine engine, string messageId)
    {
        var row = await CommitRowAsync(engine, messageId);
        return row.Sequence;
    }

    private static async Task<StoredArticleMetadata> CommitRowAsync(FileArticleStorageEngine engine, string messageId)
    {
        var record = CreateRecord(messageId);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(engine.Index.TryGet(record.ArtId, out var row));
        Assert.Equal(ArticleStorageState.Present, row.State);
        return row;
    }

    private static string SnapshotPath(TempStorageDir dir) =>
        Path.Combine(dir.Options.ControlDir, FileArticleIndex.SnapshotFileName);

    private static FileArticleStorageEngine OpenWithCapacity(TempStorageDir dir, MutableCapacityReader reader) =>
        FileArticleStorageEngine.Open(
            dir.Options,
            volumeProbe: ScriptedVolumeProbe.Same(dir),
            capacityReader: reader);

    private static bool IsEvent(LogEvent logEvent, int eventId) =>
        logEvent.Properties.TryGetValue("EventId", out var value)
        && value.ToString().Contains(eventId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static void AssertDurationMsNonNegative(LogEvent logEvent)
    {
        var duration = Assert.Contains("DurationMs", logEvent.Properties);
        var raw = duration.ToString().Trim('"');
        Assert.True(
            double.TryParse(
                raw,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var ms));
        Assert.True(ms >= 0);
    }

    private static SerilogLoggerFactory CreateLoggerFactory(CollectingSink sink)
    {
        var serilog = new Serilog.LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return new SerilogLoggerFactory(serilog, dispose: true);
    }

    private static ArticleRecord CreateRecord(string messageId)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: index-checkpoint\r\n");
        _ = builder.Append("\r\nline1\r\nline2\r\n");
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

        public static ScriptedVolumeProbe Same(TempStorageDir dir) =>
            new(dir.Options.SegmentDir, dir.Options.ControlDir);

        public bool TryResolve(string directoryPath, out StorageVolumeIdentity identity)
        {
            var full = Path.GetFullPath(directoryPath);
            if (string.Equals(full, _segmentDir, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, _controlDir, StringComparison.OrdinalIgnoreCase))
            {
                identity = new StorageVolumeIdentity("volume-index-checkpoint");
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

        public long TotalBytes { get; }

        public long UsedBytes { get; set; }

        public StorageCapacitySnapshot Read() => new(TotalBytes, UsedBytes, Math.Max(0, TotalBytes - UsedBytes));
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-idx-ck-" + Guid.NewGuid().ToString("N"));
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
