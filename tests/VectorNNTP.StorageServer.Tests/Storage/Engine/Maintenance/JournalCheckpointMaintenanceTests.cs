using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog.Events;
using Serilog.Extensions.Logging;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;
using VectorNNTP.StorageServer.Storage.Engine.Policy;
using VectorNNTP.StorageServer.Tests.Fixtures;
using VectorNNTP.StorageServer.Tests.Logging;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine.Maintenance;

/// <summary>
/// Maintenance invokes engine journal checkpoint from the physical file length only.
/// </summary>
public sealed class JournalCheckpointMaintenanceTests
{
    [Fact]
    public async Task BelowThreshold_DoesNotCheckpoint()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sequence = await CommitAsync(engine, "<mnt-ck-below@seg.test>");
        var physical = engine.Journal.JournalPhysicalBytes;
        var sink = new CollectingSink();
        using var logs = CreateLoggerFactory(sink);

        var result = await CreateCoordinator(engine, physical + 1, logs.CreateLogger<StorageMaintenanceCoordinator>())
            .RunOnceAsync(CancellationToken.None, maintenanceRunId: 7);

        Assert.Equal(StorageMaintenanceOutcome.NoWork, result.Outcome);
        Assert.True(engine.Journal.TryGetSequenceRetention(sequence, out var retained));
        Assert.True(retained.IndexCommitted);
        Assert.DoesNotContain(sink.Events, static e => IsEvent(e, 3021));
    }

    [Fact]
    public async Task ExactThreshold_Checkpoints()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sequence = await CommitAsync(engine, "<mnt-ck-exact@seg.test>");
        var physical = engine.Journal.JournalPhysicalBytes;

        var result = await CreateCoordinator(engine, physical).RunOnceAsync(CancellationToken.None);

        Assert.NotEqual(StorageMaintenanceOutcome.Failed, result.Outcome);
        Assert.False(engine.Journal.TryGetSequenceRetention(sequence, out _));
    }

    [Fact]
    public async Task AboveThreshold_Checkpoints()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sequence = await CommitAsync(engine, "<mnt-ck-above@seg.test>");
        Assert.True(engine.Journal.JournalPhysicalBytes > 1);

        await CreateCoordinator(engine, thresholdBytes: 1).RunOnceAsync(CancellationToken.None);

        Assert.False(engine.Journal.TryGetSequenceRetention(sequence, out _));
    }

    [Fact]
    public async Task DisabledThreshold_DoesNotCheckpoint()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sequence = await CommitAsync(engine, "<mnt-ck-off@seg.test>");
        Assert.True(engine.Journal.JournalPhysicalBytes > 0);

        var result = await CreateCoordinator(engine, thresholdBytes: 0).RunOnceAsync(CancellationToken.None);

        Assert.Equal(StorageMaintenanceOutcome.NoWork, result.Outcome);
        Assert.True(engine.Journal.TryGetSequenceRetention(sequence, out var retained));
        Assert.True(retained.IndexCommitted);
    }

    [Fact]
    public async Task Checkpoint_RemovesCommittedState_AndKeepsIncompletePhysicalWrittenAndOpenCompaction()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var committed = await CommitAsync(engine, "<mnt-ck-done@seg.test>");
        engine.SuspendBackgroundPersist = true;
        var outstanding = CreateRecord("<mnt-ck-open@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(outstanding, CancellationToken.None)).Outcome);
        var incomplete = Assert.Single(engine.Journal.EnumerateIncomplete());
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(
                    1,
                    incomplete.Accept.Sequence,
                    new StoredArticleLocation(new SegmentId(3), 0, outstanding.ArtSize)),
                CancellationToken.None));
        var compactionId = engine.Journal.AllocateCompactionId();
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionBeginAsync(
                new JournalCompactionBeginRecord(1, compactionId, new SegmentId(7), 3),
                CancellationToken.None));
        var sink = new CollectingSink();
        using var logs = CreateLoggerFactory(sink);

        var result = await CreateCoordinator(engine, thresholdBytes: 1, logs.CreateLogger<StorageMaintenanceCoordinator>())
            .RunOnceAsync(CancellationToken.None, maintenanceRunId: 11);

        Assert.NotEqual(StorageMaintenanceOutcome.Failed, result.Outcome);
        Assert.False(engine.Journal.TryGetSequenceRetention(committed, out _));
        var kept = Assert.Single(engine.Journal.EnumerateIncomplete());
        Assert.Equal(incomplete.Accept.Sequence, kept.Accept.Sequence);
        Assert.NotNull(kept.PhysicalWritten);
        Assert.True(engine.Journal.TryGetCompaction(compactionId, out var compaction));
        Assert.Null(compaction.Retired);
        Assert.Contains(sink.Events, static e => IsEvent(e, 3021));
        Assert.Contains(sink.Events, static e => IsEvent(e, 3022));
    }

    [Fact]
    public async Task NothingToOmit_IsNotAFailure()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(CreateRecord("<mnt-ck-empty@seg.test>"), CancellationToken.None)).Outcome);
        var physical = engine.Journal.JournalPhysicalBytes;
        var sink = new CollectingSink();
        using var logs = CreateLoggerFactory(sink);

        var result = await CreateCoordinator(engine, physical, logs.CreateLogger<StorageMaintenanceCoordinator>())
            .RunOnceAsync(CancellationToken.None, maintenanceRunId: 4);

        Assert.Equal(StorageMaintenanceOutcome.NoWork, result.Outcome);
        Assert.Single(engine.Journal.EnumerateIncomplete());
        Assert.Contains(sink.Events, static e => IsEvent(e, 3023));
        Assert.DoesNotContain(sink.Events, static e => e.Level == LogEventLevel.Error);
    }

    [Fact]
    public async Task UnreconciledTail_IsDeferred_AndLaterCheckpointSucceeds()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var committed = await CommitAsync(engine, "<mnt-ck-tail-done@seg.test>");
        engine.SuspendBackgroundPersist = true;
        var pending = CreateRecord("<mnt-ck-tail-pend@seg.test>");
        engine.Journal.TestAfterWriteBeforeFlush = FlushFails;
        engine.Journal.TestBeforeDurableFlush = () => throw new IOException("second-flush");
        await Assert.ThrowsAsync<UnreconciledDurableTailException>(
            () => engine.AcceptAsync(pending, CancellationToken.None));
        var physical = engine.Journal.JournalPhysicalBytes;
        var sink = new CollectingSink();
        using var logs = CreateLoggerFactory(sink);
        var coordinator = CreateCoordinator(engine, thresholdBytes: 1, logs.CreateLogger<StorageMaintenanceCoordinator>());

        var deferred = await coordinator.RunOnceAsync(CancellationToken.None, maintenanceRunId: 8);

        Assert.NotEqual(StorageMaintenanceOutcome.Failed, deferred.Outcome);
        Assert.Equal(physical, engine.Journal.JournalPhysicalBytes);
        Assert.True(engine.Journal.TryGetSequenceRetention(committed, out var stillCommitted));
        Assert.True(stillCommitted.IndexCommitted);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Contains(sink.Events, static e => IsEvent(e, 3024));

        engine.Journal.TestBeforeDurableFlush = null;
        engine.Journal.TestAfterWriteBeforeFlush = null;
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(pending, CancellationToken.None)).Outcome);

        var completed = await coordinator.RunOnceAsync(CancellationToken.None, maintenanceRunId: 9);

        Assert.NotEqual(StorageMaintenanceOutcome.Failed, completed.Outcome);
        Assert.False(engine.Journal.TryGetSequenceRetention(committed, out _));
        Assert.Single(engine.Journal.EnumerateIncomplete());
        Assert.Contains(sink.Events, static e => IsEvent(e, 3022));
    }

    [Fact]
    public async Task CheckpointIOException_FailsTheMaintenanceRun_AndReleasesNoReservation()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = OpenWithCapacity(dir);
        var sequence = await CommitAsync(engine, "<mnt-ck-io@seg.test>");
        var reserved = engine.ProcessLocalJournalReservedBytes;
        Assert.True(reserved > 0);
        engine.Journal.CheckpointTestFault = point =>
        {
            if (point == FileArticleJournal.CheckpointFaultPoint.AfterTempFlushed)
            {
                throw new IOException("checkpoint-io");
            }
        };
        var sink = new CollectingSink();
        using var logs = CreateLoggerFactory(sink);
        var coordinator = CreateCoordinator(engine, thresholdBytes: 1, logs.CreateLogger<StorageMaintenanceCoordinator>());
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.Compaction.MaintenanceEnabled = true;
        options.Storage.Compaction.Enabled = false;
        options.Storage.Capacity.Enabled = false;
        var service = new StorageMaintenanceService(
            ct => coordinator.RunOnceAsync(ct, maintenanceRunId: 1),
            Options.Create(options),
            logs.CreateLogger<StorageMaintenanceService>(),
            delayAsync: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        await service.StartAsync(CancellationToken.None);
        await WaitForAsync(() => sink.Events.Any(static e => IsEvent(e, 3015)), TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(reserved, engine.ProcessLocalJournalReservedBytes);
        Assert.True(engine.Journal.TryGetSequenceRetention(sequence, out var retained));
        Assert.True(retained.IndexCommitted);
        Assert.Contains(sink.Events, static e => IsEvent(e, 3015));
        Assert.DoesNotContain(sink.Events, static e => IsEvent(e, 3022));
    }

    [Fact]
    public async Task SuccessfulCheckpoint_ReleasesOmittedJournalReservation()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = OpenWithCapacity(dir);
        var committed = await CommitAsync(engine, "<mnt-ck-rel@seg.test>");
        engine.SuspendBackgroundPersist = true;
        var outstanding = CreateRecord("<mnt-ck-rel-open@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(outstanding, CancellationToken.None)).Outcome);
        var incomplete = Assert.Single(engine.Journal.EnumerateIncomplete());
        var before = engine.ProcessLocalJournalReservedBytes;

        await CreateCoordinator(engine, thresholdBytes: 1).RunOnceAsync(CancellationToken.None);

        Assert.False(engine.Journal.TryGetSequenceRetention(committed, out _));
        Assert.Equal(1, engine.ProcessLocalJournalReservationCount);
        Assert.Equal(ArticleJournalFrameCodec.SequenceReservationBytes(outstanding.ArtSize), engine.ProcessLocalJournalReservedBytes);
        Assert.True(before > engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(incomplete.Accept.Sequence, Assert.Single(engine.Journal.EnumerateIncomplete()).Accept.Sequence);
    }

    [Fact]
    public async Task ConcurrentCycles_DoNotOverlapCheckpoint()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        _ = await CommitAsync(engine, "<mnt-ck-race@seg.test>");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = 0;
        var max = 0;
        engine.Journal.CheckpointTestFault = point =>
        {
            if (point != FileArticleJournal.CheckpointFaultPoint.AfterTempFlushed)
            {
                return;
            }

            var now = Interlocked.Increment(ref current);
            UpdateMax(ref max, now);
            firstEntered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            Interlocked.Decrement(ref current);
        };
        var coordinator = CreateCoordinator(engine, thresholdBytes: 1);

        var first = Task.Run(() => coordinator.RunOnceAsync(CancellationToken.None, maintenanceRunId: 1));
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var second = Task.Run(() => coordinator.RunOnceAsync(CancellationToken.None, maintenanceRunId: 2));
            var finishedEarly = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.NotSame(second, finishedEarly);
            Assert.Equal(1, Volatile.Read(ref max));
            release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, Volatile.Read(ref max));
        }
        finally
        {
            release.TrySetResult();
            engine.Journal.CheckpointTestFault = null;
        }
    }

    private static StorageMaintenanceCoordinator CreateCoordinator(
        FileArticleStorageEngine engine,
        long thresholdBytes,
        ILogger? logger = null) =>
        new(
            engine,
            new ArticleSegmentPolicy(enabled: false, minimumDeadBytes: 0, minimumDeadRatio: 0),
            thresholdBytes,
            logger);

    private static async Task<ulong> CommitAsync(FileArticleStorageEngine engine, string messageId)
    {
        var record = CreateRecord(messageId);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        var retained = engine.Journal.CopyRetainedJournalSequences();
        var match = Assert.Single(retained, static row => true);
        Assert.True(engine.Journal.TryGetSequenceRetention(match.Sequence, out var state));
        Assert.True(state.IndexCommitted);
        return match.Sequence;
    }

    private static FileArticleStorageEngine OpenWithCapacity(TempStorageDir dir)
    {
        var reader = new MutableCapacityReader(total: 10_000_000, used: 0);
        return FileArticleStorageEngine.Open(
            dir.Options with { CapacityAdmissionEnabled = true },
            volumeProbe: ScriptedVolumeProbe.Same(dir),
            capacityReader: reader);
    }

    private static void FlushFails(FileStream stream, long start, int expectedLength)
    {
        _ = stream;
        _ = start;
        _ = expectedLength;
        throw new IOException("flush-failed");
    }

    private static bool IsEvent(LogEvent logEvent, int eventId) =>
        logEvent.Properties.TryGetValue("EventId", out var value)
        && value.ToString().Contains(eventId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static SerilogLoggerFactory CreateLoggerFactory(CollectingSink sink)
    {
        var serilog = new Serilog.LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return new SerilogLoggerFactory(serilog, dispose: true);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met before timeout.");
            }

            await Task.Delay(10);
        }
    }

    private static void UpdateMax(ref int location, int candidate)
    {
        while (true)
        {
            var observed = Volatile.Read(ref location);
            if (candidate <= observed)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref location, candidate, observed) == observed)
            {
                return;
            }
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
        _ = builder.Append("Subject: journal-checkpoint\r\n");
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

        public static ScriptedVolumeProbe Same(TempStorageDir dir) =>
            new(dir.Options.SegmentDir, dir.Options.ControlDir);

        public bool TryResolve(string directoryPath, out StorageVolumeIdentity identity)
        {
            var full = Path.GetFullPath(directoryPath);
            if (string.Equals(full, _segmentDir, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, _controlDir, StringComparison.OrdinalIgnoreCase))
            {
                identity = new StorageVolumeIdentity("volume-maintenance");
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

        public long UsedBytes { get; }

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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-mnt-ck-" + Guid.NewGuid().ToString("N"));
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
