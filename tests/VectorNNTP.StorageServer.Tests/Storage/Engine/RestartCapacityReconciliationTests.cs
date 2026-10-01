using System.Buffers.Binary;
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
/// After restart, filesystem repair finishes before the first capacity admission, and that
/// admission reads <see cref="IStorageCapacityReader"/> again. A sample taken before repair
/// or before the leftover temp is part of the reported filesystem must not be reused.
/// </summary>
public sealed class RestartCapacityReconciliationTests
{
    [Fact]
    public async Task Leftover_checkpoint_temp_is_charged_by_the_first_post_restart_admission()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<restart-leftover@seg.test>");
        var segmentBytes = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var footprint = AcceptFootprint(record);
        var leftoverBytes = footprint + 64;
        var ceiling = AlignUtilizationCeiling(footprint, footprint + leftoverBytes);
        var total = TotalForCeiling(ceiling);
        var tempPath = Path.Combine(dir.Options.ControlDir, FileArticleIndex.SnapshotTempFileName);
        var payload = new byte[leftoverBytes];
        payload[0] = 0xA5;
        await using (var stream = new FileStream(
            tempPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.None))
        {
            await stream.WriteAsync(payload);
            stream.Flush(flushToDisk: true);
        }

        Assert.Equal(leftoverBytes, new FileInfo(tempPath).Length);
        Assert.Equal(ceiling, ProcessLocalCapacityLedger.ComputeCeilingBytes(total, Utilization));
        Assert.True(Fits(usedBytes: 0, total, footprint));
        Assert.False(Fits(leftoverBytes, total, segmentBytes));

        var reader = new LiveFileCapacityReader(total, tempPath);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            volumeProbe: SameVolume(dir),
            capacityReader: reader);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(leftoverBytes, new FileInfo(tempPath).Length);
        Assert.False(File.Exists(Path.Combine(dir.Options.ControlDir, FileArticleIndex.SnapshotFileName)));
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(0, engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(0, engine.ProcessLocalCompactionJournalReservedBytes);

        engine.SuspendBackgroundPersist = true;
        var samplesBeforeAdmission = reader.SampleCount;
        var admitted = await engine.AcceptAsync(record, CancellationToken.None);

        Assert.True(reader.SampleCount > samplesBeforeAdmission);
        Assert.Equal(leftoverBytes, reader.SampleAt(samplesBeforeAdmission));
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, admitted.Outcome);
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
        Assert.Equal(leftoverBytes, new FileInfo(tempPath).Length);
    }

    [Fact]
    public async Task Active_tail_repair_is_visible_to_the_first_capacity_admission()
    {
        using var dir = TempStorageDir.Create();
        var seeded = CreateRecord("<restart-tail-seed@seg.test>");
        using (var store = FileSegmentStore.Open(dir.Options))
        {
            var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
            _ = await appender.AppendAsync(seeded.ArtData.ToArray(), CancellationToken.None);
        }

        var activePath = Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.active").Single();
        var valid = await File.ReadAllBytesAsync(activePath);
        const int tornBytes = 16;
        var torn = new byte[valid.Length + tornBytes];
        valid.CopyTo(torn, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(torn.AsSpan(valid.Length), 10_000);
        await File.WriteAllBytesAsync(activePath, torn);
        Assert.Equal(torn.Length, new FileInfo(activePath).Length);

        var probe = CreateRecord("<restart-tail-probe@seg.test>");
        var footprint = AcceptFootprint(probe);
        var repairedUsed = valid.Length;
        var tornUsed = torn.Length;
        var ceiling = AlignUtilizationCeiling(repairedUsed + footprint, tornUsed + footprint);
        var total = TotalForCeiling(ceiling);
        Assert.Equal(ceiling, ProcessLocalCapacityLedger.ComputeCeilingBytes(total, Utilization));
        Assert.True(Fits(repairedUsed, total, footprint));
        Assert.False(Fits(tornUsed, total, footprint));

        var reader = new LiveFileCapacityReader(total, activePath);
        var beforeRepair = reader.Read();
        Assert.Equal(tornUsed, beforeRepair.UsedBytes);

        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            volumeProbe: SameVolume(dir),
            capacityReader: reader);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(repairedUsed, new FileInfo(activePath).Length);
        Assert.True(repairedUsed < tornUsed);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(0, engine.ProcessLocalCompactionReservedBytes);
        Assert.Equal(0, engine.ProcessLocalCompactionJournalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);

        engine.SuspendBackgroundPersist = true;
        var samplesBeforeAdmission = reader.SampleCount;
        var admitted = await engine.AcceptAsync(probe, CancellationToken.None);

        Assert.True(reader.SampleCount > samplesBeforeAdmission);
        Assert.Equal(repairedUsed, reader.SampleAt(samplesBeforeAdmission));
        Assert.True(reader.SampleAt(samplesBeforeAdmission) < beforeRepair.UsedBytes);
        Assert.Equal(ArticleAcceptOutcome.Accepted, admitted.Outcome);
    }

    private const int Utilization = 80;

    private static bool Fits(long usedBytes, long totalBytes, long requiredBytes) =>
        ProcessLocalCapacityLedger.WouldFit(
            usedBytes,
            articleReservedBytes: 0,
            compactionReservedBytes: 0,
            totalBytes,
            requiredBytes,
            Utilization);

    private static long AcceptFootprint(ArticleRecord record) =>
        SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize)
        + ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize)
        + ArticleIndexRecordCodec.RecordLength;

    /// <summary>
    /// Smallest multiple of four at or above <paramref name="minimumInclusive"/> and still
    /// below <paramref name="exclusiveMaximum"/>. <c>0.80 × total</c> is exact for that ceiling.
    /// </summary>
    private static long AlignUtilizationCeiling(long minimumInclusive, long exclusiveMaximum)
    {
        var ceiling = minimumInclusive;
        var remainder = ceiling % 4;
        if (remainder != 0)
        {
            ceiling += 4 - remainder;
        }

        if (ceiling >= exclusiveMaximum)
        {
            throw new InvalidOperationException(
                $"Cannot place a {Utilization} ceiling in [{minimumInclusive}, {exclusiveMaximum}).");
        }

        return ceiling;
    }

    private static long TotalForCeiling(long ceiling)
    {
        if (ceiling % 4 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ceiling), ceiling, "Ceiling must be divisible by 4.");
        }

        return ceiling / 4 * 5;
    }

    private static ArticleStorageRuntimeOptions WithCapacity(ArticleStorageRuntimeOptions options) =>
        options with
        {
            CapacityMaximumUtilization = Utilization,
            CapacityCompactionHeadroom = ArticleCapacityOptions.DefaultCompactionHeadroom,
        };

    private static ArticleRecord CreateRecord(string messageId)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: restart-capacity\r\n");
        _ = builder.Append("\r\nline1\r\nline2\r\n");
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private static SameVolumeProbe SameVolume(TempStorageDir dir) => new(dir.Options.SegmentDir, dir.Options.ControlDir);

    /// <summary>
    /// Reports the live length of one file as <see cref="StorageCapacitySnapshot.UsedBytes"/>.
    /// A cached sample cannot follow a later truncate or keep ignoring a file that is still present.
    /// </summary>
    private sealed class LiveFileCapacityReader(long totalBytes, string accountedPath) : IStorageCapacityReader
    {
        private readonly object _gate = new();
        private readonly List<long> _samples = [];

        public int SampleCount
        {
            get
            {
                lock (_gate)
                {
                    return _samples.Count;
                }
            }
        }

        public long SampleAt(int index)
        {
            lock (_gate)
            {
                return _samples[index];
            }
        }

        public StorageCapacitySnapshot Read()
        {
            var used = new FileInfo(accountedPath).Length;
            lock (_gate)
            {
                _samples.Add(used);
            }

            return new StorageCapacitySnapshot(totalBytes, used, Math.Max(0L, totalBytes - used));
        }
    }

    private sealed class SameVolumeProbe(string segmentDir, string controlDir) : IStorageVolumeProbe
    {
        private readonly string _segmentDir = Path.GetFullPath(segmentDir);
        private readonly string _controlDir = Path.GetFullPath(controlDir);

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

    private sealed class TempStorageDir : IDisposable
    {
        private TempStorageDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempStorageDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-restart-cap-" + Guid.NewGuid().ToString("N"));
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

        private string Root { get; }
    }
}
