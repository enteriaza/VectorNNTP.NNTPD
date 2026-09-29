using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Memory;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Cold-start recovery, catalogue fencing, and high-value invariant tests for Phase 1.5 hardening.
/// </summary>
public sealed class MemoryArticleStorageColdStartAndFencingTests
{
    [Fact]
    public async Task ColdStart_A_AfterAcceptOnly_RecoversFromJournalSnapshot()
    {
        MemoryArticleJournalSnapshot journalSnap;
        var record = CreateRecord("<cold-a@example.test>");
        long expectedArtSize;
        await using (var engineA = CreateEngine())
        {
            engineA.SuspendBackgroundPersist = true;
            var accept = await engineA.AcceptAsync(record, CancellationToken.None);
            Assert.Equal(ArticleAcceptOutcome.Accepted, accept.Outcome);
            Assert.Equal(record.ArtSize, engineA.Journal.OutstandingRecoverableBytes);
            expectedArtSize = record.ArtSize;
            journalSnap = engineA.Journal.ExportSnapshot();
        }

        var journalB = MemoryArticleJournal.ImportSnapshot(journalSnap);
        var segmentsB = new MemorySegmentStore();
        var indexB = new MemoryArticleIndex();
        await using var engineB = new MemoryArticleStorageEngine(
            journalB,
            segmentsB,
            indexB,
            new MemorySegmentCatalogue(),
            new MemoryStorageTelemetryLog())
        {
            SuspendBackgroundPersist = true,
        };

        var incomplete = Assert.Single(journalB.EnumerateIncomplete());
        Assert.Null(incomplete.PhysicalWritten);
        Assert.True(incomplete.Accept.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(expectedArtSize, journalB.OutstandingRecoverableBytes);

        var appendsBefore = segmentsB.AppendCount;
        await engineB.RecoverAsync(CancellationToken.None);

        Assert.Empty(journalB.EnumerateIncomplete());
        Assert.Equal(0, journalB.OutstandingRecoverableBytes);
        Assert.True(segmentsB.AppendCount > appendsBefore);
        Assert.True(engineB.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.NotEqual(default, read.Metadata.Location);
    }

    [Fact]
    public async Task ColdStart_B_AfterPhysicalWritten_DoesNotReappend()
    {
        MemoryArticleJournalSnapshot journalSnap;
        MemorySegmentStoreSnapshot segmentSnap;
        StoredArticleLocation location;
        var record = CreateRecord("<cold-b@example.test>");
        await using (var engineA = CreateEngine())
        {
            engineA.SuspendBackgroundPersist = true;
            var accept = await engineA.AcceptAsync(record, CancellationToken.None);
            var appender = await engineA.Segments.GetActiveAppenderAsync(CancellationToken.None);
            location = await appender.AppendAsync(record.ArtData, CancellationToken.None);
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await engineA.Journal.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, accept.Sequence, location),
                    CancellationToken.None));
            Assert.Equal(record.ArtSize, engineA.Journal.OutstandingRecoverableBytes);
            journalSnap = engineA.Journal.ExportSnapshot();
            segmentSnap = engineA.Segments.ExportSnapshot();
        }

        var journalB = MemoryArticleJournal.ImportSnapshot(journalSnap);
        var segmentsB = MemorySegmentStore.ImportSnapshot(segmentSnap);
        var appendsBefore = segmentsB.AppendCount;
        await using var engineB = new MemoryArticleStorageEngine(
            journalB,
            segmentsB,
            new MemoryArticleIndex(),
            new MemorySegmentCatalogue(),
            new MemoryStorageTelemetryLog())
        {
            SuspendBackgroundPersist = true,
        };

        Assert.Single(journalB.EnumerateIncomplete());
        await engineB.RecoverAsync(CancellationToken.None);

        Assert.Equal(appendsBefore, segmentsB.AppendCount);
        Assert.Empty(journalB.EnumerateIncomplete());
        Assert.Equal(0, journalB.OutstandingRecoverableBytes);
        Assert.True(engineB.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(location, meta.Location);
        Assert.True(engineB.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task ColdStart_C_AfterIndexCommitted_ReplayIsNoOp()
    {
        MemoryArticleJournalSnapshot journalSnap;
        MemorySegmentStoreSnapshot segmentSnap;
        long appendsAtCrash;
        var record = CreateRecord("<cold-c@example.test>");
        await using (var engineA = CreateEngine())
        {
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await engineA.AcceptAsync(record, CancellationToken.None)).Outcome);
            await engineA.DrainPendingAsync(CancellationToken.None);
            Assert.Empty(engineA.Journal.EnumerateIncomplete());
            Assert.Equal(0, engineA.Journal.OutstandingRecoverableBytes);
            journalSnap = engineA.Journal.ExportSnapshot();
            segmentSnap = engineA.Segments.ExportSnapshot();
            appendsAtCrash = engineA.Segments.AppendCount;
        }

        var journalB = MemoryArticleJournal.ImportSnapshot(journalSnap);
        var segmentsB = MemorySegmentStore.ImportSnapshot(segmentSnap);
        var indexB = new MemoryArticleIndex();
        await using var engineB = new MemoryArticleStorageEngine(
            journalB,
            segmentsB,
            indexB,
            new MemorySegmentCatalogue(),
            new MemoryStorageTelemetryLog())
        {
            SuspendBackgroundPersist = true,
        };

        Assert.Empty(journalB.EnumerateIncomplete());
        Assert.Equal(0, journalB.OutstandingRecoverableBytes);
        await engineB.RecoverAsync(CancellationToken.None);
        await engineB.RecoverAsync(CancellationToken.None);

        Assert.Empty(journalB.EnumerateIncomplete());
        Assert.Equal(0, journalB.OutstandingRecoverableBytes);
        Assert.Equal(appendsAtCrash, segmentsB.AppendCount);
        Assert.False(indexB.TryGet(record.ArtId, out _));
    }

    [Fact]
    public void Catalogue_Upsert_CannotResurrectRetired_OrRegressGeneration()
    {
        var catalogue = new MemorySegmentCatalogue();
        var id = new SegmentId(7);
        catalogue.Upsert(new SegmentInfo(
            id, SegmentState.Active, Generation: 1, 10, 10, 0, DateTimeOffset.UtcNow, null));
        catalogue.Upsert(new SegmentInfo(
            id, SegmentState.Closed, Generation: 1, 10, 10, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        Assert.True(catalogue.TryRetire(id, expectedGeneration: 1, DateTimeOffset.UtcNow));
        Assert.True(catalogue.TryGet(id, out var retired));
        Assert.Equal(SegmentState.Retired, retired.State);

        Assert.Throws<InvalidOperationException>(() =>
            catalogue.Upsert(new SegmentInfo(
                id, SegmentState.Active, Generation: 1, 10, 10, 0, DateTimeOffset.UtcNow, null)));
        Assert.Throws<InvalidOperationException>(() =>
            catalogue.Upsert(new SegmentInfo(
                id, SegmentState.Closed, Generation: 2, 10, 10, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));

        var id2 = new SegmentId(8);
        catalogue.Upsert(new SegmentInfo(
            id2, SegmentState.Active, Generation: 5, 4, 4, 0, DateTimeOffset.UtcNow, null));
        Assert.Throws<InvalidOperationException>(() =>
            catalogue.Upsert(new SegmentInfo(
                id2, SegmentState.Active, Generation: 4, 4, 4, 0, DateTimeOffset.UtcNow, null)));
    }

    [Fact]
    public async Task Present_ConflictingAccept_DoesNotMutateOrJournal()
    {
        await using var engine = CreateEngine();
        var first = CreateRecord("<present-conflict@example.test>", "body-a\r\n");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(first, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(first.ArtId, out var before));
        var appendsBefore = engine.Segments.AppendCount;
        var incompleteBefore = engine.Journal.EnumerateIncomplete().Count;

        var conflicting = CreateRecord("<present-conflict@example.test>", "body-b\r\n");
        Assert.Equal(first.ArtId, conflicting.ArtId);
        Assert.NotEqual(first.ArtHash, conflicting.ArtHash);
        Assert.Equal(ArticleAcceptOutcome.Conflict, (await engine.AcceptAsync(conflicting, CancellationToken.None)).Outcome);

        Assert.True(engine.Index.TryGet(first.ArtId, out var after));
        Assert.Equal(before, after);
        Assert.Equal(appendsBefore, engine.Segments.AppendCount);
        Assert.Equal(incompleteBefore, engine.Journal.EnumerateIncomplete().Count);
        Assert.False(engine.Journal.TryGetOutstanding(first.ArtId, out _));
    }

    [Fact]
    public async Task Recovery_WhenIndexAlreadyPresentMatching_CompletesWithoutReappend()
    {
        MemoryArticleJournalSnapshot journalSnap;
        MemorySegmentStoreSnapshot segmentSnap;
        StoredArticleLocation location;
        var record = CreateRecord("<recover-present@example.test>");
        await using (var engineA = CreateEngine())
        {
            engineA.SuspendBackgroundPersist = true;
            var accept = await engineA.AcceptAsync(record, CancellationToken.None);
            var appender = await engineA.Segments.GetActiveAppenderAsync(CancellationToken.None);
            location = await appender.AppendAsync(record.ArtData, CancellationToken.None);
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await engineA.Journal.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, accept.Sequence, location),
                    CancellationToken.None));
            journalSnap = engineA.Journal.ExportSnapshot();
            segmentSnap = engineA.Segments.ExportSnapshot();
        }

        var journalB = MemoryArticleJournal.ImportSnapshot(journalSnap);
        var segmentsB = MemorySegmentStore.ImportSnapshot(segmentSnap);
        var indexB = new MemoryArticleIndex();
        Assert.True(indexB.TryCommitPresent(new StoredArticleMetadata(
            record.ArtId,
            record.ArtHash,
            record.ArtSize,
            location,
            ArticleStorageState.Present,
            DateTimeOffset.UtcNow)));
        var appendsBefore = segmentsB.AppendCount;
        await using var engineB = new MemoryArticleStorageEngine(
            journalB,
            segmentsB,
            indexB,
            new MemorySegmentCatalogue(),
            new MemoryStorageTelemetryLog())
        {
            SuspendBackgroundPersist = true,
        };

        await engineB.RecoverAsync(CancellationToken.None);
        Assert.Equal(appendsBefore, segmentsB.AppendCount);
        Assert.Empty(journalB.EnumerateIncomplete());
        Assert.Equal(0, journalB.OutstandingRecoverableBytes);
        Assert.True(indexB.TryGet(record.ArtId, out var meta));
        Assert.Equal(location, meta.Location);
    }

    [Fact]
    public void TryRelocate_WrongArticleId_LeavesIndexUnchanged()
    {
        var index = new MemoryArticleIndex();
        var artId = ArticleId.FromMessageId("<rel-ok@example.test>"u8);
        var otherId = ArticleId.FromMessageId("<rel-wrong@example.test>"u8);
        var oldLoc = new StoredArticleLocation(new SegmentId(1), 0, 6);
        var newLoc = new StoredArticleLocation(new SegmentId(2), 0, 6);
        Assert.True(index.TryCommitPresent(new StoredArticleMetadata(
            artId, 11, 6, oldLoc, ArticleStorageState.Present, DateTimeOffset.UtcNow)));

        Assert.Equal(
            ArticleRelocateOutcome.NotPresent,
            index.TryRelocate(otherId, oldLoc, newLoc, 11, 6));
        Assert.True(index.TryGet(artId, out var meta));
        Assert.Equal(oldLoc, meta.Location);
        Assert.False(index.TryGet(otherId, out _));
    }

    [Fact]
    public async Task Relocate_DoesNotMutateSourceSegmentBytes()
    {
        var segments = new MemorySegmentStore();
        var appender = await segments.GetActiveAppenderAsync(CancellationToken.None);
        var payload = Encoding.ASCII.GetBytes("ABCDEFGH");
        var oldLoc = await appender.AppendAsync(payload, CancellationToken.None);
        Assert.True(segments.TryRead(oldLoc, out var before));
        var beforeCopy = before.ToArray();

        var index = new MemoryArticleIndex();
        var artId = ArticleId.FromMessageId("<rel-bytes@example.test>"u8);
        Assert.True(index.TryCommitPresent(new StoredArticleMetadata(
            artId, 99, payload.Length, oldLoc, ArticleStorageState.Present, DateTimeOffset.UtcNow)));

        var newLoc = new StoredArticleLocation(new SegmentId(99), 0, payload.Length);
        Assert.Equal(
            ArticleRelocateOutcome.Relocated,
            index.TryRelocate(artId, oldLoc, newLoc, 99, payload.Length));

        Assert.True(segments.TryRead(oldLoc, out var after));
        Assert.True(after.Span.SequenceEqual(beforeCopy));
        Assert.True(index.TryGet(artId, out var meta));
        Assert.Equal(newLoc, meta.Location);
    }

    private static MemoryArticleStorageEngine CreateEngine()
    {
        var options = new ArticleStorageRuntimeOptions(
            ControlDir: "control",
            SegmentDir: "cache",
            JournalSoftLimitBytes: ArticleStorageOptions.DefaultJournalSoftLimitBytes,
            JournalHardLimitBytes: ArticleStorageOptions.DefaultJournalHardLimitBytes,
            SegmentTargetSizeBytes: ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
        return new MemoryArticleStorageEngine(options);
    }

    private static ArticleRecord CreateRecord(string messageId, string body = "line1\r\nline2\r\n")
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var destuffed = BuildDestuffed(messageId, body);
        var created = ArticleRecordFactory.TryCreate(parser, destuffed);
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private static byte[] BuildDestuffed(string messageId, string body)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: storage\r\n");
        _ = builder.Append("\r\n").Append(body);
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
