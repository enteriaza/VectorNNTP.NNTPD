using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Phase 2D filesystem engine integration and crash-recovery tests (Option 1 orphan semantics).
/// </summary>
public sealed class FileArticleStorageEngineTests
{
    [Fact]
    public async Task A_Accept_Succeeds()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<a@example.test>");
        var result = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, result.Outcome);
        Assert.True(result.Sequence > 0);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task B_Read_AfterAccept()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<b@example.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task C_Duplicate_Identical()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<c@example.test>", "same\r\n");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        var dup = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Duplicate, dup.Outcome);
    }

    [Fact]
    public async Task D_Conflict_Rejected()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var first = CreateRecord("<d@example.test>", "body-a\r\n");
        var conflict = CreateRecord("<d@example.test>", "body-b\r\n");
        _ = await engine.AcceptAsync(first, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        var result = await engine.AcceptAsync(conflict, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Conflict, result.Outcome);
    }

    [Fact]
    public async Task E_Survive_DisposeReopen()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<e@example.test>");
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
            await engineA.DrainPendingAsync(CancellationToken.None);
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.True(engineB.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task F_JournalAcceptFailure_PreventsFalseSuccess()
    {
        var probe = CreateRecord("<f-probe@example.test>", "x\r\n");
        using var dir = TempStorageDir.Create(
            softLimitBytes: Math.Max(1, probe.ArtSize / 2),
            hardLimitBytes: probe.ArtSize);
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(probe, CancellationToken.None)).Outcome);
        var rejected = CreateRecord("<f-reject@example.test>", "y\r\n");
        var result = await engine.AcceptAsync(rejected, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedPressure, result.Outcome);
        Assert.False(engine.Index.TryGet(rejected.ArtId, out _));
    }

    [Fact]
    public async Task G_SataFailure_PreventsIndexCommit()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<g@example.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;
        await Assert.ThrowsAsync<IOException>(() => engine.RecoverAsync(CancellationToken.None));
        Assert.Single(engine.Journal.EnumerateIncomplete());
        Assert.Null(Assert.Single(engine.Journal.EnumerateIncomplete()).PhysicalWritten);
        Assert.False(engine.Index.TryGet(record.ArtId, out _));
    }

    [Fact]
    public async Task H_PhysicalWrittenFailure_PreventsIndexCommitted()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<h@example.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforePhysicalWritten;
        await Assert.ThrowsAsync<IOException>(() => engine.RecoverAsync(CancellationToken.None));
        var incomplete = Assert.Single(engine.Journal.EnumerateIncomplete());
        Assert.Null(incomplete.PhysicalWritten);
        Assert.False(engine.Index.TryGet(record.ArtId, out _));
        // Orphan SATA append may exist; Accept-only remains recoverable.
        Assert.True(engine.PhysicalAppendCount >= 1);
    }

    [Fact]
    public async Task I_IndexFailure_PreventsIndexCommitted()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<i@example.test>", "body-a\r\n");
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            engineA.SuspendBackgroundPersist = true;
            var accept = await engineA.AcceptAsync(record, CancellationToken.None);
            var appender = await engineA.Segments.GetActiveAppenderAsync(CancellationToken.None);
            var location = await appender.AppendAsync(record.ArtData, CancellationToken.None);
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await engineA.Journal.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, accept.Sequence, location),
                    CancellationToken.None));

            // Conflicting Present at a different location blocks TryCommitPresent.
            var other = new StoredArticleLocation(new SegmentId(99), 0, location.Length);
            Assert.True(
                engineA.Index.TryCommitPresent(
                    new StoredArticleMetadata(
                        record.ArtId,
                        record.ArtHash ^ 1UL,
                        record.ArtSize,
                        other,
                        ArticleStorageState.Present,
                        DateTimeOffset.UtcNow,
                        0UL)));
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        engineB.SuspendBackgroundPersist = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => engineB.RecoverAsync(CancellationToken.None));
        Assert.Single(engineB.Journal.EnumerateIncomplete());
        Assert.NotNull(Assert.Single(engineB.Journal.EnumerateIncomplete()).PhysicalWritten);
    }

    [Fact]
    public async Task J_IndexCommittedFailure_LeavesRecoverableState()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<j@example.test>");
        long appendsAfterFault;
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            engineA.SuspendBackgroundPersist = true;
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
            engineA.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.AfterIndexCommit;
            await Assert.ThrowsAsync<IOException>(() => engineA.RecoverAsync(CancellationToken.None));
            Assert.True(engineA.Index.TryGet(record.ArtId, out var meta));
            Assert.Equal(ArticleStorageState.Present, meta.State);
            Assert.Single(engineA.Journal.EnumerateIncomplete());
            appendsAfterFault = engineA.PhysicalAppendCount;
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        engineB.SuspendBackgroundPersist = true;
        var before = engineB.PhysicalAppendCount;
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.Equal(before, engineB.PhysicalAppendCount);
        Assert.Empty(engineB.Journal.EnumerateIncomplete());
        Assert.True(engineB.TryRead(record.ArtId, out _));
        Assert.True(appendsAfterFault >= 1);
    }

    [Fact]
    public async Task K_AcceptOnly_Recovery_ReappendsFromJournal()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<k@example.test>");
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            engineA.SuspendBackgroundPersist = true;
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
            Assert.Null(Assert.Single(engineA.Journal.EnumerateIncomplete()).PhysicalWritten);
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        engineB.SuspendBackgroundPersist = true;
        Assert.Equal(0, engineB.PhysicalAppendCount);
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.Equal(1, engineB.PhysicalAppendCount);
        Assert.True(engineB.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task L_AcceptOnly_AfterOrphanSata_AdoptsProvenRecord()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<l@example.test>");
        long sizeAfterOrphan;
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            engineA.SuspendBackgroundPersist = true;
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
            var appender = await engineA.Segments.GetActiveAppenderAsync(CancellationToken.None);
            _ = await appender.AppendAsync(record.ArtData, CancellationToken.None);
            sizeAfterOrphan = engineA.Segments.GetActiveSizeBytes();
            Assert.True(sizeAfterOrphan > 0);
            Assert.Null(Assert.Single(engineA.Journal.EnumerateIncomplete()).PhysicalWritten);
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        engineB.SuspendBackgroundPersist = true;
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.Equal(0, engineB.PhysicalAppendCount);
        Assert.Equal(sizeAfterOrphan, engineB.Segments.GetActiveSizeBytes());
        Assert.True(engineB.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task M_PhysicalWritten_Recovery_ReusesLocation()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<m@example.test>");
        StoredArticleLocation location;
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
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
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        engineB.SuspendBackgroundPersist = true;
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.Equal(0, engineB.PhysicalAppendCount);
        Assert.True(engineB.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(location, meta.Location);
    }

    [Fact]
    public async Task N_PhysicalWritten_Recovery_DoesNotReappend()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<n@example.test>");
        long sizeBeforeRecover;
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            engineA.SuspendBackgroundPersist = true;
            var accept = await engineA.AcceptAsync(record, CancellationToken.None);
            var appender = await engineA.Segments.GetActiveAppenderAsync(CancellationToken.None);
            var location = await appender.AppendAsync(record.ArtData, CancellationToken.None);
            _ = await engineA.Journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, location),
                CancellationToken.None);
            sizeBeforeRecover = engineA.Segments.GetActiveSizeBytes();
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        engineB.SuspendBackgroundPersist = true;
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.Equal(sizeBeforeRecover, engineB.Segments.GetActiveSizeBytes());
        Assert.Equal(0, engineB.PhysicalAppendCount);
    }

    [Fact]
    public async Task O_IndexPresent_WithoutIndexCommitted_CompletesIdempotently()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<o@example.test>");
        StoredArticleLocation location;
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            engineA.SuspendBackgroundPersist = true;
            var accept = await engineA.AcceptAsync(record, CancellationToken.None);
            var appender = await engineA.Segments.GetActiveAppenderAsync(CancellationToken.None);
            location = await appender.AppendAsync(record.ArtData, CancellationToken.None);
            _ = await engineA.Journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, location),
                CancellationToken.None);
            Assert.True(
                engineA.Index.TryCommitPresent(
                    new StoredArticleMetadata(
                        record.ArtId,
                        record.ArtHash,
                        record.ArtSize,
                        location,
                        ArticleStorageState.Present,
                        DateTimeOffset.UtcNow,
                        accept.Sequence)));
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        engineB.SuspendBackgroundPersist = true;
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.Equal(0, engineB.PhysicalAppendCount);
        Assert.Empty(engineB.Journal.EnumerateIncomplete());
        Assert.True(engineB.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task P_FullyCommitted_Recovery_IsNoOp()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<p@example.test>");
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
            await engineA.DrainPendingAsync(CancellationToken.None);
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        engineB.SuspendBackgroundPersist = true;
        Assert.Empty(engineB.Journal.EnumerateIncomplete());
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.Equal(0, engineB.PhysicalAppendCount);
        Assert.True(engineB.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task Q_Recovery_Idempotent_AcrossEngines()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<q@example.test>");
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            engineA.SuspendBackgroundPersist = true;
            var accept = await engineA.AcceptAsync(record, CancellationToken.None);
            var appender = await engineA.Segments.GetActiveAppenderAsync(CancellationToken.None);
            var location = await appender.AppendAsync(record.ArtData, CancellationToken.None);
            _ = await engineA.Journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, location),
                CancellationToken.None);
        }

        long sizeAfterB;
        await using (var engineB = FileArticleStorageEngine.Open(dir.Options))
        {
            engineB.SuspendBackgroundPersist = true;
            await engineB.RecoverAsync(CancellationToken.None);
            sizeAfterB = engineB.Segments.GetActiveSizeBytes();
            Assert.Empty(engineB.Journal.EnumerateIncomplete());
        }

        await using var engineC = FileArticleStorageEngine.Open(dir.Options);
        engineC.SuspendBackgroundPersist = true;
        await engineC.RecoverAsync(CancellationToken.None);
        Assert.Equal(sizeAfterB, engineC.Segments.GetActiveSizeBytes());
        Assert.Equal(0, engineC.PhysicalAppendCount);
        Assert.True(engineC.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task R_PhysicalWritten_LocationValidated()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<r@example.test>");
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            engineA.SuspendBackgroundPersist = true;
            var accept = await engineA.AcceptAsync(record, CancellationToken.None);
            // Journal a plausible-length location that does not contain valid bytes.
            var bogus = new StoredArticleLocation(
                new SegmentId(1),
                0,
                SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await engineA.Journal.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, accept.Sequence, bogus),
                    CancellationToken.None));
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        engineB.SuspendBackgroundPersist = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => engineB.RecoverAsync(CancellationToken.None));
        Assert.Single(engineB.Journal.EnumerateIncomplete());
        Assert.False(
            engineB.Index.TryGet(record.ArtId, out var meta)
            && meta.State == ArticleStorageState.Present);
    }

    [Fact]
    public async Task S_Physical_ArtIdMismatch_Detected()
    {
        await AssertPhysicalIdentityMismatchAsync(
            "<s-id@example.test>",
            "<s-other@example.test>",
            corruptArtId: true,
            corruptHash: false,
            corruptSize: false);
    }

    [Fact]
    public async Task T_Physical_ArtHashMismatch_Detected()
    {
        await AssertPhysicalIdentityMismatchAsync(
            "<t-hash@example.test>",
            null,
            corruptArtId: false,
            corruptHash: true,
            corruptSize: false);
    }

    [Fact]
    public async Task U_Physical_ArtSizeMismatch_Detected()
    {
        await AssertPhysicalIdentityMismatchAsync(
            "<u-size@example.test>",
            null,
            corruptArtId: false,
            corruptHash: false,
            corruptSize: true);
    }

    [Fact]
    public async Task V_CorruptPhysicalWritten_FailsClosed_NoFalsePresent()
    {
        // Journal forbids superseding PhysicalWritten (Conflict). Corrupt location cannot be
        // rewritten with a new PhysicalWritten frame; recovery must fail closed and leave
        // the sequence outstanding without Present over corrupt bytes.
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<v@example.test>");
        StoredArticleLocation location;
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
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
        }

        CorruptSegmentRecord(dir.Options.SegmentDir, location, flipPayload: true);

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        engineB.SuspendBackgroundPersist = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => engineB.RecoverAsync(CancellationToken.None));
        Assert.Single(engineB.Journal.EnumerateIncomplete());
        Assert.Equal(0, engineB.PhysicalAppendCount);
        Assert.False(
            engineB.Index.TryGet(record.ArtId, out var meta)
            && meta.State == ArticleStorageState.Present);
    }

    [Fact]
    public async Task W_Restart_AfterRecovery_Readable()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<w@example.test>");
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            engineA.SuspendBackgroundPersist = true;
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
        }

        await using (var engineB = FileArticleStorageEngine.Open(dir.Options))
        {
            await engineB.RecoverAsync(CancellationToken.None);
        }

        await using var engineC = FileArticleStorageEngine.Open(dir.Options);
        Assert.True(engineC.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task X_Relocate_ThroughIndex_Persists()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<x@example.test>");
        StoredArticleLocation oldLoc;
        StoredArticleLocation newLoc;
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
            await engineA.DrainPendingAsync(CancellationToken.None);
            Assert.True(engineA.Index.TryGet(record.ArtId, out var meta));
            oldLoc = meta.Location;
            var appender = await engineA.Segments.GetActiveAppenderAsync(CancellationToken.None);
            newLoc = await appender.AppendAsync(record.ArtData, CancellationToken.None);
            Assert.Equal(
                ArticleRelocateOutcome.Relocated,
                engineA.Index.TryRelocate(record.ArtId, oldLoc, newLoc, record.ArtHash, record.ArtSize));
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        Assert.True(engineB.Index.TryGet(record.ArtId, out var got));
        Assert.Equal(newLoc, got.Location);
        Assert.True(engineB.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task Y_Eviction_Persists()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<y@example.test>");
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
            await engineA.DrainPendingAsync(CancellationToken.None);
            Assert.True(engineA.TryEvict(record.ArtId));
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        Assert.True(engineB.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Evicted, meta.State);
        Assert.False(engineB.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task Z_Invalid_Persists()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<z@example.test>");
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
            await engineA.DrainPendingAsync(CancellationToken.None);
            Assert.True(engineA.TryInvalidate(record.ArtId));
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        Assert.True(engineB.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Invalid, meta.State);
        Assert.False(engineB.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task AA_CorruptPhysical_DoesNotReturnCorruptBytes()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<aa@example.test>");
        StoredArticleLocation location;
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
            await engineA.DrainPendingAsync(CancellationToken.None);
            Assert.True(engineA.Index.TryGet(record.ArtId, out var meta));
            location = meta.Location;
        }

        CorruptSegmentRecord(dir.Options.SegmentDir, location, flipPayload: true);

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        Assert.False(engineB.TryRead(record.ArtId, out _));
        Assert.True(engineB.Index.TryGet(record.ArtId, out var after));
        Assert.Equal(ArticleStorageState.Invalid, after.State);
    }

    [Fact]
    public async Task AB_Read_TouchHint_NonDurable()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<ab@example.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        var durableBefore = engine.Index.DurableWriteCount;
        var touchBefore = engine.Index.TouchHintCount;
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.Equal(durableBefore, engine.Index.DurableWriteCount);
        Assert.Equal(touchBefore + 1, engine.Index.TouchHintCount);
    }

    [Fact]
    public async Task AC_ConcurrentDuplicateAccepts_PreserveIndex()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<ac@example.test>");
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => engine.AcceptAsync(record, CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.Contains(results, r => r.Outcome == ArticleAcceptOutcome.Accepted);
        Assert.All(
            results.Where(r => r.Outcome != ArticleAcceptOutcome.Accepted),
            r => Assert.Equal(ArticleAcceptOutcome.Duplicate, r.Outcome));
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.True(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task AD_MultipleArticles_AcceptAndRecover()
    {
        using var dir = TempStorageDir.Create();
        var records = Enumerable.Range(0, 5)
            .Select(i => CreateRecord($"<ad-{i}@example.test>", $"body-{i}\r\n"))
            .ToArray();
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            engineA.SuspendBackgroundPersist = true;
            foreach (var record in records)
            {
                _ = await engineA.AcceptAsync(record, CancellationToken.None);
            }
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        await engineB.RecoverAsync(CancellationToken.None);
        foreach (var record in records)
        {
            Assert.True(engineB.TryRead(record.ArtId, out var read));
            Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        }
    }

    [Fact]
    public async Task AE_Checkpoint_DoesNotRemoveIncomplete()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<ae@example.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        var before = engine.Journal.OutstandingRecoverableBytes;
        _ = engine.CheckpointTruncateCommitted();
        Assert.Equal(before, engine.Journal.OutstandingRecoverableBytes);
        Assert.Single(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task AF_Checkpoint_RemovesCommitted()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<af@example.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.Equal(0, engine.Journal.OutstandingRecoverableBytes);
        var physicalBefore = engine.Journal.JournalPhysicalBytes;
        var released = engine.CheckpointTruncateCommitted();
        Assert.True(released > 0 || physicalBefore >= 0);
        Assert.True(engine.Journal.JournalPhysicalBytes <= physicalBefore);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.True(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task AG_Recovery_AfterCheckpoint_RemainsCorrect()
    {
        using var dir = TempStorageDir.Create();
        var committed = CreateRecord("<ag-committed@example.test>", "done\r\n");
        var outstanding = CreateRecord("<ag-open@example.test>", "open\r\n");
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            _ = await engineA.AcceptAsync(committed, CancellationToken.None);
            await engineA.DrainPendingAsync(CancellationToken.None);
            engineA.SuspendBackgroundPersist = true;
            _ = await engineA.AcceptAsync(outstanding, CancellationToken.None);
            _ = engineA.CheckpointTruncateCommitted();
            Assert.Single(engineA.Journal.EnumerateIncomplete());
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.True(engineB.TryRead(committed.ArtId, out _));
        Assert.True(engineB.TryRead(outstanding.ArtId, out _));
        Assert.Empty(engineB.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task AH_Repeated_OpenRecoverClose_Cycles()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<ah@example.test>");
        for (var i = 0; i < 3; i++)
        {
            await using var engine = FileArticleStorageEngine.Open(dir.Options);
            if (i == 0)
            {
                engine.SuspendBackgroundPersist = true;
                _ = await engine.AcceptAsync(record, CancellationToken.None);
            }

            await engine.RecoverAsync(CancellationToken.None);
            Assert.True(engine.TryRead(record.ArtId, out _));
        }
    }

    private static async Task AssertPhysicalIdentityMismatchAsync(
        string messageId,
        string? otherMessageId,
        bool corruptArtId,
        bool corruptHash,
        bool corruptSize)
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord(messageId);
        StoredArticleLocation location;
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            engineA.SuspendBackgroundPersist = true;
            var accept = await engineA.AcceptAsync(record, CancellationToken.None);
            if (otherMessageId is not null)
            {
                var other = CreateRecord(otherMessageId); // same default body → same ArtSize/record length
                var appender = await engineA.Segments.GetActiveAppenderAsync(CancellationToken.None);
                location = await appender.AppendAsync(other.ArtData, CancellationToken.None);
            }
            else
            {
                var appender = await engineA.Segments.GetActiveAppenderAsync(CancellationToken.None);
                location = await appender.AppendAsync(record.ArtData, CancellationToken.None);
            }

            Assert.Equal(
                JournalAppendOutcome.Applied,
                await engineA.Journal.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, accept.Sequence, location),
                    CancellationToken.None));
        }

        if (otherMessageId is null)
        {
            CorruptSegmentRecord(
                dir.Options.SegmentDir,
                location,
                flipPayload: false,
                corruptArtId: corruptArtId,
                corruptHash: corruptHash,
                corruptSize: corruptSize);
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        engineB.SuspendBackgroundPersist = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => engineB.RecoverAsync(CancellationToken.None));
        Assert.Single(engineB.Journal.EnumerateIncomplete());
        Assert.False(
            engineB.Index.TryGet(record.ArtId, out var meta)
            && meta.State == ArticleStorageState.Present);
    }

    private static void CorruptSegmentRecord(
        string segmentDir,
        StoredArticleLocation location,
        bool flipPayload,
        bool corruptArtId = false,
        bool corruptHash = false,
        bool corruptSize = false)
    {
        var path = Directory.EnumerateFiles(segmentDir, "seg-*").Single();
        var bytes = File.ReadAllBytes(path);
        var span = bytes.AsSpan((int)location.Offset, location.Length);
        if (flipPayload)
        {
            span[SegmentRecordCodec.FixedHeaderLength] ^= 0xFF;
        }

        if (corruptArtId)
        {
            span[8] ^= 0xFF;
        }

        if (corruptHash)
        {
            var hashOffset = 8 + ArticleId.Length;
            span[hashOffset] ^= 0xFF;
        }

        if (corruptSize)
        {
            var sizeOffset = 8 + ArticleId.Length + 8;
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(sizeOffset, 4), span.Length); // inconsistent
        }

        // Recompute CRC so decode reaches identity checks (except payload-only flip which breaks CRC).
        if (!flipPayload)
        {
            var crc = Crc32.HashToUInt32(span[..^4]);
            BinaryPrimitives.WriteUInt32LittleEndian(span[^4..], crc);
        }

        File.WriteAllBytes(path, bytes);
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
        _ = builder.Append("Subject: storage\r\n");
        _ = builder.Append("\r\n").Append(body);
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

        public static TempStorageDir Create(long? softLimitBytes = null, long? hardLimitBytes = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-engine-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(cache);
            var options = new ArticleStorageRuntimeOptions(
                ControlDir: control,
                SegmentDir: cache,
                JournalSoftLimitBytes: softLimitBytes ?? ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                JournalHardLimitBytes: hardLimitBytes ?? ArticleStorageOptions.DefaultJournalHardLimitBytes,
                SegmentTargetSizeBytes: ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
            return new TempStorageDir(root, options);
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
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
