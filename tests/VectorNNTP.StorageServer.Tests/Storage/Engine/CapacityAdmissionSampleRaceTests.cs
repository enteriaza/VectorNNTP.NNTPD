using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Phase 2E: a <c>UsedBytes</c> sample taken before a temporary reservation is released
/// must not admit a later write.
/// </summary>
public sealed class CapacityAdmissionSampleRaceTests
{
    [Fact]
    public async Task Exact_fit_accepts_and_one_byte_over_does_not_write()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<race-fit@seg.test>");
        var admit = AdmitBytes(record);
        const long total = 100_000;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, 0.80);
        var reader = new MutableCapacityReader(total, used: ceiling - admit);
        await using var engine = Open(dir, reader);
        var journalBefore = engine.Journal.JournalPhysicalBytes;

        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.Equal(admit, engine.ProcessLocalReservedBytes);

        reader.UsedBytes = ceiling - admit + 1;
        var journalAtCeiling = engine.Journal.JournalPhysicalBytes;
        var rejected = await engine.AcceptAsync(CreateRecord("<race-over@seg.test>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, rejected.Outcome);
        Assert.Equal(journalAtCeiling, engine.Journal.JournalPhysicalBytes);
        Assert.True(engine.Journal.JournalPhysicalBytes > journalBefore);
        Assert.Equal(0, engine.PhysicalAppendCount);
    }

    [Fact]
    public async Task Stale_sample_then_checkpoint_release_rejects_accept_without_a_write()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<race-ckpt@seg.test>");
        var segmentBytes = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        const long total = 100_000;
        const long releasedBytes = 1_000;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, 0.80);
        var stale = ceiling - segmentBytes;
        var reader = new MutableCapacityReader(total, used: stale);
        await using var engine = Open(dir, reader);
        Assert.Same(engine.SegmentCapacity, engine.ControlCapacity);

        ulong reservationId = 0;
        _ = engine.ControlCapacity!.WithLedger(ledger =>
        {
            reservationId = ledger.ReserveCheckpoint(releasedBytes);
            return 0;
        });

        var journalBefore = engine.Journal.JournalPhysicalBytes;
        var indexBefore = engine.Index.DurableLength;
        var result = await AdmitWhileTemporaryReservationIsReleasedAsync(
            engine,
            reader,
            staleUsed: stale,
            flushedUsed: stale + releasedBytes,
            release: () => engine.ControlCapacity.WithLedger(ledger => ledger.ReleaseCheckpoint(reservationId)));

        Assert.Equal(stale, result.ObservedUsedBytes);
        Assert.True(ProcessLocalCapacityLedger.WouldFit(stale, 0, 0, total, segmentBytes, 0.80));
        Assert.False(ProcessLocalCapacityLedger.WouldFit(stale + releasedBytes, 0, 0, total, segmentBytes, 0.80));
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, result.Accept.Outcome);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
        Assert.Equal(journalBefore, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(indexBefore, engine.Index.DurableLength);
        Assert.Equal(0, engine.PhysicalAppendCount);
    }

    [Fact]
    public async Task Stale_sample_then_compaction_destination_release_rejects_accept_without_a_write()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<race-dest@seg.test>");
        var segmentBytes = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        const long total = 100_000;
        const long releasedBytes = 1_000;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, 0.80);
        var stale = ceiling - segmentBytes;
        var reader = new MutableCapacityReader(total, used: stale);
        await using var engine = Open(dir, reader);
        Assert.Same(engine.SegmentCapacity, engine.ControlCapacity);

        _ = engine.SegmentCapacity!.WithLedger(ledger =>
        {
            ledger.ReserveCompaction(4, 1, releasedBytes);
            return 0;
        });

        var journalBefore = engine.Journal.JournalPhysicalBytes;
        var result = await AdmitWhileTemporaryReservationIsReleasedAsync(
            engine,
            reader,
            staleUsed: stale,
            flushedUsed: stale + releasedBytes,
            release: () => engine.SegmentCapacity.WithLedger(ledger => ledger.ReleaseCompaction(4, 1)));

        Assert.Equal(stale, result.ObservedUsedBytes);
        Assert.True(ProcessLocalCapacityLedger.WouldFit(stale, 0, 0, total, segmentBytes, 0.80));
        Assert.False(ProcessLocalCapacityLedger.WouldFit(stale + releasedBytes, 0, 0, total, segmentBytes, 0.80));
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, result.Accept.Outcome);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(journalBefore, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(0, engine.PhysicalAppendCount);
    }

    [Fact]
    public async Task Stale_sample_then_destination_release_rejects_checkpoint_without_a_temp()
    {
        using var dir = TempStorageDir.Create();
        const long total = 1_000_000;
        const long releasedBytes = 5_000;
        var reader = new MutableCapacityReader(total, used: 0);
        await using var engine = Open(dir, reader);
        Assert.Same(engine.SegmentCapacity, engine.ControlCapacity);
        var accepted = await engine.AcceptAsync(CreateRecord("<race-ckpt-b@seg.test>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.Empty(engine.Journal.EnumerateIncomplete());

        var reserved = engine.ProcessLocalReservedBytes;
        var fence = ArticleJournalFrameCodec.EncodeSequenceFence(accepted.Sequence + 1).Length;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, 0.80);
        var stale = ceiling - reserved - fence;
        Assert.True(stale >= releasedBytes);
        reader.UsedBytes = stale;
        _ = engine.SegmentCapacity!.WithLedger(ledger =>
        {
            ledger.ReserveCompaction(8, 2, releasedBytes);
            return 0;
        });

        var journalBefore = engine.Journal.JournalPhysicalBytes;
        var sampled = new TaskCompletionSource<StorageCapacitySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.TestAfterCapacitySample = snap =>
        {
            sampled.TrySetResult(snap);
            proceed.Task.GetAwaiter().GetResult();
        };

        var checkpoint = Task.Run(() => engine.CheckpointTruncateCommitted());
        try
        {
            var entered = await Task.WhenAny(sampled.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(sampled.Task, entered);
            Assert.Equal(stale, (await sampled.Task).UsedBytes);
            reader.UsedBytes = stale + releasedBytes;
            Assert.True(engine.SegmentCapacity.WithLedger(ledger => ledger.ReleaseCompaction(8, 2)));
            Assert.True(ProcessLocalCapacityLedger.WouldFit(
                stale,
                engine.ProcessLocalArticleReservedBytes,
                0,
                total,
                fence,
                0.80,
                journalReservedBytes: engine.ProcessLocalJournalReservedBytes,
                indexReservedBytes: engine.ProcessLocalIndexReservedBytes));
            Assert.False(ProcessLocalCapacityLedger.WouldFit(
                stale + releasedBytes,
                engine.ProcessLocalArticleReservedBytes,
                0,
                total,
                fence,
                0.80,
                journalReservedBytes: engine.ProcessLocalJournalReservedBytes,
                indexReservedBytes: engine.ProcessLocalIndexReservedBytes));
        }
        finally
        {
            proceed.TrySetResult();
        }

        Assert.Equal(0, await checkpoint.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(journalBefore, engine.Journal.JournalPhysicalBytes);
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
        Assert.Equal(0, engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(reserved, engine.ProcessLocalReservedBytes);
        Assert.Empty(Directory.EnumerateFiles(dir.Options.ControlDir, "*.tmp"));
    }

    [Fact]
    public async Task Two_admits_racing_for_exact_remaining_capacity_admit_one()
    {
        using var dir = TempStorageDir.Create();
        var first = CreateRecord("<race-one-a@seg.test>");
        var second = CreateRecord("<race-one-b@seg.test>");
        var admit = AdmitBytes(first);
        Assert.Equal(admit, AdmitBytes(second));
        const long total = 100_000;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, 0.80);
        var reader = new MutableCapacityReader(total, used: ceiling - admit);
        await using var engine = Open(dir, reader);
        engine.SuspendBackgroundPersist = true;

        using var gate = new ManualResetEventSlim(false);
        var outcomes = new ArticleAcceptOutcome[2];
        var tasks = new[]
        {
            Task.Run(async () =>
            {
                gate.Wait();
                outcomes[0] = (await engine.AcceptAsync(first, CancellationToken.None)).Outcome;
            }),
            Task.Run(async () =>
            {
                gate.Wait();
                outcomes[1] = (await engine.AcceptAsync(second, CancellationToken.None)).Outcome;
            }),
        };
        gate.Set();
        await Task.WhenAll(tasks);

        Assert.Equal(1, outcomes.Count(static outcome => outcome == ArticleAcceptOutcome.Accepted));
        Assert.Equal(1, outcomes.Count(static outcome => outcome == ArticleAcceptOutcome.RejectedCapacity));
        Assert.Equal(admit, engine.ProcessLocalReservedBytes);
        Assert.Equal(1, engine.ProcessLocalJournalReservationCount);
        Assert.Equal(0, engine.PhysicalAppendCount);
    }

    private static async Task<(ArticleAcceptResult Accept, long ObservedUsedBytes)> AdmitWhileTemporaryReservationIsReleasedAsync(
        FileArticleStorageEngine engine,
        MutableCapacityReader reader,
        long staleUsed,
        long flushedUsed,
        Func<bool> release)
    {
        var sampled = new TaskCompletionSource<StorageCapacitySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var once = 0;
        engine.TestAfterCapacitySample = snap =>
        {
            if (Interlocked.Increment(ref once) != 1)
            {
                return;
            }

            sampled.TrySetResult(snap);
            proceed.Task.GetAwaiter().GetResult();
        };

        var admit = Task.Run(() => engine.AcceptAsync(CreateRecord("<race-live@seg.test>"), CancellationToken.None));
        try
        {
            var entered = await Task.WhenAny(sampled.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(sampled.Task, entered);
            var observed = await sampled.Task;
            Assert.Equal(staleUsed, observed.UsedBytes);
            reader.UsedBytes = flushedUsed;
            Assert.True(release());
            proceed.TrySetResult();
            return (await admit.WaitAsync(TimeSpan.FromSeconds(10)), observed.UsedBytes);
        }
        finally
        {
            proceed.TrySetResult();
        }
    }

    private static long AdmitBytes(ArticleRecord record) =>
        SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize)
        + ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize)
        + ArticleIndexRecordCodec.RecordLength;

    private static FileArticleStorageEngine Open(TempStorageDir dir, MutableCapacityReader reader) =>
        FileArticleStorageEngine.Open(
            dir.Options with
            {
                CapacityAdmissionEnabled = true,
                CapacityMaximumUtilization = 0.80,
                CapacityCompactionHeadroom = 0.10,
            },
            volumeProbe: ScriptedVolumeProbe.Same(dir),
            capacityReader: reader);

    private static ArticleRecord CreateRecord(string messageId)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: capacity-race\r\n");
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
                identity = new StorageVolumeIdentity("volume-shared");
                return true;
            }

            identity = default;
            return false;
        }
    }

    private sealed class MutableCapacityReader(long total, long used) : IStorageCapacityReader
    {
        public long TotalBytes { get; set; } = total;

        public long UsedBytes { get; set; } = used;

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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-race-" + Guid.NewGuid().ToString("N"));
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
                // best-effort
            }
        }
    }
}
