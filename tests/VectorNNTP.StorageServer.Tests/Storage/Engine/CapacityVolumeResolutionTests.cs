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

/// <summary>Per-volume capacity reader and ledger routing.</summary>
public sealed class CapacityVolumeResolutionTests
{
    [Fact]
    public void SameVolume_SharesOneLedgerAndOneReader()
    {
        using var dir = TempStorageDir.Create();
        var reader = new MutableCapacityReader(total: 1_000_000, used: 0);
        var probe = ScriptedVolumeProbe.SameVolume(dir);
        using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: reader,
            volumeProbe: probe);

        Assert.True(probe.Calls >= 2);
        Assert.NotNull(engine.SegmentCapacity);
        Assert.Same(engine.SegmentCapacity, engine.ControlCapacity);
        Assert.Same(reader, engine.SegmentCapacity!.Reader);
        Assert.Same(engine.SegmentCapacity.Reader, engine.ControlCapacity!.Reader);
        Assert.Equal(engine.SegmentCapacity.Identity, engine.ControlCapacity.Identity);
    }

    [Fact]
    public void DifferentVolumes_UseIndependentLedgersAndReaders()
    {
        using var dir = TempStorageDir.Create();
        var segmentReader = new MutableCapacityReader(total: 1_000_000, used: 0);
        var controlReader = new MutableCapacityReader(total: 2_000_000, used: 0);
        var probe = ScriptedVolumeProbe.SplitVolumes(dir);
        using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: segmentReader,
            volumeProbe: probe,
            controlCapacityReader: controlReader);

        Assert.NotNull(engine.SegmentCapacity);
        Assert.NotNull(engine.ControlCapacity);
        Assert.NotSame(engine.SegmentCapacity, engine.ControlCapacity);
        Assert.Same(segmentReader, engine.SegmentCapacity!.Reader);
        Assert.Same(controlReader, engine.ControlCapacity!.Reader);
        Assert.NotEqual(engine.SegmentCapacity.Identity, engine.ControlCapacity.Identity);
    }

    [Fact]
    public async Task SplitVolume_SegmentReservationIsInvisibleToControlLedger()
    {
        using var dir = TempStorageDir.Create();
        var segmentReader = new MutableCapacityReader(total: 1_000_000, used: 0);
        var controlReader = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: segmentReader,
            volumeProbe: ScriptedVolumeProbe.SplitVolumes(dir),
            controlCapacityReader: controlReader);
        engine.SuspendBackgroundPersist = true;

        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(CreateRecord("<vol-seg@seg.test>"), CancellationToken.None)).Outcome);

        Assert.True(engine.ProcessLocalArticleReservedBytes > 0);
        Assert.Equal(0, engine.ControlCapacity!.WithLedger(static ledger => ledger.ArticleReservedBytes));
        Assert.Equal(
            ArticleJournalFrameCodec.SequenceReservationBytes(
                CreateRecord("<vol-seg@seg.test>").ArtSize),
            engine.ControlCapacity.WithLedger(static ledger => ledger.JournalReservedBytes));
        Assert.Equal(
            engine.ControlCapacity.WithLedger(static ledger => ledger.JournalReservedBytes)
            + engine.ControlCapacity.WithLedger(static ledger => ledger.IndexReservedBytes),
            engine.ControlCapacity.WithLedger(static ledger => ledger.ReservedBytes));
        Assert.Equal(ArticleIndexRecordCodec.RecordLength, engine.ProcessLocalIndexReservedBytes);
    }

    [Fact]
    public async Task SplitVolume_CheckpointReservationIsInvisibleToSegmentLedger()
    {
        using var dir = TempStorageDir.Create();
        var segmentReader = new MutableCapacityReader(total: 1_000_000, used: 0);
        var controlReader = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: segmentReader,
            volumeProbe: ScriptedVolumeProbe.SplitVolumes(dir),
            controlCapacityReader: controlReader);
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(CreateRecord("<vol-ctl@seg.test>"), CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);

        var controlDuring = 0L;
        var segmentDuring = 0L;
        var pressureDuring = 0L;
        engine.Journal.CheckpointTestFault = point =>
        {
            if (point != FileArticleJournal.CheckpointFaultPoint.AfterTempFlushed)
            {
                return;
            }

            controlDuring = engine.ProcessLocalCheckpointReservedBytes;
            segmentDuring = engine.SegmentCapacity!.WithLedger(static ledger => ledger.CheckpointReservedBytes);
            pressureDuring = engine.ObserveCapacityAdmissionPressure().CheckpointReservedBytes;
        };

        Assert.True(engine.CheckpointTruncateCommitted() > 0);
        Assert.True(controlDuring > 0);
        Assert.Equal(0, segmentDuring);
        Assert.Equal(0, pressureDuring);
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
    }

    [Fact]
    public async Task SameVolume_ArticleAndCheckpointReservationsAccumulate()
    {
        using var dir = TempStorageDir.Create();
        var reader = new MutableCapacityReader(total: 1_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: reader,
            volumeProbe: ScriptedVolumeProbe.SameVolume(dir));
        engine.SuspendBackgroundPersist = true;
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(CreateRecord("<vol-both-live@seg.test>"), CancellationToken.None)).Outcome);
        engine.SuspendBackgroundPersist = false;
        await engine.DrainPendingAsync(CancellationToken.None);
        engine.SuspendBackgroundPersist = true;
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(CreateRecord("<vol-both-hold@seg.test>"), CancellationToken.None)).Outcome);

        var articleDuring = 0L;
        var journalDuring = 0L;
        var indexDuring = 0L;
        var checkpointDuring = 0L;
        var totalDuring = 0L;
        engine.Journal.CheckpointTestFault = point =>
        {
            if (point != FileArticleJournal.CheckpointFaultPoint.AfterTempFlushed)
            {
                return;
            }

            articleDuring = engine.ProcessLocalArticleReservedBytes;
            journalDuring = engine.ProcessLocalJournalReservedBytes;
            indexDuring = engine.ProcessLocalIndexReservedBytes;
            checkpointDuring = engine.ProcessLocalCheckpointReservedBytes;
            totalDuring = engine.SegmentCapacity!.WithLedger(static ledger => ledger.ReservedBytes);
        };

        Assert.True(engine.CheckpointTruncateCommitted() > 0);
        Assert.True(articleDuring > 0);
        Assert.True(journalDuring > 0);
        Assert.True(indexDuring > 0);
        Assert.True(checkpointDuring > 0);
        Assert.Equal(articleDuring + journalDuring + indexDuring + checkpointDuring, totalDuring);
        Assert.Same(engine.SegmentCapacity, engine.ControlCapacity);
    }

    [Fact]
    public async Task SameVolume_CompactionAndCheckpointReservationsAccumulate()
    {
        using var dir = TempStorageDir.Create();
        var reader = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: reader,
            volumeProbe: ScriptedVolumeProbe.SameVolume(dir));
        var record = CreateRecord("<vol-cmp@seg.test>");
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var info));
        var compactionId = engine.Journal.AllocateCompactionId();
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionBeginAsync(
                new JournalCompactionBeginRecord(1, compactionId, sourceId, info.Generation),
                CancellationToken.None));

        var articleDuring = 0L;
        var journalDuring = 0L;
        var indexDuring = 0L;
        var compactionDuring = 0L;
        var compactionJournalDuring = 0L;
        var checkpointDuring = 0L;
        var totalDuring = 0L;
        engine.Journal.CheckpointTestFault = point =>
        {
            if (point != FileArticleJournal.CheckpointFaultPoint.AfterTempFlushed)
            {
                return;
            }

            articleDuring = engine.ProcessLocalArticleReservedBytes;
            journalDuring = engine.ProcessLocalJournalReservedBytes;
            indexDuring = engine.ProcessLocalIndexReservedBytes;
            compactionDuring = engine.ProcessLocalCompactionReservedBytes;
            compactionJournalDuring = engine.ProcessLocalCompactionJournalReservedBytes;
            checkpointDuring = engine.ProcessLocalCheckpointReservedBytes;
            totalDuring = engine.SegmentCapacity!.WithLedger(static ledger => ledger.ReservedBytes);
        };
        engine.TestHookAfterCompactionCapacityReserved = () =>
        {
            Assert.True(engine.CheckpointTruncateCommitted() > 0);
        };

        var relocated = await engine.RelocateArticleAsync(
            compactionId,
            1,
            sourceId,
            info.Generation,
            record.ArtId,
            CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.Relocated, relocated.Outcome);
        Assert.True(articleDuring > 0);
        Assert.True(journalDuring > 0);
        Assert.True(indexDuring > 0);
        Assert.True(compactionDuring > 0);
        Assert.True(checkpointDuring > 0);
        Assert.Equal(
            articleDuring + journalDuring + indexDuring + compactionDuring + compactionJournalDuring + checkpointDuring,
            totalDuring);
        Assert.Equal(0, engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
    }

    [Fact]
    public void CapacityDisabled_DoesNotResolveVolumes()
    {
        using var dir = TempStorageDir.Create();
        var probe = ScriptedVolumeProbe.SameVolume(dir);
        probe.ThrowOnResolve = true;
        using var engine = FileArticleStorageEngine.Open(dir.Options, volumeProbe: probe);

        Assert.Equal(0, probe.Calls);
        Assert.Null(engine.SegmentCapacity);
        Assert.Null(engine.ControlCapacity);
        Assert.Equal(0, engine.ProcessLocalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
    }

    [Fact]
    public void UnresolvableVolume_FailsClosedWhenCapacityEnabled()
    {
        using var dir = TempStorageDir.Create();
        var probe = ScriptedVolumeProbe.SameVolume(dir);
        probe.Fail = true;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FileArticleStorageEngine.Open(
                WithCapacity(dir.Options),
                capacityReader: new MutableCapacityReader(total: 1_000, used: 0),
                volumeProbe: probe));
        Assert.Contains("cannot be resolved", ex.Message, StringComparison.Ordinal);
        Assert.True(probe.Calls >= 1);
    }

    [Fact]
    public void OsProbe_DirectoriesOnTheSameDrive_ShareIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "vectornntp-vol-" + Guid.NewGuid().ToString("N"));
        var control = Path.Combine(root, "control");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(control);
        Directory.CreateDirectory(cache);
        try
        {
            Assert.True(OsStorageVolumeProbe.Shared.TryResolve(control, out var left));
            Assert.True(OsStorageVolumeProbe.Shared.TryResolve(cache, out var right));
            Assert.Equal(left, right);
            Assert.False(string.IsNullOrWhiteSpace(left.Value));
            Assert.False(OsStorageVolumeProbe.Shared.TryResolve(" ", out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ArticleStorageRuntimeOptions WithCapacity(ArticleStorageRuntimeOptions options) =>
        options with { CapacityAdmissionEnabled = true };

    private static ArticleRecord CreateRecord(string messageId)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: volume\r\n");
        _ = builder.Append("\r\nline1\r\n");
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class ScriptedVolumeProbe : IStorageVolumeProbe
    {
        private readonly string _segmentDir;
        private readonly string _controlDir;
        private readonly bool _same;

        private ScriptedVolumeProbe(string segmentDir, string controlDir, bool same)
        {
            _segmentDir = Path.GetFullPath(segmentDir);
            _controlDir = Path.GetFullPath(controlDir);
            _same = same;
        }

        public int Calls { get; private set; }

        public bool Fail { get; set; }

        public bool ThrowOnResolve { get; set; }

        public static ScriptedVolumeProbe SameVolume(TempStorageDir dir) =>
            new(dir.Options.SegmentDir, dir.Options.ControlDir, same: true);

        public static ScriptedVolumeProbe SplitVolumes(TempStorageDir dir) =>
            new(dir.Options.SegmentDir, dir.Options.ControlDir, same: false);

        public bool TryResolve(string directoryPath, out StorageVolumeIdentity identity)
        {
            Calls++;
            identity = default;
            if (ThrowOnResolve)
            {
                throw new InvalidOperationException("Volume probe must not run while capacity is disabled.");
            }

            if (Fail)
            {
                return false;
            }

            var full = Path.GetFullPath(directoryPath);
            if (string.Equals(full, _segmentDir, StringComparison.OrdinalIgnoreCase))
            {
                identity = new StorageVolumeIdentity("volume-segment");
                return true;
            }

            if (string.Equals(full, _controlDir, StringComparison.OrdinalIgnoreCase))
            {
                identity = new StorageVolumeIdentity(_same ? "volume-segment" : "volume-control");
                return true;
            }

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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-volcap-" + Guid.NewGuid().ToString("N"));
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
