using System.Buffers.Binary;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Index rows carry the journal Accept sequence. Recovery must not let an older
/// PhysicalWritten replace a later logical state, and a newer Accept may still publish.
/// </summary>
public sealed class IndexSequenceProvenanceTests
{
    [Fact]
    public void Codec_RoundTrips_Sequence_And_Rejects_Legacy_Length()
    {
        var metadata = new StoredArticleMetadata(
            ArticleId.FromMessageId("<seq-codec@example.test>"u8),
            42,
            10,
            new StoredArticleLocation(new SegmentId(3), 8, 10),
            ArticleStorageState.Present,
            new DateTimeOffset(2024, 8, 23, 7, 30, 10, TimeSpan.Zero),
            42UL);
        var encoded = ArticleIndexRecordCodec.Encode(metadata);
        Assert.Equal(96, encoded.Length);
        Assert.Equal(ArticleIndexRecordCodec.RecordLength, encoded.Length);
        Assert.True(ArticleIndexRecordCodec.TryDecode(encoded, out var length, out var decoded, out var error));
        Assert.Equal(ArticleIndexFrameError.None, error);
        Assert.Equal(96, length);
        Assert.Equal(metadata, decoded);

        encoded[4] = 1;
        var crc = System.IO.Hashing.Crc32.HashToUInt32(encoded.AsSpan(0, encoded.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(encoded.Length - 4, 4), crc);
        Assert.False(ArticleIndexRecordCodec.TryDecode(encoded, out _, out _, out var schemaError));
        Assert.Equal(ArticleIndexFrameError.Corrupt, schemaError);

        var legacy = new byte[88];
        BinaryPrimitives.WriteUInt32LittleEndian(legacy, 88);
        Assert.False(ArticleIndexRecordCodec.TryDecode(legacy, out _, out _, out var lengthError));
        Assert.Equal(ArticleIndexFrameError.CorruptLength, lengthError);
    }

    [Fact]
    public void Legacy_88_Byte_Index_Fails_Closed()
    {
        using var dir = TempStorageDir.Create();
        var path = Path.Combine(dir.Options.ControlDir, FileArticleIndex.IndexFileName);
        var legacy = new byte[88];
        BinaryPrimitives.WriteUInt32LittleEndian(legacy, 88);
        File.WriteAllBytes(path, legacy);
        _ = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(dir.Options));
    }

    [Fact]
    public void Snapshot_And_Vnid_Tail_Restore_Sequence()
    {
        using var dir = TempStorageDir.Create();
        var row = new StoredArticleMetadata(
            ArticleId.FromMessageId("<seq-snap@example.test>"u8),
            7,
            10,
            new StoredArticleLocation(new SegmentId(1), 0, 10),
            ArticleStorageState.Evicted,
            new DateTimeOffset(2024, 8, 23, 7, 30, 10, TimeSpan.Zero),
            9UL);
        using (var index = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(index.TryCommitPresent(row with { State = ArticleStorageState.Present }));
            Assert.True(index.TrySetState(row.ArtId, ArticleStorageState.Evicted, row.LastAccessUtc));
            _ = index.WriteSnapshot();
            Assert.Equal(2u, ArticleIndexSnapshotCodec.Version);
            Assert.Equal(2u, ArticleIndexDeltaFile.Version);
            _ = index.Checkpoint();
        }

        using var reopened = FileArticleIndex.Open(dir.Options);
        Assert.True(reopened.TryGet(row.ArtId, out var restored));
        Assert.Equal(ArticleStorageState.Evicted, restored.State);
        Assert.Equal(9UL, restored.Sequence);
        Assert.Equal(row.Location, restored.Location);
    }

    [Fact]
    public async Task Present_Relocation_And_Death_Preserve_Sequence_Reaccept_Replaces_It()
    {
        using var dir = TempStorageDir.Create();
        var cache = new ArticleMemoryCache(4L * 1024 * 1024);
        await using var engine = Open(dir, cache);
        var record = CreateRecord("<seq-life@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var present));
        Assert.Equal(ArticleStorageState.Present, present.State);
        var sequence = present.Sequence;
        Assert.True(sequence >= 1);

        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var compact = await engine.CompactClosedSegmentAsync(present.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        Assert.True(engine.Index.TryGet(record.ArtId, out var moved));
        Assert.Equal(sequence, moved.Sequence);
        Assert.NotEqual(present.Location.SegmentId.Value, moved.Location.SegmentId.Value);

        Assert.True(engine.TryEvict(record.ArtId));
        var afterDeath = engine.Index.DurableLength;
        Assert.True(engine.TryEvict(record.ArtId));
        Assert.Equal(afterDeath, engine.Index.DurableLength);
        Assert.True(engine.Index.TryGet(record.ArtId, out var evicted));
        Assert.Equal(ArticleStorageState.Evicted, evicted.State);
        Assert.Equal(sequence, evicted.Sequence);
        Assert.Equal(moved.Location, evicted.Location);

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var revived));
        Assert.Equal(ArticleStorageState.Present, revived.State);
        Assert.True(revived.Sequence > sequence);
        Assert.NotEqual(evicted.Location, revived.Location);
        Assert.True(engine.ArticleCache.TryGet(record.ArtId, out _));
    }

    [Fact]
    public async Task Invalid_Preserves_Sequence_And_Reaccept_Replaces_It()
    {
        using var dir = TempStorageDir.Create();
        var cache = new ArticleMemoryCache(4L * 1024 * 1024);
        await using var engine = Open(dir, cache);
        var record = CreateRecord("<seq-inv@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var present));
        Assert.True(engine.TryInvalidate(record.ArtId));
        Assert.True(engine.TryInvalidate(record.ArtId));
        Assert.True(engine.Index.TryGet(record.ArtId, out var invalid));
        Assert.Equal(ArticleStorageState.Invalid, invalid.State);
        Assert.Equal(present.Sequence, invalid.Sequence);

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var revived));
        Assert.Equal(ArticleStorageState.Present, revived.State);
        Assert.True(revived.Sequence > invalid.Sequence);
        Assert.True(engine.ArticleCache.TryGet(record.ArtId, out _));
    }

    [Fact]
    public async Task Same_Location_Evicted_Does_Not_Publish()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new ArticleMemoryCache(4L * 1024 * 1024));
        var record = CreateRecord("<seq-evict-a@seg.test>");
        var sequence = await AcceptPresentWithoutIndexCommittedAsync(engine, record);
        Assert.True(engine.TryEvict(record.ArtId));
        Assert.True(engine.Index.TryGet(record.ArtId, out var dead));
        await FinishAndAssertDeadAsync(engine, record, sequence, ArticleStorageState.Evicted, dead.Location);
    }

    [Fact]
    public async Task Same_Location_Invalid_Does_Not_Publish()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new ArticleMemoryCache(4L * 1024 * 1024));
        var record = CreateRecord("<seq-invalid-a@seg.test>");
        var sequence = await AcceptPresentWithoutIndexCommittedAsync(engine, record);
        Assert.True(engine.TryInvalidate(record.ArtId));
        Assert.True(engine.Index.TryGet(record.ArtId, out var dead));
        await FinishAndAssertDeadAsync(engine, record, sequence, ArticleStorageState.Invalid, dead.Location);
    }

    [Fact]
    public async Task Present_Elsewhere_Keeps_Destination_Sequence()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new ArticleMemoryCache(4L * 1024 * 1024));
        var record = CreateRecord("<seq-present-b@seg.test>");
        var sequence = await AcceptPresentWithoutIndexCommittedAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var source));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var compact = await engine.CompactClosedSegmentAsync(source.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        Assert.True(engine.Index.TryGet(record.ArtId, out var moved));
        Assert.Equal(sequence, moved.Sequence);

        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.None;
        await engine.RecoverAsync(CancellationToken.None);

        Assert.True(engine.Index.TryGet(record.ArtId, out var after));
        Assert.Equal(ArticleStorageState.Present, after.State);
        Assert.Equal(moved.Location, after.Location);
        Assert.Equal(sequence, after.Sequence);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.True(engine.ArticleCache.TryGet(record.ArtId, out _));
    }

    [Fact]
    public async Task Relocated_Evicted_Is_Not_Resurrected()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new ArticleMemoryCache(4L * 1024 * 1024));
        var record = CreateRecord("<seq-rel-evict@seg.test>");
        await RecoverRelocatedDeathAsync(engine, record, evict: true);
    }

    [Fact]
    public async Task Relocated_Invalid_Is_Not_Resurrected()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new ArticleMemoryCache(4L * 1024 * 1024));
        var record = CreateRecord("<seq-rel-invalid@seg.test>");
        await RecoverRelocatedDeathAsync(engine, record, evict: false);
    }

    [Fact]
    public async Task Newer_Accept_After_Evicted_Publishes()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new ArticleMemoryCache(4L * 1024 * 1024));
        var record = CreateRecord("<seq-new-evict@seg.test>");
        await AssertNewerAcceptPublishesAsync(engine, record, evict: true);
    }

    [Fact]
    public async Task Newer_Accept_After_Invalid_Publishes()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new ArticleMemoryCache(4L * 1024 * 1024));
        var record = CreateRecord("<seq-new-invalid@seg.test>");
        await AssertNewerAcceptPublishesAsync(engine, record, evict: false);
    }

    [Fact]
    public async Task Older_PhysicalWritten_Does_Not_Move_Newer_Present_Backward()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = Open(dir, new ArticleMemoryCache(4L * 1024 * 1024));
        var record = CreateRecord("<seq-older@seg.test>");
        var sequence = await AcceptPresentWithoutIndexCommittedAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var source));
        Assert.True(engine.TryEvict(record.ArtId));
        var newerLocation = new StoredArticleLocation(new SegmentId(90), 0, source.Location.Length);
        Assert.True(engine.Index.TryCommitPresent(source with
        {
            State = ArticleStorageState.Present,
            Location = newerLocation,
            Sequence = sequence + 1,
        }));

        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.None;
        await engine.RecoverAsync(CancellationToken.None);

        Assert.True(engine.Index.TryGet(record.ArtId, out var after));
        Assert.Equal(ArticleStorageState.Present, after.State);
        Assert.Equal(newerLocation, after.Location);
        Assert.Equal(sequence + 1, after.Sequence);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task CheckpointRestart_OldPhysicalWritten_DoesNotResurrectEvicted()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<seq-ckpt-dead@seg.test>");
        StoredArticleLocation source;
        StoredArticleLocation deadLocation;
        ulong sequence;
        await using (var engine = OpenCapacity(dir, new ArticleMemoryCache(4L * 1024 * 1024)))
        {
            sequence = await AcceptPresentWithoutIndexCommittedAsync(engine, record);
            engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.None;
            Assert.True(engine.Index.TryGet(record.ArtId, out var present));
            source = present.Location;
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
            var compact = await engine.CompactClosedSegmentAsync(source.SegmentId, CancellationToken.None);
            Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
            Assert.True(engine.TryEvict(record.ArtId));
            Assert.True(engine.Index.TryGet(record.ArtId, out var evicted));
            Assert.Equal(sequence, evicted.Sequence);
            Assert.Equal(ArticleStorageState.Evicted, evicted.State);
            Assert.NotEqual(source.SegmentId.Value, evicted.Location.SegmentId.Value);
            deadLocation = evicted.Location;

            // A committed sequence that never enters the index gives checkpoint a prefix to
            // drop. S stays incomplete, and the source segment stays readable.
            await CommitUnindexedSequenceAsync(engine.Journal);
            Assert.True(engine.CheckpointTruncateCommitted() > 0);
            var indexCheckpoint = engine.Index.Checkpoint();
            Assert.Equal(1UL, indexCheckpoint.Snapshot.RecordCount);
            Assert.Equal((long)ArticleIndexDeltaFile.HeaderLength, engine.Index.DurableLength);
            AssertDurableDeath(dir, engine, record, sequence, source, deadLocation);
        }

        AssertJournalStartsWithSequenceFence(dir);
        await using (var restarted = OpenCapacity(dir, new ArticleMemoryCache(4L * 1024 * 1024)))
        {
            AssertDurableDeath(dir, restarted, record, sequence, source, deadLocation);
            var filesBefore = SegmentFiles(dir.Options.SegmentDir);
            var indexLengthBefore = restarted.Index.DurableLength;
            Assert.Equal(0, restarted.PhysicalAppendCount);
            Assert.Equal(0, restarted.ProcessLocalSegmentCopyCount);
            Assert.Equal(0, restarted.ProcessLocalReservationCount);

            await restarted.RecoverAsync(CancellationToken.None);

            Assert.True(restarted.Index.TryGet(record.ArtId, out var after));
            Assert.Equal(ArticleStorageState.Evicted, after.State);
            Assert.Equal(sequence, after.Sequence);
            Assert.Equal(deadLocation, after.Location);
            Assert.NotEqual(source, after.Location);
            Assert.Equal(indexLengthBefore, restarted.Index.DurableLength);
            Assert.Empty(restarted.Journal.EnumerateIncomplete());
            Assert.False(restarted.ArticleCache.TryGet(record.ArtId, out _));
            Assert.False(restarted.TryRead(record.ArtId, out _));
            Assert.Equal(0, restarted.PhysicalAppendCount);
            Assert.Equal(0, restarted.ProcessLocalSegmentCopyCount);
            Assert.Equal(0, restarted.ProcessLocalReservationCount);
            Assert.Equal(filesBefore, SegmentFiles(dir.Options.SegmentDir));

            Assert.True(restarted.CheckpointTruncateCommitted() > 0);
            Assert.Empty(restarted.Journal.EnumerateIncomplete());
            Assert.Equal(0, restarted.CheckpointTruncateCommitted());
        }

        await using var afterCheckpoint = OpenCapacity(dir, new ArticleMemoryCache(4L * 1024 * 1024));
        Assert.Empty(afterCheckpoint.Journal.EnumerateIncomplete());
        Assert.True(afterCheckpoint.Journal.NextSequence > sequence);
        Assert.True(afterCheckpoint.Index.TryGet(record.ArtId, out var stillDead));
        Assert.Equal(ArticleStorageState.Evicted, stillDead.State);
        Assert.Equal(sequence, stillDead.Sequence);
        Assert.Equal(deadLocation, stillDead.Location);
    }

    [Fact]
    public async Task CheckpointRestart_NewerAccept_PublishesOverCheckpointedEviction()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<seq-ckpt-revive@seg.test>");
        ulong sequence;
        StoredArticleLocation deadLocation;
        await using (var engine = Open(dir, new ArticleMemoryCache(4L * 1024 * 1024)))
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(engine.Index.TryGet(record.ArtId, out var present));
            sequence = present.Sequence;
            Assert.True(engine.TryEvict(record.ArtId));
            Assert.True(engine.CheckpointTruncateCommitted() > 0);
            var indexCheckpoint = engine.Index.Checkpoint();
            Assert.Equal(1UL, indexCheckpoint.Snapshot.RecordCount);
            Assert.True(engine.Index.TryGet(record.ArtId, out var evicted));
            Assert.Equal(ArticleStorageState.Evicted, evicted.State);
            Assert.Equal(sequence, evicted.Sequence);
            deadLocation = evicted.Location;
            Assert.Empty(engine.Journal.EnumerateIncomplete());

            engine.SuspendBackgroundPersist = true;
            engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeIndexCommit;
            engine.TestPersistFaultExceptionKind = FileArticleStorageEngine.PersistFaultExceptionKind.InvalidOperation;
            var again = await engine.AcceptAsync(record, CancellationToken.None);
            Assert.Equal(ArticleAcceptOutcome.Accepted, again.Outcome);
            Assert.True(again.Sequence > sequence);
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RecoverAsync(CancellationToken.None));
            AssertDurableRevivalPending(dir, engine, record, sequence, deadLocation, again.Sequence);
        }

        AssertJournalStartsWithSequenceFence(dir);
        await using var restarted = Open(dir, new ArticleMemoryCache(4L * 1024 * 1024));
        var pending = AssertDurableRevivalPending(dir, restarted, record, sequence, deadLocation, expectedSequence: null);
        Assert.False(restarted.ArticleCache.TryGet(record.ArtId, out _));
        var indexLengthBefore = restarted.Index.DurableLength;

        await restarted.RecoverAsync(CancellationToken.None);

        Assert.True(restarted.Index.TryGet(record.ArtId, out var revived));
        Assert.Equal(ArticleStorageState.Present, revived.State);
        Assert.Equal(pending.Sequence, revived.Sequence);
        Assert.True(revived.Sequence > sequence);
        Assert.Equal(pending.Location, revived.Location);
        Assert.NotEqual(deadLocation, revived.Location);
        Assert.Equal(indexLengthBefore + ArticleIndexRecordCodec.RecordLength, restarted.Index.DurableLength);
        Assert.Empty(restarted.Journal.EnumerateIncomplete());
        Assert.True(restarted.ArticleCache.TryGet(record.ArtId, out _));
        Assert.True(restarted.TryRead(record.ArtId, out var read));
        Assert.Equal(revived.Location, read.Metadata.Location);
        Assert.Equal(revived.Sequence, read.Metadata.Sequence);
    }

    [Fact]
    public async Task CheckpointRestart_NewAcceptSequence_ExceedsPersistedRowSequence()
    {
        using var dir = TempStorageDir.Create();
        var first = CreateRecord("<seq-ckpt-alloc-a@seg.test>");
        var second = CreateRecord("<seq-ckpt-alloc-b@seg.test>");
        ulong sequence;
        await using (var engine = Open(dir, new ArticleMemoryCache(4L * 1024 * 1024)))
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(first, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(engine.Index.TryGet(first.ArtId, out var present));
            sequence = present.Sequence;
            Assert.True(engine.CheckpointTruncateCommitted() > 0);
            var indexCheckpoint = engine.Index.Checkpoint();
            Assert.Equal(1UL, indexCheckpoint.Snapshot.RecordCount);
            var snapshot = ArticleIndexSnapshotCodec.Read(
                Path.Combine(dir.Options.ControlDir, FileArticleIndex.SnapshotFileName));
            Assert.Single(snapshot.Entries);
            Assert.Equal(sequence, snapshot.Entries[0].Sequence);
        }

        AssertJournalStartsWithSequenceFence(dir);
        await using var restarted = Open(dir, new ArticleMemoryCache(4L * 1024 * 1024));
        Assert.True(restarted.Index.TryGet(first.ArtId, out var restored));
        Assert.Equal(sequence, restored.Sequence);
        Assert.True(restarted.Journal.NextSequence > restored.Sequence);
        var accepted = await restarted.AcceptAsync(second, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.True(accepted.Sequence > restored.Sequence);
        await restarted.DrainPendingAsync(CancellationToken.None);
        Assert.True(restarted.Index.TryGet(first.ArtId, out var stillFirst));
        Assert.Equal(sequence, stillFirst.Sequence);
        Assert.True(restarted.Index.TryGet(second.ArtId, out var allocated));
        Assert.Equal(accepted.Sequence, allocated.Sequence);
        Assert.Equal(ArticleStorageState.Present, allocated.State);
    }

    private static async Task RecoverRelocatedDeathAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record,
        bool evict)
    {
        var sequence = await AcceptPresentWithoutIndexCommittedAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var source));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var compact = await engine.CompactClosedSegmentAsync(source.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        Assert.True(evict ? engine.TryEvict(record.ArtId) : engine.TryInvalidate(record.ArtId));
        Assert.True(engine.Index.TryGet(record.ArtId, out var dead));
        Assert.NotEqual(source.Location.SegmentId.Value, dead.Location.SegmentId.Value);
        await FinishAndAssertDeadAsync(
            engine,
            record,
            sequence,
            evict ? ArticleStorageState.Evicted : ArticleStorageState.Invalid,
            dead.Location);
    }

    private static async Task FinishAndAssertDeadAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record,
        ulong sequence,
        ArticleStorageState state,
        StoredArticleLocation expectedLocation)
    {
        var lengthBefore = engine.Index.DurableLength;
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.None;
        await engine.RecoverAsync(CancellationToken.None);

        Assert.True(engine.Index.TryGet(record.ArtId, out var after));
        Assert.Equal(state, after.State);
        Assert.Equal(sequence, after.Sequence);
        Assert.Equal(lengthBefore, engine.Index.DurableLength);
        Assert.Equal(expectedLocation, after.Location);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.False(engine.ArticleCache.TryGet(record.ArtId, out _));
        Assert.False(engine.TryRead(record.ArtId, out _));
    }

    private static async Task AssertNewerAcceptPublishesAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record,
        bool evict)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var original));
        Assert.True(evict ? engine.TryEvict(record.ArtId) : engine.TryInvalidate(record.ArtId));
        Assert.False(engine.ArticleCache.TryGet(record.ArtId, out _));

        engine.SuspendBackgroundPersist = true;
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeIndexCommit;
        engine.TestPersistFaultExceptionKind = FileArticleStorageEngine.PersistFaultExceptionKind.InvalidOperation;
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RecoverAsync(CancellationToken.None));
        Assert.True(engine.Index.TryGet(record.ArtId, out var stillDead));
        Assert.Equal(original.Sequence, stillDead.Sequence);
        Assert.NotEqual(ArticleStorageState.Present, stillDead.State);

        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.None;
        await engine.RecoverAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var revived));
        Assert.Equal(ArticleStorageState.Present, revived.State);
        Assert.True(revived.Sequence > original.Sequence);
        Assert.NotEqual(stillDead.Location, revived.Location);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.True(engine.ArticleCache.TryGet(record.ArtId, out _));
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.Equal(revived.Location, read.Metadata.Location);
    }

    private static async Task<ulong> AcceptPresentWithoutIndexCommittedAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record)
    {
        engine.SuspendBackgroundPersist = true;
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.AfterIndexCommit;
        engine.TestPersistFaultExceptionKind = FileArticleStorageEngine.PersistFaultExceptionKind.InvalidOperation;
        var accept = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accept.Outcome);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RecoverAsync(CancellationToken.None));
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.Equal(accept.Sequence, meta.Sequence);
        return accept.Sequence;
    }

    private static void AssertDurableDeath(
        TempStorageDir dir,
        FileArticleStorageEngine engine,
        ArticleRecord record,
        ulong sequence,
        StoredArticleLocation source,
        StoredArticleLocation deadLocation)
    {
        var incomplete = engine.Journal.EnumerateIncomplete();
        var pending = Assert.Single(incomplete);
        Assert.Equal(sequence, pending.Accept.Sequence);
        Assert.NotNull(pending.PhysicalWritten);
        Assert.Equal(source, pending.PhysicalWritten.Value.Location);
        Assert.True(engine.Index.TryGet(record.ArtId, out var row));
        Assert.Equal(ArticleStorageState.Evicted, row.State);
        Assert.Equal(sequence, row.Sequence);
        Assert.Equal(deadLocation, row.Location);
        Assert.Equal((long)ArticleIndexDeltaFile.HeaderLength, engine.Index.DurableLength);
        var snapshot = ArticleIndexSnapshotCodec.Read(
            Path.Combine(dir.Options.ControlDir, FileArticleIndex.SnapshotFileName));
        var stored = Assert.Single(snapshot.Entries);
        Assert.Equal(ArticleStorageState.Evicted, stored.State);
        Assert.Equal(sequence, stored.Sequence);
        Assert.Equal(deadLocation, stored.Location);
    }

    private static async Task CommitUnindexedSequenceAsync(FileArticleJournal journal)
    {
        var record = CreateRecord("<seq-ckpt-fence@seg.test>");
        Assert.True(journal.TryAppendNewAccept(
            record.ArtId,
            record.ArtHash,
            record.ArtSize,
            DateTimeOffset.UtcNow,
            record.ArtData,
            out var accept,
            out _));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, new StoredArticleLocation(new SegmentId(1), 0, record.ArtSize)),
                CancellationToken.None));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendIndexCommittedAsync(
                new JournalIndexCommittedRecord(1, accept.Sequence),
                CancellationToken.None));
    }

    private static (ulong Sequence, StoredArticleLocation Location) AssertDurableRevivalPending(
        TempStorageDir dir,
        FileArticleStorageEngine engine,
        ArticleRecord record,
        ulong deadSequence,
        StoredArticleLocation deadLocation,
        ulong? expectedSequence)
    {
        var incomplete = engine.Journal.EnumerateIncomplete();
        var pending = Assert.Single(incomplete);
        Assert.True(pending.Accept.Sequence > deadSequence);
        if (expectedSequence is ulong expected)
        {
            Assert.Equal(expected, pending.Accept.Sequence);
        }

        Assert.NotNull(pending.PhysicalWritten);
        Assert.NotEqual(deadLocation, pending.PhysicalWritten.Value.Location);
        Assert.True(engine.Index.TryGet(record.ArtId, out var row));
        Assert.Equal(ArticleStorageState.Evicted, row.State);
        Assert.Equal(deadSequence, row.Sequence);
        Assert.Equal(deadLocation, row.Location);
        Assert.NotEqual(deadSequence, pending.Accept.Sequence);
        var snapshot = ArticleIndexSnapshotCodec.Read(
            Path.Combine(dir.Options.ControlDir, FileArticleIndex.SnapshotFileName));
        var stored = Assert.Single(snapshot.Entries);
        Assert.Equal(ArticleStorageState.Evicted, stored.State);
        Assert.Equal(deadSequence, stored.Sequence);
        Assert.Equal(deadLocation, stored.Location);
        return (pending.Accept.Sequence, pending.PhysicalWritten.Value.Location);
    }

    private static FileArticleStorageEngine Open(TempStorageDir dir, IArticleMemoryCache cache) =>
        FileArticleStorageEngine.Open(dir.Options, articleCache: cache);

    private static FileArticleStorageEngine OpenCapacity(TempStorageDir dir, IArticleMemoryCache cache) =>
        FileArticleStorageEngine.Open(
            dir.Options with { CapacityAdmissionEnabled = true },
            articleCache: cache,
            capacityReader: new FixedCapacityReader());

    private static void AssertJournalStartsWithSequenceFence(TempStorageDir dir)
    {
        var path = Path.Combine(dir.Options.ControlDir, FileArticleJournal.JournalFileName);
        var prefix = new byte[5];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.Equal(prefix.Length, stream.Read(prefix));
        }

        Assert.Equal((byte)ArticleJournalFrameType.SequenceFence, prefix[4]);
    }

    private static Dictionary<string, long> SegmentFiles(string segmentDir)
    {
        var files = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(segmentDir, "seg-*"))
        {
            files.Add(Path.GetFileName(path), new FileInfo(path).Length);
        }

        return files;
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
        _ = builder.Append("Subject: seq\r\n");
        _ = builder.Append("\r\nline1\r\nline2\r\n");
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-seq-" + Guid.NewGuid().ToString("N"));
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

    private sealed class FixedCapacityReader : IStorageCapacityReader
    {
        public StorageCapacitySnapshot Read() => new(10_000_000, 0, 10_000_000);
    }
}
