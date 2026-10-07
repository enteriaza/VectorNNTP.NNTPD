using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.Maintenance;
using VectorNNTP.StorageServer.Storage.Engine.Policy;
using Xunit.Abstractions;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine.Maintenance;

/// <summary>
/// Phase 33: sustained checkpoint, retention, reclaim, concurrency, and restart stress after the
/// Phase 32 journal-checkpoint admission credit. Synthetic capacity forces the credit path.
/// </summary>
public sealed class JournalCheckpointRetentionStressTests
{
    private const long Total = 1_000_000;

    private static readonly DateTimeOffset Arrival = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Grace = TimeSpan.FromDays(7);

    private readonly ITestOutputHelper _output;

    public JournalCheckpointRetentionStressTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Repeated_constrained_checkpoints_release_exactly_omitted_reservations()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time, maximumUtilization: 100);

        long peakReserved = 0;
        long totalReleasedPhysical = 0;
        var successfulCheckpoints = 0;
        var cycles = 24;
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            reader.UsedBytes = 0;
            var id = await PublishAsync(engine, $"<p33-repeat-{cycle}@example>", bodyLines: 2200);
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            var reservedBefore = engine.ProcessLocalJournalReservedBytes;
            var physicalBefore = engine.Journal.JournalPhysicalBytes;
            peakReserved = Math.Max(peakReserved, reservedBefore);
            reader.UsedBytes = 900_000;
            Assert.True(reservedBefore > Total - reader.UsedBytes);

            var started = Stopwatch.StartNew();
            var result = await Coordinator(engine, checkpointThreshold: 1).RunOnceAsync(CancellationToken.None);
            started.Stop();

            var reservedAfter = engine.ProcessLocalJournalReservedBytes;
            var physicalAfter = engine.Journal.JournalPhysicalBytes;
            Assert.Equal(0, reservedAfter);
            Assert.Equal(0, engine.ProcessLocalJournalReservationCount);
            Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
            Assert.True(physicalAfter < physicalBefore);
            Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
            Assert.True(engine.TryRead(id, out _));
            Assert.Equal(ArticleStorageState.Present, State(engine, id));
            totalReleasedPhysical += physicalBefore - physicalAfter;
            successfulCheckpoints++;
            _output.WriteLine(
                $"cycle {cycle} ms {started.Elapsed.TotalMilliseconds:0.0} reserved {reservedBefore}->{reservedAfter} "
                + $"physical {physicalBefore}->{physicalAfter} expired {result.Recovery!.Value.PressureArticlesExpired}");
        }

        Assert.Equal(cycles, successfulCheckpoints);
        Assert.True(peakReserved > 100_000);
        Assert.True(totalReleasedPhysical > 0);
        Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
        Assert.True(engine.Journal.JournalPhysicalBytes < 256);
    }

    [Fact]
    public async Task Concurrent_accept_during_checkpoint_keeps_incomplete_charged_and_releases_only_omitted()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time, maximumUtilization: 100);

        var committed = await PublishAsync(engine, "<p33-concurrent-done@example>", bodyLines: 2200);
        var committedReserved = engine.ProcessLocalJournalReservedBytes;
        Assert.True(committedReserved > 0);

        var snapshotEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Journal.TestBeforeCheckpointSnapshot = () =>
        {
            snapshotEntered.TrySetResult();
            releaseSnapshot.Task.GetAwaiter().GetResult();
        };

        var checkpointTask = Task.Run(() => engine.CheckpointTruncateCommitted());
        Assert.Same(snapshotEntered.Task, await Task.WhenAny(snapshotEntered.Task, Task.Delay(TimeSpan.FromSeconds(10))));

        engine.SuspendBackgroundPersist = true;
        var incomplete = await engine.AcceptAsync(Article("<p33-concurrent-open@example>", bodyLines: 40), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, incomplete.Outcome);
        var incompleteReserved = engine.ProcessLocalJournalReservedBytes - committedReserved;
        Assert.True(incompleteReserved > 0);
        Assert.Equal(2, engine.ProcessLocalJournalReservationCount);

        // Raise used after Accept so the concurrent Accept is admitted, then force the Phase 32
        // credit path for the committed sequence that this checkpoint will omit.
        reader.UsedBytes = 900_000;
        Assert.True(committedReserved > Total - reader.UsedBytes);
        releaseSnapshot.TrySetResult();
        Assert.True(await checkpointTask > 0);

        Assert.Equal(incompleteReserved, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(1, engine.ProcessLocalJournalReservationCount);
        Assert.Single(engine.Journal.EnumerateIncomplete());
        Assert.True(engine.TryRead(committed, out _));
        Assert.True(engine.TryRead(incomplete.ArtId, out _));
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);

        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.CheckpointTruncateCommitted() > 0);
        Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
        Assert.True(engine.TryRead(committed, out _));
        Assert.True(engine.TryRead(incomplete.ArtId, out _));
    }

    [Fact]
    public async Task Constrained_checkpoint_failure_keeps_reservations_and_retries()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time, maximumUtilization: 100);
        var id = await PublishAsync(engine, "<p33-fault@example>", bodyLines: 2200);
        var reserved = engine.ProcessLocalJournalReservedBytes;
        var physical = engine.Journal.JournalPhysicalBytes;
        reader.UsedBytes = 900_000;
        Assert.True(reserved > Total - reader.UsedBytes);

        engine.Journal.CheckpointTestFault = point =>
        {
            if (point == FileArticleJournal.CheckpointFaultPoint.AfterTempFlushed)
            {
                throw new IOException("p33-temp-fault");
            }
        };

        var fault = Assert.Throws<IOException>(() => engine.CheckpointTruncateCommitted());
        Assert.Contains("p33-temp-fault", fault.Message, StringComparison.Ordinal);
        Assert.Equal(reserved, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(physical, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
        Assert.True(engine.TryRead(id, out _));

        engine.Journal.CheckpointTestFault = null;
        Assert.True(engine.CheckpointTruncateCommitted() > 0);
        Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
        Assert.True(engine.Journal.JournalPhysicalBytes < physical);
        Assert.True(engine.TryRead(id, out _));
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
    }

    [Fact]
    public async Task Sustained_pressure_expiration_checkpoint_and_fully_dead_reclaim_progress()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time, maximumUtilization: 100);

        var oldIds = new List<ArticleId>(8);
        for (var i = 0; i < 8; i++)
        {
            oldIds.Add(await PublishAsync(engine, $"<p33-old-{i}@example>", bodyLines: 40));
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
        }

        Assert.True(engine.CheckpointTruncateCommitted() > 0);
        Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);

        time.Advance(Grace + TimeSpan.FromDays(1));
        var young = await PublishAsync(engine, "<p33-young@example>", bodyLines: 40);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.CheckpointTruncateCommitted() > 0);
        reader.UsedBytes = 920_000;

        long cumulativeExpired = 0;
        long cumulativeFullyDead = 0;
        var cycles = 0;
        var maxCycles = 32;
        while (cycles < maxCycles && oldIds.Exists(id => engine.TryRead(id, out _)))
        {
            var started = Stopwatch.StartNew();
            var result = await Coordinator(engine, checkpointThreshold: 1).RunOnceAsync(CancellationToken.None);
            started.Stop();
            cycles++;
            cumulativeExpired += result.Recovery!.Value.PressureArticlesExpired;
            cumulativeFullyDead += result.Recovery.Value.FullyDeadBytesReclaimed;
            Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
            Assert.True(engine.TryRead(young, out _));
            _output.WriteLine(
                $"pressure-cycle {cycles} ms {started.Elapsed.TotalMilliseconds:0.0} "
                + $"expired {result.Recovery.Value.PressureArticlesExpired} "
                + $"fullyDead {result.Recovery.Value.FullyDeadBytesReclaimed} "
                + $"state {result.Recovery.Value.PressureStateBefore}");
        }

        Assert.True(cycles < maxCycles);
        Assert.Equal(8, cumulativeExpired);
        Assert.True(cumulativeFullyDead > 0);
        foreach (var id in oldIds)
        {
            Assert.False(engine.TryRead(id, out _));
        }

        Assert.True(engine.TryRead(young, out _));
        Assert.Equal(ArticleStorageState.Present, State(engine, young));
        Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
    }

    [Fact]
    public async Task Restart_after_index_committed_before_and_after_checkpoint_reconstructs_reservations()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        ArticleId beforeCheckpointId;
        long reservedBeforeRestart;
        await using (var engine = Open(dir, reader, time, maximumUtilization: 100))
        {
            beforeCheckpointId = await PublishAsync(engine, "<p33-f2@example>", bodyLines: 2200);
            reservedBeforeRestart = engine.ProcessLocalJournalReservedBytes;
            Assert.True(reservedBeforeRestart > 0);
            Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
            Assert.True(engine.TryRead(beforeCheckpointId, out _));
        }

        await using (var restarted = Open(dir, reader, time, maximumUtilization: 100))
        {
            Assert.Equal(reservedBeforeRestart, restarted.ProcessLocalJournalReservedBytes);
            Assert.Equal(1, restarted.ProcessLocalJournalReservationCount);
            Assert.True(restarted.TryRead(beforeCheckpointId, out _));
            reader.UsedBytes = 900_000;
            Assert.True(restarted.CheckpointTruncateCommitted() > 0);
            Assert.Equal(0, restarted.ProcessLocalJournalReservedBytes);
            Assert.True(restarted.TryRead(beforeCheckpointId, out _));
        }

        await using var afterCheckpoint = Open(dir, reader, time, maximumUtilization: 100);
        Assert.Equal(0, afterCheckpoint.ProcessLocalJournalReservedBytes);
        Assert.Equal(0, afterCheckpoint.ProcessLocalJournalReservationCount);
        Assert.True(afterCheckpoint.TryRead(beforeCheckpointId, out _));
        Assert.Equal(0, afterCheckpoint.Journal.OutstandingRecoverableBytes);
    }

    [Fact]
    public async Task Restart_after_expiration_before_reclaim_keeps_evicted_and_continues_cleanup()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        ArticleId expiredId;
        ArticleId keptId;
        await using (var engine = Open(dir, reader, time, maximumUtilization: 100))
        {
            expiredId = await PublishAsync(engine, "<p33-f4-expire@example>", bodyLines: 40);
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            Assert.True(engine.CheckpointTruncateCommitted() > 0);
            time.Advance(Grace + TimeSpan.FromDays(1));
            keptId = await PublishAsync(engine, "<p33-f4-keep@example>", bodyLines: 40);
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            Assert.True(engine.CheckpointTruncateCommitted() > 0);
            reader.UsedBytes = 920_000;
            var expired = await Coordinator(engine, checkpointThreshold: 1).RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, expired.Recovery!.Value.PressureArticlesExpired);
            Assert.False(engine.TryRead(expiredId, out _));
            Assert.True(engine.TryRead(keptId, out _));
        }

        await using var restarted = Open(dir, reader, time, maximumUtilization: 100);
        Assert.False(restarted.TryRead(expiredId, out _));
        Assert.True(restarted.TryRead(keptId, out _));
        Assert.Equal(0, restarted.ProcessLocalJournalReservedBytes);
        reader.UsedBytes = 920_000;
        var followUp = await Coordinator(restarted, checkpointThreshold: 1).RunOnceAsync(CancellationToken.None);
        Assert.True(followUp.Recovery!.Value.FullyDeadBytesReclaimed >= 0);
        Assert.False(restarted.TryRead(expiredId, out _));
        Assert.True(restarted.TryRead(keptId, out _));
    }

    [Fact]
    public async Task Concurrent_reads_during_pressure_checkpoint_cycles_stay_consistent()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time, maximumUtilization: 100);

        var hot = await PublishAsync(engine, "<p33-hot@example>", bodyLines: 40);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var cold = await PublishAsync(engine, "<p33-cold@example>", bodyLines: 40);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var doomed = await PublishAsync(engine, "<p33-doomed@example>", bodyLines: 40);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.CheckpointTruncateCommitted() > 0);

        time.Advance(Grace + TimeSpan.FromDays(1));
        var young = await PublishAsync(engine, "<p33-read-young@example>", bodyLines: 40);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.CheckpointTruncateCommitted() > 0);
        reader.UsedBytes = 920_000;

        using var cts = new CancellationTokenSource();
        var youngMisses = 0;
        var hotHits = 0;
        var readerTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (engine.TryRead(hot, out _))
                {
                    Interlocked.Increment(ref hotHits);
                }

                _ = engine.TryRead(cold, out _);
                _ = engine.TryRead(doomed, out _);
                if (!engine.TryRead(young, out _))
                {
                    Interlocked.Increment(ref youngMisses);
                }

                await Task.Yield();
            }
        });

        for (var i = 0; i < 12; i++)
        {
            _ = await Coordinator(engine, checkpointThreshold: 1).RunOnceAsync(CancellationToken.None);
            Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
            Assert.True(engine.TryRead(young, out _));
        }

        await cts.CancelAsync();
        await readerTask;
        Assert.Equal(0, Volatile.Read(ref youngMisses));
        Assert.True(Volatile.Read(ref hotHits) > 0);
        Assert.False(engine.TryRead(doomed, out _));
        Assert.False(engine.TryRead(hot, out _));
        Assert.False(engine.TryRead(cold, out _));
        Assert.True(engine.TryRead(young, out _));
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
    }

    [Fact]
    public async Task Boundedness_under_accept_checkpoint_expire_reclaim_loops()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var reader = new MutableCapacityReader(Total, 0);
        await using var engine = Open(dir, reader, time, maximumUtilization: 100);

        long maxJournalReserved = 0;
        long maxIndexReserved = 0;
        long maxCheckpointReserved = 0;
        long maxPhysical = 0;
        long maxOutstanding = 0;
        ArticleId? lastSurvivor = null;

        for (var wave = 0; wave < 10; wave++)
        {
            reader.UsedBytes = 0;
            var batch = new List<ArticleId>(3);
            for (var i = 0; i < 3; i++)
            {
                batch.Add(await PublishAsync(engine, $"<p33-bound-{wave}-{i}@example>", bodyLines: 800));
                await engine.Segments.CloseActiveAsync(CancellationToken.None);
            }

            maxJournalReserved = Math.Max(maxJournalReserved, engine.ProcessLocalJournalReservedBytes);
            maxPhysical = Math.Max(maxPhysical, engine.Journal.JournalPhysicalBytes);
            maxOutstanding = Math.Max(maxOutstanding, engine.Journal.OutstandingRecoverableBytes);
            maxIndexReserved = Math.Max(maxIndexReserved, engine.ProcessLocalIndexReservedBytes);
            maxCheckpointReserved = Math.Max(maxCheckpointReserved, engine.ProcessLocalCheckpointReservedBytes);

            reader.UsedBytes = 880_000;
            Assert.True(engine.CheckpointTruncateCommitted() >= 0);
            Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
            Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
            Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);

            time.Advance(Grace + TimeSpan.FromDays(1));
            reader.UsedBytes = 920_000;
            for (var i = 0; i < 8; i++)
            {
                _ = await Coordinator(engine, checkpointThreshold: 1).RunOnceAsync(CancellationToken.None);
            }

            foreach (var id in batch)
            {
                Assert.False(engine.TryRead(id, out _));
            }

            reader.UsedBytes = 0;
            lastSurvivor = await PublishAsync(engine, $"<p33-survivor-{wave}@example>", bodyLines: 4);
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            Assert.True(engine.CheckpointTruncateCommitted() >= 0);
            Assert.True(engine.TryRead(lastSurvivor.Value, out _));
        }

        Assert.NotNull(lastSurvivor);
        Assert.True(engine.TryRead(lastSurvivor.Value, out _));
        Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        Assert.True(engine.Journal.JournalPhysicalBytes < 4_096);
        Assert.True(maxJournalReserved > 0);
        Assert.True(maxIndexReserved > 0);
        Assert.Equal(0, maxCheckpointReserved);
        Assert.Equal(0, maxOutstanding);
        _output.WriteLine(
            $"bounded peak journalReserved={maxJournalReserved} physical={maxPhysical} indexReserved={maxIndexReserved} "
            + $"finalPhysical={engine.Journal.JournalPhysicalBytes}");
    }

    private static StorageMaintenanceCoordinator Coordinator(
        FileArticleStorageEngine engine,
        long checkpointThreshold) =>
        new(
            engine,
            new ArticleSegmentPolicy(new ArticleCompactionPolicyOptions()),
            journalCheckpointThresholdBytes: checkpointThreshold,
            logger: NullLogger.Instance,
            maxRetentionAge: TimeSpan.Zero,
            bulkPressure: new BulkStoragePressureOptions
            {
                MinimumRetentionAge = Grace,
            });

    private static FileArticleStorageEngine Open(
        TempDir dir,
        IStorageCapacityReader reader,
        TimeProvider time,
        int maximumUtilization) =>
        FileArticleStorageEngine.Open(
            dir.Options with
            {
                CapacityMaximumUtilization = maximumUtilization,
                CapacityCompactionHeadroom = maximumUtilization == 100 ? 0 : 10,
                CapacityMaximumUsageCapacity = maximumUtilization == 100 ? 100 : 50,
                CapacityFreeCapacity = maximumUtilization == 100 ? 1 : 5,
            },
            logger: NullLogger.Instance,
            timeProvider: time,
            capacityReader: reader);

    private static async Task<ArticleId> PublishAsync(
        FileArticleStorageEngine engine,
        string messageId,
        int bodyLines)
    {
        engine.SuspendBackgroundPersist = true;
        var accepted = await engine.AcceptAsync(Article(messageId, bodyLines), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        return accepted.ArtId;
    }

    private static ArticleRecord Article(string messageId, int bodyLines)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: phase33\r\n\r\n");
        for (var i = 0; i < bodyLines; i++)
        {
            _ = builder.Append(new string('x', 64)).Append("\r\n");
        }

        var created = ArticleRecordFactory.TryCreate(
            new NntpArticleParser("cache01.usenet.ninja"),
            Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private static ArticleStorageState State(FileArticleStorageEngine engine, ArticleId artId)
    {
        Assert.True(engine.Index.TryGet(artId, out var row));
        return row.State;
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

        public StorageCapacitySnapshot Read() =>
            new(TotalBytes, UsedBytes, Math.Max(0L, TotalBytes - UsedBytes));
    }

    private sealed class TempDir : IDisposable
    {
        private TempDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public ArticleStorageRuntimeOptions Options { get; }

        private string Root { get; }

        public static TempDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase33-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "control"));
            Directory.CreateDirectory(Path.Combine(root, "cache"));
            return new TempDir(
                root,
                new ArticleStorageRuntimeOptions(
                    ControlDir: Path.Combine(root, "control"),
                    SegmentDir: Path.Combine(root, "cache"),
                    JournalSoftLimitBytes: ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                    JournalHardLimitBytes: ArticleStorageOptions.DefaultJournalHardLimitBytes,
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
