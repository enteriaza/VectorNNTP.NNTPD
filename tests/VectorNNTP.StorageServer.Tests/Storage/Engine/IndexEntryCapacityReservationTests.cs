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

/// <summary>Phase 2C durable Present index-frame reservations.</summary>
public sealed class IndexEntryCapacityReservationTests
{
    private const long IndexBytes = 88;

    [Fact]
    public void Present_frame_is_88_bytes()
    {
        Assert.Equal(IndexBytes, ArticleIndexRecordCodec.RecordLength);
    }

    [Fact]
    public async Task Present_reserves_88_bytes_and_later_stages_keep_it()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<idx-present@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(0, engine.ProcessLocalIndexFrameCount);

        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.AfterIndexCommit;
        _ = await Assert.ThrowsAsync<IOException>(() => engine.RecoverAsync(CancellationToken.None));
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(1, engine.ProcessLocalIndexFrameCount);

        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.AfterIndexCommitted;
        _ = await Assert.ThrowsAsync<IOException>(() => engine.RecoverAsync(CancellationToken.None));
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(1, engine.ProcessLocalIndexFrameCount);

        await engine.RecoverAsync(CancellationToken.None);
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(1, engine.ProcessLocalIndexFrameCount);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Duplicate_present_does_not_double_reserve()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        var record = CreateRecord("<idx-dup@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);

        Assert.Equal(ArticleAcceptOutcome.Duplicate, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(1, engine.ProcessLocalIndexFrameCount);
    }

    [Fact]
    public async Task Retry_before_Present_reuses_one_reservation()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<idx-retry-before@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeIndexCommit;
        _ = await Assert.ThrowsAsync<IOException>(() => engine.RecoverAsync(CancellationToken.None));
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(0, engine.ProcessLocalIndexFrameCount);

        await engine.RecoverAsync(CancellationToken.None);
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(1, engine.ProcessLocalIndexFrameCount);
    }

    [Fact]
    public async Task Present_append_failure_before_write_releases_only_that_reservation()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<idx-append-fail@seg.test>");
        var journal = JournalBytes(record);
        var segment = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        engine.Index.TestBeforeDurableAppend = () => throw new IOException("index-before-write");

        _ = await Assert.ThrowsAsync<IOException>(() => engine.RecoverAsync(CancellationToken.None));
        Assert.Equal(0, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(journal, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(segment, engine.ProcessLocalArticleReservedBytes);

        engine.Index.TestBeforeDurableAppend = null;
        await engine.RecoverAsync(CancellationToken.None);
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(1, engine.ProcessLocalIndexFrameCount);
        Assert.Equal(journal, engine.ProcessLocalJournalReservedBytes);
    }

    [Fact]
    public async Task NonRetryable_failure_before_Present_releases_the_unbound_index_reservation()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        engine.TestPersistRetryDelay = TimeSpan.FromHours(1);
        engine.TestPersistFaultExceptionKind = FileArticleStorageEngine.PersistFaultExceptionKind.InvalidOperation;
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeIndexCommit;
        var record = CreateRecord("<idx-nr@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);

        await WaitUntilAsync(() => engine.PersistBlockedRetryScheduledCount >= 1);
        Assert.Equal(0, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(JournalBytes(record), engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize), engine.ProcessLocalArticleReservedBytes);
    }

    [Fact]
    public async Task Relocation_keeps_both_physical_present_frames()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, headroom: 0.10);
        var record = CreateRecord("<idx-reloc@seg.test>");
        var (compactionId, sourceId, generation) = await PrepareRelocationAsync(engine, record);
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);

        var relocated = await engine.RelocateArticleAsync(
            compactionId,
            1,
            sourceId,
            generation,
            record.ArtId,
            CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.Relocated, relocated.Outcome);
        Assert.Equal(IndexBytes * 2, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(2, engine.ProcessLocalIndexFrameCount);

        Assert.True(engine.Journal.CheckpointTruncateCommitted() >= 0);
        Assert.Equal(IndexBytes * 2, engine.ProcessLocalIndexReservedBytes);
    }

    [Fact]
    public async Task Index_checkpoint_releases_only_retired_present_frames()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, headroom: 0.10);
        var kept = CreateRecord("<idx-keep@seg.test>");
        var doubled = CreateRecord("<idx-double@seg.test>");
        _ = await engine.AcceptAsync(kept, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        var (compactionId, sourceId, generation) = await PrepareRelocationAsync(engine, doubled);

        _ = await engine.RelocateArticleAsync(
            compactionId,
            1,
            sourceId,
            generation,
            doubled.ArtId,
            CancellationToken.None);
        Assert.Equal(IndexBytes * 3, engine.ProcessLocalIndexReservedBytes);

        _ = engine.Index.Checkpoint();
        Assert.Equal(IndexBytes * 2, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(2, engine.ProcessLocalIndexFrameCount);

        _ = engine.Index.Checkpoint();
        Assert.Equal(IndexBytes * 2, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(2, engine.ProcessLocalIndexFrameCount);
    }

    [Fact]
    public async Task Index_checkpoint_that_retains_every_present_frame_releases_none()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        var record = CreateRecord("<idx-keep-all@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);

        _ = engine.Index.Checkpoint();
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(1, engine.ProcessLocalIndexFrameCount);

        _ = engine.Index.Checkpoint();
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);
    }

    [Fact]
    public async Task Evict_reserves_a_second_frame_until_checkpoint_retires_the_prefix()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        var record = CreateRecord("<idx-evict@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        var lengthBefore = engine.Index.DurableLength;
        Assert.True(engine.TryEvict(record.ArtId));
        Assert.Equal(IndexBytes * 2, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(2, engine.ProcessLocalIndexFrameCount);
        Assert.Equal(lengthBefore + IndexBytes, engine.Index.DurableLength);

        _ = engine.Index.Checkpoint();
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(1, engine.ProcessLocalIndexFrameCount);
        Assert.Equal(ArticleIndexDeltaFile.HeaderLength, engine.Index.DurableLength);

        _ = engine.Index.Checkpoint();
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(1, engine.ProcessLocalIndexFrameCount);
    }

    [Fact]
    public async Task Invalidate_reserves_a_second_frame()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        var record = CreateRecord("<idx-invalid@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.TryInvalidate(record.ArtId));
        Assert.Equal(IndexBytes * 2, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(2, engine.ProcessLocalIndexFrameCount);
        Assert.Equal(IndexBytes * 2, engine.Index.DurableLength);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Invalid, meta.State);
    }

    [Fact]
    public async Task Evicted_exact_fit_does_not_append()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<idx-evict-fit@seg.test>");
        var admit = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize) + JournalBytes(record) + IndexBytes;
        const long total = 10_000;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, 0.80);
        var capacity = new MutableCapacityReader(total, used: ceiling - admit);
        await using var engine = Open(dir, capacity, maximumUtilization: 0.80);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        var length = engine.Index.DurableLength;

        Assert.False(engine.TryEvict(record.ArtId));
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.Equal(length, engine.Index.DurableLength);
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(1, engine.ProcessLocalIndexFrameCount);
        Assert.Equal(ceiling - admit, capacity.UsedBytes);
        Assert.True(capacity.UsedBytes <= ceiling);
    }

    [Fact]
    public async Task Invalid_exact_fit_does_not_append()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<idx-invalid-fit@seg.test>");
        var admit = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize) + JournalBytes(record) + IndexBytes;
        const long total = 10_000;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, 0.80);
        var capacity = new MutableCapacityReader(total, used: ceiling - admit);
        await using var engine = Open(dir, capacity, maximumUtilization: 0.80);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        var length = engine.Index.DurableLength;

        Assert.False(engine.TryInvalidate(record.ArtId));
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.Equal(length, engine.Index.DurableLength);
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(ceiling - admit, capacity.UsedBytes);
    }

    [Fact]
    public async Task Evicted_append_failure_releases_only_that_reservation()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        var record = CreateRecord("<idx-evict-fail@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        var length = engine.Index.DurableLength;
        engine.Index.TestBeforeDurableAppend = () => throw new IOException("evict-before-write");

        _ = Assert.Throws<IOException>(() => engine.TryEvict(record.ArtId));
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(1, engine.ProcessLocalIndexFrameCount);
        Assert.Equal(length, engine.Index.DurableLength);
        Assert.Equal(JournalBytes(record), engine.ProcessLocalJournalReservedBytes);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
    }

    [Fact]
    public async Task Invalid_append_failure_releases_only_that_reservation()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        var record = CreateRecord("<idx-invalid-fail@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        var length = engine.Index.DurableLength;
        engine.Index.TestBeforeDurableAppend = () => throw new IOException("invalid-before-write");

        _ = Assert.Throws<IOException>(() => engine.TryInvalidate(record.ArtId));
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(1, engine.ProcessLocalIndexFrameCount);
        Assert.Equal(length, engine.Index.DurableLength);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
    }

    [Fact]
    public async Task Repeated_state_transitions_reserve_every_physical_frame()
    {
        using var dir = TempStorageDir.Create();
        await using (var engine = Open(dir))
        {
            var record = CreateRecord("<idx-cycle@seg.test>");
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(engine.TryEvict(record.ArtId));
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(engine.TryInvalidate(record.ArtId));
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(engine.TryEvict(record.ArtId));

            Assert.Equal(IndexBytes * 6, engine.ProcessLocalIndexReservedBytes);
            Assert.Equal(6, engine.ProcessLocalIndexFrameCount);
            Assert.Equal(IndexBytes * 6, engine.Index.DurableLength);

            _ = engine.Index.Checkpoint();
            Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);
            Assert.Equal(1, engine.ProcessLocalIndexFrameCount);
            Assert.Equal(ArticleIndexDeltaFile.HeaderLength, engine.Index.DurableLength);
        }

        await using var restarted = Open(dir);
        Assert.Equal(IndexBytes, restarted.ProcessLocalIndexReservedBytes);
        Assert.Equal(1, restarted.ProcessLocalIndexFrameCount);
    }

    [Fact]
    public async Task Restart_reconstructs_evicted_and_invalid_frames()
    {
        using var dir = TempStorageDir.Create();
        var evicted = CreateRecord("<idx-re-evict@seg.test>");
        var invalid = CreateRecord("<idx-re-invalid@seg.test>");
        await using (var engine = Open(dir))
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(evicted, CancellationToken.None)).Outcome);
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(invalid, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(engine.TryEvict(evicted.ArtId));
            Assert.True(engine.TryInvalidate(invalid.ArtId));
            Assert.Equal(IndexBytes * 4, engine.ProcessLocalIndexReservedBytes);
        }

        await using var restarted = Open(dir);
        Assert.Equal(IndexBytes * 4, restarted.ProcessLocalIndexReservedBytes);
        Assert.Equal(4, restarted.ProcessLocalIndexFrameCount);
    }

    [Fact]
    public async Task Evict_installation_failure_retains_both_frame_reservations()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        var record = CreateRecord("<idx-evict-install@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(record.ArtId));
        engine.Index.TestBeforeReplacementInstall = () => throw new IOException("replace");

        _ = Assert.Throws<IOException>(() => engine.Index.Checkpoint());
        Assert.Equal(IndexBytes * 2, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(2, engine.ProcessLocalIndexFrameCount);
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
    }

    [Fact]
    public async Task Split_volume_evict_reserves_the_second_frame_on_the_control_ledger()
    {
        using var dir = TempStorageDir.Create();
        var segmentReader = new MutableCapacityReader(total: 10_000_000, used: 0);
        var controlReader = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            volumeProbe: ScriptedVolumeProbe.Split(dir),
            capacityReader: segmentReader,
            controlCapacityReader: controlReader);
        var record = CreateRecord("<idx-split-evict@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(record.ArtId));

        Assert.Equal(0, engine.SegmentCapacity!.WithLedger(static ledger => ledger.IndexReservedBytes));
        Assert.Equal(IndexBytes * 2, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(
            JournalBytes(record) + (IndexBytes * 2),
            engine.ControlCapacity!.WithLedger(static ledger => ledger.ReservedBytes));
    }

    [Fact]
    public async Task Capacity_disabled_evict_reserves_nothing()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(
            dir.Options with { CapacityAdmissionEnabled = false });
        var record = CreateRecord("<idx-evict-off@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(record.ArtId));
        Assert.Equal(0, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(0, engine.ProcessLocalIndexFrameCount);
    }

    [Fact]
    public async Task Index_checkpoint_temp_failure_releases_only_the_temporary_reservation()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        var record = CreateRecord("<idx-temp-fail@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        engine.Index.TestBeforeSnapshotFlush = () => throw new IOException("snapshot-temp");

        _ = Assert.Throws<IOException>(() => engine.Index.Checkpoint());
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
    }

    [Fact]
    public async Task Index_installation_failure_retains_present_reservations()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir);
        var record = CreateRecord("<idx-install-fail@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        engine.Index.TestBeforeReplacementInstall = () => throw new IOException("replace");

        _ = Assert.Throws<IOException>(() => engine.Index.Checkpoint());
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(1, engine.ProcessLocalIndexFrameCount);
        Assert.Equal(0, engine.ProcessLocalCheckpointReservedBytes);
    }

    [Fact]
    public async Task Restart_reconstructs_present_frames_and_omits_retired_history()
    {
        using var dir = TempStorageDir.Create();
        var kept = CreateRecord("<idx-restart-keep@seg.test>");
        var retired = CreateRecord("<idx-restart-old@seg.test>");
        await using (var engineA = Open(dir, headroom: 0.10))
        {
            var (compactionId, sourceId, generation) = await PrepareRelocationAsync(engineA, retired);
            _ = await engineA.RelocateArticleAsync(
                compactionId,
                1,
                sourceId,
                generation,
                retired.ArtId,
                CancellationToken.None);
            _ = await engineA.AcceptAsync(kept, CancellationToken.None);
            await engineA.DrainPendingAsync(CancellationToken.None);
            Assert.Equal(IndexBytes * 3, engineA.ProcessLocalIndexReservedBytes);
            _ = engineA.Index.Checkpoint();
            Assert.Equal(IndexBytes * 2, engineA.ProcessLocalIndexReservedBytes);
        }

        await using var engineB = Open(dir);
        Assert.Equal(IndexBytes * 2, engineB.ProcessLocalIndexReservedBytes);
        Assert.Equal(2, engineB.ProcessLocalIndexFrameCount);
        Assert.Equal(0, engineB.ProcessLocalSegmentCopyCount);
    }

    [Fact]
    public async Task Accept_only_recovery_has_no_index_reservation_until_Present()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<idx-accept-only@seg.test>");
        await using (var engineA = Open(dir))
        {
            engineA.SuspendBackgroundPersist = true;
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
            Assert.Equal(IndexBytes, engineA.ProcessLocalIndexReservedBytes);
            Assert.Equal(0, engineA.ProcessLocalIndexFrameCount);
        }

        await using var engineB = Open(dir);
        Assert.Equal(0, engineB.ProcessLocalIndexReservedBytes);
        Assert.Equal(JournalBytes(record), engineB.ProcessLocalJournalReservedBytes);
        engineB.SuspendBackgroundPersist = true;
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.Equal(IndexBytes, engineB.ProcessLocalIndexReservedBytes);
        Assert.Equal(1, engineB.ProcessLocalIndexFrameCount);
        Assert.Equal(1, engineB.ProcessLocalSegmentCopyCount);
    }

    [Fact]
    public async Task PhysicalWritten_recovery_reserves_the_index_frame_once()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<idx-pw@seg.test>");
        await using (var engineA = Open(dir))
        {
            engineA.SuspendBackgroundPersist = true;
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
            engineA.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.AfterPhysicalWritten;
            _ = await Assert.ThrowsAsync<IOException>(() => engineA.RecoverAsync(CancellationToken.None));
        }

        await using var engineB = Open(dir);
        Assert.Equal(0, engineB.ProcessLocalIndexReservedBytes);
        Assert.Equal(0, engineB.ProcessLocalSegmentCopyCount);
        Assert.Equal(JournalBytes(record), engineB.ProcessLocalJournalReservedBytes);
        engineB.SuspendBackgroundPersist = true;
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.Equal(IndexBytes, engineB.ProcessLocalIndexReservedBytes);
        Assert.Equal(1, engineB.ProcessLocalIndexFrameCount);
        Assert.Equal(0, engineB.ProcessLocalSegmentCopyCount);
    }

    [Fact]
    public async Task Repeated_checkpoint_relocation_and_restart_do_not_leak()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<idx-cycle@seg.test>");
        await using (var engineA = Open(dir, headroom: 0.10))
        {
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
            await engineA.DrainPendingAsync(CancellationToken.None);
            _ = engineA.Index.Checkpoint();
            Assert.Equal(IndexBytes, engineA.ProcessLocalIndexReservedBytes);

            var (compactionId, sourceId, generation) = await BeginRelocationAsync(engineA, record);
            _ = await engineA.RelocateArticleAsync(
                compactionId,
                1,
                sourceId,
                generation,
                record.ArtId,
                CancellationToken.None);
            Assert.Equal(IndexBytes * 2, engineA.ProcessLocalIndexReservedBytes);
            _ = engineA.Index.Checkpoint();
            Assert.Equal(IndexBytes, engineA.ProcessLocalIndexReservedBytes);
            _ = engineA.Index.Checkpoint();
            Assert.Equal(IndexBytes, engineA.ProcessLocalIndexReservedBytes);
        }

        await using var engineB = Open(dir);
        Assert.Equal(IndexBytes, engineB.ProcessLocalIndexReservedBytes);
        Assert.Equal(1, engineB.ProcessLocalIndexFrameCount);
        engineB.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeIndexCommit;
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.Equal(IndexBytes, engineB.ProcessLocalIndexReservedBytes);
        Assert.Equal(1, engineB.ProcessLocalIndexFrameCount);
    }

    [Fact]
    public async Task Shared_volume_accumulates_segment_journal_and_index()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            volumeProbe: ScriptedVolumeProbe.Same(dir),
            capacityReader: capacity);
        var record = CreateRecord("<idx-shared@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);

        var segment = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var journal = JournalBytes(record);
        Assert.Same(engine.SegmentCapacity, engine.ControlCapacity);
        Assert.Equal(segment + journal + IndexBytes, engine.ProcessLocalReservedBytes);
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);
    }

    [Fact]
    public async Task Split_volume_puts_journal_and_index_on_the_control_ledger()
    {
        using var dir = TempStorageDir.Create();
        var segmentReader = new MutableCapacityReader(total: 10_000_000, used: 0);
        var controlReader = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            volumeProbe: ScriptedVolumeProbe.Split(dir),
            capacityReader: segmentReader,
            controlCapacityReader: controlReader);
        var record = CreateRecord("<idx-split@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);

        var segment = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        Assert.NotSame(engine.SegmentCapacity, engine.ControlCapacity);
        Assert.Equal(segment, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, engine.SegmentCapacity!.WithLedger(static ledger => ledger.IndexReservedBytes));
        Assert.Equal(0, engine.SegmentCapacity.WithLedger(static ledger => ledger.JournalReservedBytes));
        Assert.Equal(JournalBytes(record) + IndexBytes, engine.ControlCapacity!.WithLedger(static ledger => ledger.ReservedBytes));
        Assert.Equal(IndexBytes, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(segment, engine.ProcessLocalReservedBytes);
    }

    [Fact]
    public async Task Index_reservation_that_crosses_the_ceiling_is_rejected()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<idx-ceil@seg.test>");
        var segment = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var journal = JournalBytes(record);
        const long total = 10_000;
        var ceiling = ProcessLocalCapacityLedger.ComputeCeilingBytes(total, 0.80);
        var capacity = new MutableCapacityReader(total, used: ceiling - segment - journal);
        await using var engine = Open(dir, capacity, 0.80);

        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        Assert.Equal(0, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Capacity_disabled_reserves_nothing()
    {
        using var dir = TempStorageDir.Create();
        var reader = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            dir.Options with { CapacityAdmissionEnabled = false },
            capacityReader: reader);
        var record = CreateRecord("<idx-off@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.Equal(0, engine.ProcessLocalIndexReservedBytes);
        Assert.Equal(0, engine.ProcessLocalJournalReservedBytes);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Null(engine.SegmentCapacity);
        Assert.Null(engine.ControlCapacity);
    }

    private static async Task<(ulong CompactionId, SegmentId SourceId, ulong Generation)> PrepareRelocationAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        return await BeginRelocationAsync(engine, record);
    }

    private static async Task<(ulong CompactionId, SegmentId SourceId, ulong Generation)> BeginRelocationAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record)
    {
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
        return (compactionId, sourceId, info.Generation);
    }

    private static long JournalBytes(ArticleRecord record) =>
        ArticleJournalFrameCodec.SequenceReservationBytes(record.ArtSize);

    private static FileArticleStorageEngine Open(
        TempStorageDir dir,
        MutableCapacityReader? capacity = null,
        double maximumUtilization = ArticleCapacityOptions.DefaultMaximumUtilization,
        double headroom = ArticleCapacityOptions.DefaultCompactionHeadroom) =>
        FileArticleStorageEngine.Open(
            WithCapacity(dir.Options, maximumUtilization, headroom),
            capacityReader: capacity ?? new MutableCapacityReader(total: 10_000_000, used: 0));

    private static ArticleStorageRuntimeOptions WithCapacity(
        ArticleStorageRuntimeOptions options,
        double maximumUtilization = ArticleCapacityOptions.DefaultMaximumUtilization,
        double headroom = ArticleCapacityOptions.DefaultCompactionHeadroom) =>
        options with
        {
            CapacityAdmissionEnabled = true,
            CapacityMaximumUtilization = maximumUtilization,
            CapacityCompactionHeadroom = headroom,
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
        _ = builder.Append("Subject: index-reservation\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-idxcap-" + Guid.NewGuid().ToString("N"));
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
