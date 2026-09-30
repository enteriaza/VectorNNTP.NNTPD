using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Memory;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

public sealed class MemoryArticleStorageEngineTests
{
    [Fact]
    public async Task A_CrashAfterAcceptOnly_ArtDataRecoverable_PressureIncludesArtSize()
    {
        await using var engine = CreateEngine();
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<a-accept@example.test>");
        var accept = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accept.Outcome);

        var incomplete = Assert.Single(engine.Journal.EnumerateIncomplete());
        Assert.Null(incomplete.PhysicalWritten);
        Assert.True(incomplete.Accept.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(record.ArtSize, engine.Journal.OutstandingRecoverableBytes);
        Assert.Equal(StorageWritePressure.Normal, engine.GetWritePressure());
        Assert.False(engine.Index.TryGet(record.ArtId, out _));

        await engine.RecoverAsync(CancellationToken.None);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task B_CrashAfterPhysicalWritten_CompletesIndexCommit()
    {
        await using var engine = CreateEngine();
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<b-pw@example.test>");
        var accept = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accept.Outcome);

        var appender = await engine.Segments.GetActiveAppenderAsync(CancellationToken.None);
        var location = await appender.AppendAsync(record.ArtData, CancellationToken.None);
        var pw = await engine.Journal.AppendPhysicalWrittenAsync(
            new JournalPhysicalWrittenRecord(1, accept.Sequence, location),
            CancellationToken.None);
        Assert.Equal(JournalAppendOutcome.Applied, pw);

        var incomplete = Assert.Single(engine.Journal.EnumerateIncomplete());
        Assert.NotNull(incomplete.PhysicalWritten);
        Assert.Equal(location, incomplete.PhysicalWritten!.Value.Location);
        Assert.Equal(record.ArtSize, engine.Journal.OutstandingRecoverableBytes);
        Assert.False(engine.Index.TryGet(record.ArtId, out _));

        await engine.RecoverAsync(CancellationToken.None);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(location, meta.Location);
        Assert.True(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task C_CrashAfterIndexCommitted_ReplayIsNoOp()
    {
        await using var engine = CreateEngine();
        var record = CreateRecord("<c-done@example.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        Assert.True(engine.TryRead(record.ArtId, out var before));

        var physicalBefore = engine.Journal.JournalPhysicalBytes;
        await engine.RecoverAsync(CancellationToken.None);
        Assert.True(engine.TryRead(record.ArtId, out var after));
        Assert.Equal(before.Metadata.Location, after.Metadata.Location);
        Assert.Equal(physicalBefore, engine.Journal.JournalPhysicalBytes);
    }

    [Fact]
    public async Task D_DuplicateWhileOutstanding_AndConflict()
    {
        await using var engine = CreateEngine();
        engine.SuspendBackgroundPersist = true;
        var first = CreateRecord("<d-dup@example.test>", "body-a\r\n");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(first, CancellationToken.None)).Outcome);

        var dup = await engine.AcceptAsync(first, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Duplicate, dup.Outcome);

        var conflict = CreateRecord("<d-dup@example.test>", "body-b\r\n");
        Assert.Equal(first.ArtId, conflict.ArtId);
        Assert.NotEqual(first.ArtHash, conflict.ArtHash);
        Assert.Equal(ArticleAcceptOutcome.Conflict, (await engine.AcceptAsync(conflict, CancellationToken.None)).Outcome);

        Assert.Single(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task E_PhysicalWritten_IdempotentAndConflict()
    {
        await using var engine = CreateEngine();
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<e-pw@example.test>");
        var accept = await engine.AcceptAsync(record, CancellationToken.None);
        var appender = await engine.Segments.GetActiveAppenderAsync(CancellationToken.None);
        var location = await appender.AppendAsync(record.ArtData, CancellationToken.None);

        var applied = await engine.Journal.AppendPhysicalWrittenAsync(
            new JournalPhysicalWrittenRecord(1, accept.Sequence, location),
            CancellationToken.None);
        Assert.Equal(JournalAppendOutcome.Applied, applied);

        var noop = await engine.Journal.AppendPhysicalWrittenAsync(
            new JournalPhysicalWrittenRecord(1, accept.Sequence, location),
            CancellationToken.None);
        Assert.Equal(JournalAppendOutcome.IdempotentNoOp, noop);

        var other = new StoredArticleLocation(location.SegmentId, location.Offset + 1, location.Length);
        var conflict = await engine.Journal.AppendPhysicalWrittenAsync(
            new JournalPhysicalWrittenRecord(1, accept.Sequence, other),
            CancellationToken.None);
        Assert.Equal(JournalAppendOutcome.Conflict, conflict);
    }

    [Fact]
    public void F_Relocate_RequiresExpectedLocation_LeavesSourceUntouched()
    {
        var index = new MemoryArticleIndex();
        var artId = ArticleId.FromMessageId("<f-rel@example.test>"u8);
        var oldLoc = new StoredArticleLocation(new SegmentId(1), 0, 10);
        var newLoc = new StoredArticleLocation(new SegmentId(2), 0, 10);
        Assert.True(index.TryCommitPresent(new StoredArticleMetadata(
            artId, 123, 10, oldLoc, ArticleStorageState.Present, DateTimeOffset.UtcNow)));

        Assert.Equal(
            ArticleRelocateOutcome.ExpectedLocationMismatch,
            index.TryRelocate(artId, newLoc, newLoc, 123, 10));
        Assert.Equal(
            ArticleRelocateOutcome.IdentityMismatch,
            index.TryRelocate(artId, oldLoc, newLoc, 999, 10));
        Assert.Equal(
            ArticleRelocateOutcome.Relocated,
            index.TryRelocate(artId, oldLoc, newLoc, 123, 10));
        Assert.True(index.TryGet(artId, out var meta));
        Assert.Equal(newLoc, meta.Location);

        // Source segment bytes are independent of index relocate (catalogue/store unchanged here).
        var catalogue = new MemorySegmentCatalogue();
        catalogue.Upsert(new SegmentInfo(
            oldLoc.SegmentId, SegmentState.Closed, 1, 10, 10, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        Assert.True(catalogue.TryGet(oldLoc.SegmentId, out var before));
        Assert.Equal(SegmentState.Closed, before.State);
        Assert.False(catalogue.TryRetire(oldLoc.SegmentId, expectedGeneration: 99, DateTimeOffset.UtcNow));
        Assert.True(catalogue.TryRetire(oldLoc.SegmentId, expectedGeneration: 1, DateTimeOffset.UtcNow));
        Assert.True(catalogue.TryGet(oldLoc.SegmentId, out var retired));
        Assert.Equal(SegmentState.Retired, retired.State);
    }

    [Fact]
    public void G_RelocateReplay_Idempotent()
    {
        var index = new MemoryArticleIndex();
        var artId = ArticleId.FromMessageId("<g-rel@example.test>"u8);
        var oldLoc = new StoredArticleLocation(new SegmentId(3), 0, 8);
        var newLoc = new StoredArticleLocation(new SegmentId(4), 0, 8);
        Assert.True(index.TryCommitPresent(new StoredArticleMetadata(
            artId, 42, 8, oldLoc, ArticleStorageState.Present, DateTimeOffset.UtcNow)));
        Assert.Equal(ArticleRelocateOutcome.Relocated, index.TryRelocate(artId, oldLoc, newLoc, 42, 8));
        Assert.Equal(ArticleRelocateOutcome.IdempotentNoOp, index.TryRelocate(artId, oldLoc, newLoc, 42, 8));
        Assert.Equal(ArticleRelocateOutcome.IdempotentNoOp, index.TryRelocate(artId, newLoc, newLoc, 42, 8));
        Assert.True(index.TryGet(artId, out var meta));
        Assert.Equal(newLoc, meta.Location);
    }

    [Fact]
    public async Task H_TryRead_DoesNotDurableWriteForLastAccess()
    {
        await using var engine = CreateEngine();
        var record = CreateRecord("<h-touch@example.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);

        var durableBefore = engine.Index.DurableWriteCount;
        var touchBefore = engine.Index.TouchHintCount;
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.Equal(durableBefore, engine.Index.DurableWriteCount);
        Assert.True(engine.Index.TouchHintCount > touchBefore);
    }

    [Fact]
    public async Task I_Pressure_UsesOutstandingRecoverableBytes_NotPhysicalOccupancy()
    {
        var options = new ArticleStorageRuntimeOptions(
            ControlDir: "control",
            SegmentDir: "cache",
            JournalSoftLimitBytes: Math.Max(1, CreateRecord("<probe@example.test>", "x\r\n").ArtSize / 2),
            JournalHardLimitBytes: CreateRecord("<probe2@example.test>", "x\r\n").ArtSize,
            SegmentTargetSizeBytes: 1024);

        // Use identical size for limits.
        var probe = CreateRecord("<i-probe@example.test>", "x\r\n");
        options = options with
        {
            JournalSoftLimitBytes = Math.Max(1, probe.ArtSize / 2),
            JournalHardLimitBytes = probe.ArtSize,
        };

        var journal = new MemoryArticleJournal(options);
        await using var engine = new MemoryArticleStorageEngine(
            journal,
            new MemorySegmentStore(),
            new MemoryArticleIndex(),
            new MemorySegmentCatalogue(),
            new MemoryStorageTelemetryLog());
        engine.SuspendBackgroundPersist = true;

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(probe, CancellationToken.None)).Outcome);
        Assert.Equal(probe.ArtSize, journal.OutstandingRecoverableBytes);
        Assert.True(journal.JournalPhysicalBytes >= probe.ArtSize);
        Assert.Equal(StorageWritePressure.Critical, engine.GetWritePressure());

        Assert.Equal(
            ArticleAcceptOutcome.RejectedPressure,
            (await engine.AcceptAsync(CreateRecord("<i-extra@example.test>", "y\r\n"), CancellationToken.None)).Outcome);

        await engine.RecoverAsync(CancellationToken.None);
        Assert.Equal(0, journal.OutstandingRecoverableBytes);
        Assert.Equal(StorageWritePressure.Normal, engine.GetWritePressure());

        var physicalWhileCommitted = journal.JournalPhysicalBytes;
        Assert.True(physicalWhileCommitted > 0);
        _ = journal.CheckpointTruncateCommitted();
        Assert.True(journal.JournalPhysicalBytes < physicalWhileCommitted);
        Assert.Equal(0, journal.OutstandingRecoverableBytes);
    }

    [Fact]
    public async Task Accept_JournalFirst_BeforeSegmentWhenBackgroundSuspended()
    {
        await using var engine = CreateEngine();
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<journal-first@example.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        Assert.False(engine.Index.TryGet(record.ArtId, out _));
        await engine.RecoverAsync(CancellationToken.None);
        Assert.True(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task ConcurrentAccepts_CorrelateIndependently()
    {
        await using var engine = CreateEngine();
        var tasks = Enumerable.Range(0, 8)
            .Select(i => engine.AcceptAsync(CreateRecord($"<c{i}@example.test>"), CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, static r => Assert.Equal(ArticleAcceptOutcome.Accepted, r.Outcome));
        await engine.DrainPendingAsync(CancellationToken.None);
        foreach (var r in results)
        {
            Assert.True(engine.TryRead(r.ArtId, out _));
        }
    }

    [Fact]
    public async Task IndexCommitted_WithoutPhysicalWritten_IsRejected()
    {
        await using var engine = CreateEngine();
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<no-pw@example.test>");
        var accept = await engine.AcceptAsync(record, CancellationToken.None);
        var outcome = await engine.Journal.AppendIndexCommittedAsync(
            new JournalIndexCommittedRecord(1, accept.Sequence),
            CancellationToken.None);
        Assert.Equal(JournalAppendOutcome.Rejected, outcome);
        Assert.Equal(record.ArtSize, engine.Journal.OutstandingRecoverableBytes);
    }

    [Fact]
    public async Task PresentSameContent_IsDuplicate_AndLeavesOriginal()
    {
        await using var engine = CreateEngine();
        var record = CreateRecord("<same@example.test>", "alpha\r\n");
        var first = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, first.Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);

        var again = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Duplicate, again.Outcome);
        Assert.Equal(0ul, again.Sequence);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task PresentDifferentContent_IsConflict_AndDoesNotOverwrite()
    {
        await using var engine = CreateEngine();
        var original = CreateRecord("<conflict@example.test>", "alpha\r\n");
        var conflicting = CreateRecord("<conflict@example.test>", "beta\r\n");
        Assert.Equal(original.ArtId, conflicting.ArtId);
        Assert.NotEqual(original.ArtHash, conflicting.ArtHash);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(original, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);

        var conflict = await engine.AcceptAsync(conflicting, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Conflict, conflict.Outcome);
        Assert.True(engine.TryRead(original.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(original.ArtData.Span));
        Assert.False(read.ArtData.Span.SequenceEqual(conflicting.ArtData.Span));
    }

    [Fact]
    public async Task EvictedSameContent_IsAcceptedAtNewLocation()
    {
        await using var engine = CreateEngine();
        var record = CreateRecord("<evicted@example.test>", "alpha\r\n");
        var first = await engine.AcceptAsync(record, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(record.ArtId));
        Assert.False(engine.TryRead(record.ArtId, out _));

        var again = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, again.Outcome);
        Assert.True(again.Sequence > first.Sequence);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task InvalidSameContent_IsAcceptedAtNewLocation()
    {
        await using var engine = CreateEngine();
        var record = CreateRecord("<invalid@example.test>", "alpha\r\n");
        var first = await engine.AcceptAsync(record, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.TryInvalidate(record.ArtId));
        Assert.False(engine.TryRead(record.ArtId, out _));

        var again = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, again.Outcome);
        Assert.True(again.Sequence > first.Sequence);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
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
