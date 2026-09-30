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
/// Journal-sequence reservations are ArtSize+132 on the control ledger until checkpoint omits them.
/// </summary>
public sealed class JournalSequenceCapacityReservationTests
{
    [Fact]
    public void SequenceReservationBytes_matches_encoded_frames()
    {
        var record = CreateRecord("<jr-len@seg.test>");
        var accept = new JournalAcceptRecord(
            1,
            7,
            record.ArtId,
            record.ArtHash,
            record.ArtSize,
            DateTimeOffset.UnixEpoch,
            record.ArtData);
        var physical = ArticleJournalFrameCodec.EncodePhysicalWritten(
            new JournalPhysicalWrittenRecord(1, 7, new StoredArticleLocation(new SegmentId(1), 0, record.ArtSize)));
        var committed = ArticleJournalFrameCodec.EncodeIndexCommitted(new JournalIndexCommittedRecord(1, 7));
        var reserved = ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize);
        Assert.Equal(record.ArtSize + 132, reserved);
        Assert.Equal(
            ArticleJournalFrameCodec.EncodeAccept(accept).Length + physical.Length + committed.Length,
            reserved);
    }

    [Fact]
    public async Task Accept_reserves_journal_sequence_on_the_control_ledger()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<jr-accept@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);

        Assert.Equal(JournalBytes(record), engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(1, engine.ProcessLocalJournalReservationCount);
        Assert.Equal(SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize), engine.ProcessLocalArticleReservedBytes);
    }

    [Fact]
    public async Task PhysicalWritten_and_IndexCommitted_do_not_release_the_journal_reservation()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<jr-pw@seg.test>");
        var journal = JournalBytes(record);
        _ = await engine.AcceptAsync(record, CancellationToken.None);

        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.AfterPhysicalWritten;
        _ = await Assert.ThrowsAsync<IOException>(() => engine.RecoverAsync(CancellationToken.None));
        Assert.NotNull(Assert.Single(engine.Journal.EnumerateIncomplete()).PhysicalWritten);
        Assert.Equal(journal, engine.ProcessLocalJournalReservedBytes);

        await engine.RecoverAsync(CancellationToken.None);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(journal, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(1, engine.ProcessLocalJournalReservationCount);
    }

    [Fact]
    public async Task Multiple_accepts_accumulate_independent_sequence_reservations()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        engine.SuspendBackgroundPersist = true;
        var first = CreateRecord("<jr-a@seg.test>");
        var second = CreateRecord("<jr-b@seg.test>", "other\r\n");
        _ = await engine.AcceptAsync(first, CancellationToken.None);
        _ = await engine.AcceptAsync(second, CancellationToken.None);

        Assert.Equal(2, engine.ProcessLocalJournalReservationCount);
        Assert.Equal(JournalBytes(first) + JournalBytes(second), engine.ProcessLocalJournalReservedBytes);
    }

    [Fact]
    public async Task Retry_reuses_the_original_journal_reservation()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<jr-retry@seg.test>");
        var journal = JournalBytes(record);
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;
        _ = await Assert.ThrowsAsync<IOException>(() => engine.RecoverAsync(CancellationToken.None));
        Assert.Equal(journal, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(1, engine.ProcessLocalJournalReservationCount);

        await engine.RecoverAsync(CancellationToken.None);
        Assert.Equal(journal, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(1, engine.ProcessLocalJournalReservationCount);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task NonRetryable_failure_retains_the_durable_journal_reservation()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        engine.TestPersistRetryDelay = TimeSpan.FromHours(1);
        engine.TestPersistFaultExceptionKind = FileArticleStorageEngine.PersistFaultExceptionKind.InvalidOperation;
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;
        var record = CreateRecord("<jr-nr@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);

        await WaitUntilAsync(() => engine.PersistBlockedRetryScheduledCount >= 1);
        Assert.Equal(JournalBytes(record), engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(1, engine.ProcessLocalJournalReservationCount);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Single(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task AcceptOnly_recovery_reconstructs_one_journal_reservation()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<jr-recover-accept@seg.test>");
        var journal = JournalBytes(record);
        await using (var engineA = Open(dir))
        {
            engineA.SuspendBackgroundPersist = true;
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
        }

        await using var engineB = Open(dir);
        Assert.Equal(journal, engineB.ProcessLocalJournalReservedBytes);
        Assert.Equal(1, engineB.ProcessLocalJournalReservationCount);
        Assert.Equal(0, engineB.ProcessLocalSegmentCopyCount);
        engineB.SuspendBackgroundPersist = true;
        await engineB.RecoverAsync(CancellationToken.None);

        Assert.Equal(journal, engineB.ProcessLocalJournalReservedBytes);
        Assert.Equal(1, engineB.ProcessLocalJournalReservationCount);
        Assert.Equal(1, engineB.ProcessLocalSegmentCopyCount);
        Assert.Equal(1, engineB.PhysicalAppendCount);
    }

    [Fact]
    public async Task PhysicalWritten_recovery_reconstructs_exactly_one_journal_reservation()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<jr-recover-pw@seg.test>");
        var journal = JournalBytes(record);
        await using (var engineA = Open(dir))
        {
            engineA.SuspendBackgroundPersist = true;
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
            engineA.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.AfterPhysicalWritten;
            _ = await Assert.ThrowsAsync<IOException>(() => engineA.RecoverAsync(CancellationToken.None));
        }

        await using var engineB = Open(dir);
        Assert.Equal(journal, engineB.ProcessLocalJournalReservedBytes);
        Assert.Equal(1, engineB.ProcessLocalJournalReservationCount);
        Assert.Equal(0, engineB.ProcessLocalSegmentCopyCount);
        engineB.SuspendBackgroundPersist = true;
        await engineB.RecoverAsync(CancellationToken.None);

        Assert.Equal(journal, engineB.ProcessLocalJournalReservedBytes);
        Assert.Equal(1, engineB.ProcessLocalJournalReservationCount);
        Assert.Equal(0, engineB.ProcessLocalSegmentCopyCount);
        Assert.Empty(engineB.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Checkpoint_with_no_remaining_sequences_releases_retired_reservations()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        var first = CreateRecord("<jr-ck0-a@seg.test>");
        var second = CreateRecord("<jr-ck0-b@seg.test>");
        _ = await engine.AcceptAsync(first, CancellationToken.None);
        _ = await engine.AcceptAsync(second, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.Equal(JournalBytes(first) + JournalBytes(second), engine.ProcessLocalJournalReservedBytes);

        Assert.True(engine.CheckpointTruncateCommitted() > 0);
        Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalJournalReservationCount);
        Assert.Equal(2, engine.ProcessLocalSegmentCopyCount);
        Assert.Equal(0, engine.CheckpointTruncateCommitted());
        Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
    }

    [Fact]
    public async Task Checkpoint_releases_only_omitted_sequences()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        var committed = CreateRecord("<jr-omit@seg.test>");
        var retained = CreateRecord("<jr-keep@seg.test>", "keep\r\n");
        _ = await engine.AcceptAsync(committed, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        engine.SuspendBackgroundPersist = true;
        _ = await engine.AcceptAsync(retained, CancellationToken.None);
        Assert.Equal(2, engine.ProcessLocalJournalReservationCount);

        Assert.True(engine.CheckpointTruncateCommitted() > 0);
        Assert.Equal(JournalBytes(retained), engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(1, engine.ProcessLocalJournalReservationCount);
        Assert.Single(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Checkpoint_that_retains_every_sequence_releases_none()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<jr-all@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        var compactionId = engine.Journal.AllocateCompactionId();
        var source = new SegmentId(41);
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionBeginAsync(
                new JournalCompactionBeginRecord(1, compactionId, source, 3),
                CancellationToken.None));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionCommittedAsync(
                new JournalCompactionCommittedRecord(1, compactionId),
                CancellationToken.None));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionRetiredAsync(
                new JournalCompactionRetiredRecord(1, compactionId, source, 3),
                CancellationToken.None));

        var journal = engine.ProcessLocalJournalReservedBytes;
        Assert.True(engine.CheckpointTruncateCommitted() > 0);
        Assert.Equal(journal, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(1, engine.ProcessLocalJournalReservationCount);
        Assert.Single(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Checkpoint_temp_failure_releases_only_the_temporary_reservation()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        var record = CreateRecord("<jr-temp@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        var journal = engine.ProcessLocalJournalReservedBytes;
        engine.Journal.CheckpointTestFault = point =>
        {
            if (point == FileArticleJournal.CheckpointFaultPoint.AfterTempFlushed)
            {
                throw new IOException("temp-fault");
            }
        };

        _ = await Assert.ThrowsAsync<IOException>(() => Task.Run(() => engine.CheckpointTruncateCommitted()));
        Assert.Equal(journal, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(1, engine.ProcessLocalJournalReservationCount);
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);

        engine.Journal.CheckpointTestFault = null;
        Assert.True(engine.CheckpointTruncateCommitted() > 0);
        Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
    }

    [Fact]
    public async Task Checkpoint_installation_failure_retains_sequence_reservations()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        var record = CreateRecord("<jr-install@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        var journal = engine.ProcessLocalJournalReservedBytes;
        engine.Journal.CheckpointTestFault = point =>
        {
            if (point == FileArticleJournal.CheckpointFaultPoint.AfterMoveBeforeReopen)
            {
                throw new IOException("install-fault");
            }
        };

        _ = await Assert.ThrowsAsync<IOException>(() => Task.Run(() => engine.CheckpointTruncateCommitted()));
        Assert.Equal(journal, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(1, engine.ProcessLocalJournalReservationCount);
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
    }

    [Fact]
    public async Task Restart_reconstructs_only_sequences_still_in_the_authoritative_journal()
    {
        using var dir = TempStorageDir.Create();
        var retired = CreateRecord("<jr-gone@seg.test>");
        var kept = CreateRecord("<jr-stay@seg.test>", "stay\r\n");
        await using (var engineA = Open(dir))
        {
            _ = await engineA.AcceptAsync(retired, CancellationToken.None);
            await engineA.DrainPendingAsync(CancellationToken.None);
            engineA.SuspendBackgroundPersist = true;
            _ = await engineA.AcceptAsync(kept, CancellationToken.None);
            Assert.True(engineA.CheckpointTruncateCommitted() > 0);
        }

        await using var engineB = Open(dir);
        Assert.Equal(JournalBytes(kept), engineB.ProcessLocalJournalReservedBytes);
        Assert.Equal(1, engineB.ProcessLocalJournalReservationCount);
        Assert.Equal(0, engineB.ProcessLocalSegmentCopyCount);
    }

    [Fact]
    public async Task Repeated_checkpoint_cycles_do_not_leak_or_double_release()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        for (var i = 0; i < 3; i++)
        {
            var record = CreateRecord($"<jr-cycle-{i}@seg.test>");
            _ = await engine.AcceptAsync(record, CancellationToken.None);
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.Equal(JournalBytes(record), engine.ProcessLocalJournalReservedBytes);
            Assert.True(engine.CheckpointTruncateCommitted() > 0);
            Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
            Assert.Equal(0, engine.CheckpointTruncateCommitted());
            Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
        }

        Assert.Equal(3, engine.ProcessLocalSegmentCopyCount);
    }

    [Fact]
    public async Task Shared_volume_journal_and_segment_reservations_accumulate()
    {
        using var dir = TempStorageDir.Create();
        var reader = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            volumeProbe: ScriptedVolumeProbe.Same(dir),
            capacityReader: reader);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<jr-shared@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        var segment = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var journal = JournalBytes(record);

        Assert.Same(engine.SegmentCapacity, engine.ControlCapacity);
        Assert.Equal(segment, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(journal, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(segment + journal + ArticleIndexRecordCodec.RecordLength, engine.ProcessLocalReservedBytes);
    }

    [Fact]
    public async Task Split_volume_journal_reservation_stays_on_the_control_ledger()
    {
        using var dir = TempStorageDir.Create();
        var segment = new MutableCapacityReader(total: 10_000_000, used: 0);
        var control = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            volumeProbe: ScriptedVolumeProbe.Split(dir),
            capacityReader: segment,
            controlCapacityReader: control);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<jr-split@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);

        Assert.NotSame(engine.SegmentCapacity, engine.ControlCapacity);
        Assert.Equal(SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize), engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, engine.SegmentCapacity!.WithLedger(static ledger => ledger.JournalReservedBytes));
        Assert.Equal(JournalBytes(record), engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(0, engine.ControlCapacity!.WithLedger(static ledger => ledger.ArticleReservedBytes));
        Assert.Equal(
            SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize),
            engine.ProcessLocalReservedBytes);
        Assert.Equal(
            JournalBytes(record) + ArticleIndexRecordCodec.RecordLength,
            engine.ControlCapacity.WithLedger(static ledger => ledger.ReservedBytes));
    }

    [Fact]
    public async Task Journal_reservation_that_crosses_the_ceiling_is_rejected()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<jr-ceil@seg.test>");
        var segmentBytes = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var journalBytes = JournalBytes(record);
        const long total = 10_000;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, 0.80);
        var capacity = new MutableCapacityReader(total, used: ceiling - segmentBytes);
        await using var engine = Open(dir, capacity, 0.80);

        var rejected = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, rejected.Outcome);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalReservationCount);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Capacity_disabled_reserves_nothing()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 100, used: 100);
        await using var engine = FileArticleStorageEngine.Open(
            dir.Options with { CapacityAdmissionEnabled = false },
            capacityReader: capacity);
        var record = CreateRecord("<jr-off@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Null(engine.SegmentCapacity);
        Assert.Null(engine.ControlCapacity);
        Assert.True(engine.TryRead(record.ArtId, out _));
    }

    private static long JournalBytes(ArticleRecord record) =>
        ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize);

    private static FileArticleStorageEngine Open(
        TempStorageDir dir,
        MutableCapacityReader? capacity = null,
        double maximumUtilization = ArticleCapacityOptions.DefaultMaximumUtilization) =>
        FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization),
            capacityReader: capacity ?? new MutableCapacityReader(total: 10_000_000, used: 0));

    private static ArticleStorageRuntimeOptions WithCapacity(
        ArticleStorageRuntimeOptions options,
        double maximumUtilization = ArticleCapacityOptions.DefaultMaximumUtilization) =>
        options with
        {
            CapacityAdmissionEnabled = true,
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
        _ = builder.Append("Subject: journal-sequence\r\n");
        _ = builder.Append("\r\n").Append(body);
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

        public static ScriptedVolumeProbe Same(TempStorageDir dir) =>
            new(dir.Options.SegmentDir, dir.Options.ControlDir, same: true);

        public static ScriptedVolumeProbe Split(TempStorageDir dir) =>
            new(dir.Options.SegmentDir, dir.Options.ControlDir, same: false);

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
                identity = new StorageVolumeIdentity(_same ? "volume-segment" : "volume-control");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-jrnlcap-" + Guid.NewGuid().ToString("N"));
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
