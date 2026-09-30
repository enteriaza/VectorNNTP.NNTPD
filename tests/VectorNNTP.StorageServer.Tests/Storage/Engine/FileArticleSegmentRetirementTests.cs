using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>Phase 4C.1: durable CompactionRetired + Closed→Retired catalogue fence.</summary>
public sealed class FileArticleSegmentRetirementTests
{
    [Fact]
    public async Task A_NormalRetirement_CatalogueRetired_FilePreserved()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<ret-a@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sourceId = await AcceptCloseCompactAsync(engine, record);

        Assert.True(engine.Journal.TryGetCompaction(
            engine.Journal.EnumerateCompactions().Single(c => c.Begin.SourceSegmentId.Value == sourceId.Value).Begin.CompactionId,
            out var before));
        var compactionId = before.Begin.CompactionId;

        var result = await engine.RetireCompactedSegmentAsync(compactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, result.Outcome);
        Assert.True(result.CompactionRetiredAppended);
        Assert.True(engine.Journal.TryGetCompaction(compactionId, out var snap));
        Assert.NotNull(snap.Retired);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var info));
        Assert.Equal(SegmentState.Retired, info.State);

        var retiredFiles = Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.retired").ToArray();
        Assert.Single(retiredFiles);
        Assert.True(new FileInfo(retiredFiles[0]).Length > 0);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(dir.Options.SegmentDir),
            p => p.EndsWith(".closed", StringComparison.OrdinalIgnoreCase)
                 && p.Contains(sourceId.Value.ToString("D20"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task B_MissingCompaction_Rejected()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var result = await engine.RetireCompactedSegmentAsync(999, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.RejectedUnknownCompaction, result.Outcome);
        Assert.False(result.CompactionRetiredAppended);
    }

    [Fact]
    public async Task C_ActiveSource_Rejected()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<ret-c@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.True(engine.TryEvict(record.ArtId)); // Present fence would otherwise fire first
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var info));
        Assert.Equal(SegmentState.Active, info.State);

        var compactionId = engine.Journal.AllocateCompactionId();
        _ = await engine.Journal.AppendCompactionBeginAsync(
            new JournalCompactionBeginRecord(1, compactionId, meta.Location.SegmentId, info.Generation),
            CancellationToken.None);
        _ = await engine.Journal.AppendCompactionCommittedAsync(
            new JournalCompactionCommittedRecord(1, compactionId),
            CancellationToken.None);

        var result = await engine.RetireCompactedSegmentAsync(compactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.RejectedSourceNotClosed, result.Outcome);
        Assert.False(result.CompactionRetiredAppended);
    }

    [Fact]
    public async Task D_ClosedButNotCommitted_Rejected()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<ret-d@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var info));
        var compactionId = engine.Journal.AllocateCompactionId();
        _ = await engine.Journal.AppendCompactionBeginAsync(
            new JournalCompactionBeginRecord(1, compactionId, meta.Location.SegmentId, info.Generation),
            CancellationToken.None);

        var result = await engine.RetireCompactedSegmentAsync(compactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.RejectedNotCommitted, result.Outcome);
        Assert.True(engine.Journal.TryGetCompaction(compactionId, out var snap));
        Assert.Null(snap.Retired);
    }

    [Fact]
    public async Task E_PresentSourceReference_Rejected()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<ret-e@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var info));
        var compactionId = engine.Journal.AllocateCompactionId();
        _ = await engine.Journal.AppendCompactionBeginAsync(
            new JournalCompactionBeginRecord(1, compactionId, meta.Location.SegmentId, info.Generation),
            CancellationToken.None);
        // Force CompactionCommitted without relocating (simulates corrupt/incomplete commit).
        _ = await engine.Journal.AppendCompactionCommittedAsync(
            new JournalCompactionCommittedRecord(1, compactionId),
            CancellationToken.None);

        var result = await engine.RetireCompactedSegmentAsync(compactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.RejectedPresentRemain, result.Outcome);
        Assert.True(engine.Journal.TryGetCompaction(compactionId, out var snap));
        Assert.Null(snap.Retired);
    }

    [Fact]
    public async Task F_Q_AlreadyRetired_Idempotent()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<ret-f@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sourceId = await AcceptCloseCompactAsync(engine, record);
        var compactionId = engine.Journal.EnumerateCompactions()
            .Single(c => c.Begin.SourceSegmentId.Value == sourceId.Value).Begin.CompactionId;

        var first = await engine.RetireCompactedSegmentAsync(compactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, first.Outcome);
        Assert.True(first.CompactionRetiredAppended);

        var second = await engine.RetireCompactedSegmentAsync(compactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.IdempotentNoOp, second.Outcome);
        Assert.False(second.CompactionRetiredAppended);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var info));
        Assert.Equal(SegmentState.Retired, info.State);
    }

    [Fact]
    public async Task G_WrongGeneration_Conflict()
    {
        using var dir = TempControlDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        _ = await journal.AppendCompactionBeginAsync(
            new JournalCompactionBeginRecord(1, 7, new SegmentId(3), 11),
            CancellationToken.None);
        _ = await journal.AppendCompactionCommittedAsync(
            new JournalCompactionCommittedRecord(1, 7),
            CancellationToken.None);

        Assert.Equal(
            JournalAppendOutcome.Conflict,
            await journal.AppendCompactionRetiredAsync(
                new JournalCompactionRetiredRecord(1, 7, new SegmentId(3), 99),
                CancellationToken.None));
        Assert.True(journal.TryGetCompaction(7, out var snap));
        Assert.Null(snap.Retired);
    }

    [Fact]
    public async Task H_WrongSource_Conflict()
    {
        using var dir = TempControlDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        _ = await journal.AppendCompactionBeginAsync(
            new JournalCompactionBeginRecord(1, 8, new SegmentId(3), 11),
            CancellationToken.None);
        _ = await journal.AppendCompactionCommittedAsync(
            new JournalCompactionCommittedRecord(1, 8),
            CancellationToken.None);

        Assert.Equal(
            JournalAppendOutcome.Conflict,
            await journal.AppendCompactionRetiredAsync(
                new JournalCompactionRetiredRecord(1, 8, new SegmentId(9), 11),
                CancellationToken.None));
    }

    [Fact]
    public async Task O_RetiredWithoutCommitted_Rejected()
    {
        using var dir = TempControlDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        _ = await journal.AppendCompactionBeginAsync(
            new JournalCompactionBeginRecord(1, 5, new SegmentId(1), 2),
            CancellationToken.None);
        Assert.Equal(
            JournalAppendOutcome.Rejected,
            await journal.AppendCompactionRetiredAsync(
                new JournalCompactionRetiredRecord(1, 5, new SegmentId(1), 2),
                CancellationToken.None));
    }

    [Fact]
    public async Task I_CrashBeforeCompactionRetired_RetrySucceeds()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<ret-i@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sourceId = await AcceptCloseCompactAsync(engine, record);
        var compactionId = engine.Journal.EnumerateCompactions()
            .Single(c => c.Begin.SourceSegmentId.Value == sourceId.Value).Begin.CompactionId;

        engine.TestRetirementFaultPoint =
            FileArticleStorageEngine.RetirementFaultPoint.BeforeCompactionRetired;
        await Assert.ThrowsAsync<IOException>(() =>
            engine.RetireCompactedSegmentAsync(compactionId, CancellationToken.None));
        Assert.True(engine.Journal.TryGetCompaction(compactionId, out var mid));
        Assert.Null(mid.Retired);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var closed));
        Assert.Equal(SegmentState.Closed, closed.State);

        var result = await engine.RetireCompactedSegmentAsync(compactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, result.Outcome);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var retired));
        Assert.Equal(SegmentState.Retired, retired.State);
    }

    [Fact]
    public async Task J_N_CrashAfterCompactionRetired_RecoveryReconstructsRetired()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<ret-j@seg.test>");
        ulong compactionId;
        SegmentId sourceId;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            sourceId = await AcceptCloseCompactAsync(engine, record);
            compactionId = engine.Journal.EnumerateCompactions()
                .Single(c => c.Begin.SourceSegmentId.Value == sourceId.Value).Begin.CompactionId;
            engine.TestRetirementFaultPoint =
                FileArticleStorageEngine.RetirementFaultPoint.AfterCompactionRetiredBeforeCatalogue;
            await Assert.ThrowsAsync<IOException>(() =>
                engine.RetireCompactedSegmentAsync(compactionId, CancellationToken.None));
            Assert.True(engine.Journal.TryGetCompaction(compactionId, out var mid));
            Assert.NotNull(mid.Retired);
            Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var stillClosed));
            Assert.Equal(SegmentState.Closed, stillClosed.State);
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.True(engineB.Segments.TryGetSegmentInfo(sourceId, out var after));
        Assert.Equal(SegmentState.Retired, after.State);
        Assert.True(engineB.Journal.TryGetCompaction(compactionId, out var snap));
        Assert.NotNull(snap.Retired);
    }

    [Fact]
    public async Task K_L_PhysicalFileAndAccountingUnchangedExtents()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<ret-k@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sourceId = await AcceptCloseCompactAsync(engine, record);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var before));
        var size = before.SizeBytes;
        var live = before.LiveBytes;
        var dead = before.DeadBytes;
        var compactionId = engine.Journal.EnumerateCompactions()
            .Single(c => c.Begin.SourceSegmentId.Value == sourceId.Value).Begin.CompactionId;

        var result = await engine.RetireCompactedSegmentAsync(compactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, result.Outcome);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var after));
        Assert.Equal(size, after.SizeBytes);
        Assert.Equal(live, after.LiveBytes);
        Assert.Equal(dead, after.DeadBytes);
        Assert.Single(Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.retired"));
    }

    [Fact]
    public async Task M_CacheUntouched()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<ret-m@seg.test>");
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.TryRead(record.ArtId, out _));
        var puts = cache.PutCount;
        var removes = cache.RemoveCount;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var compact = await engine.CompactClosedSegmentAsync(meta.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);

        var result = await engine.RetireCompactedSegmentAsync(compact.CompactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, result.Outcome);
        Assert.Equal(puts, cache.PutCount);
        Assert.Equal(removes, cache.RemoveCount);
    }

    [Fact]
    public async Task N_CommittedNotRetired_RemainsClosed_AfterRestart()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<ret-n@seg.test>");
        SegmentId sourceId;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            sourceId = await AcceptCloseCompactAsync(engine, record);
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.True(engineB.Segments.TryGetSegmentInfo(sourceId, out var info));
        Assert.Equal(SegmentState.Closed, info.State);
        var open = engineB.Journal.EnumerateOpenCompactions()
            .Single(c => c.Begin.SourceSegmentId.Value == sourceId.Value);
        Assert.True(open.Committed);
        Assert.Null(open.Retired);
    }

    [Fact]
    public async Task P_BeginGenerationCarriedInRetiredRecord()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<ret-p@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sourceId = await AcceptCloseCompactAsync(engine, record);
        var begin = engine.Journal.EnumerateCompactions()
            .Single(c => c.Begin.SourceSegmentId.Value == sourceId.Value).Begin;

        var result = await engine.RetireCompactedSegmentAsync(begin.CompactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, result.Outcome);
        Assert.Equal(begin.SourceGeneration, result.SourceGeneration);
        Assert.True(engine.Journal.TryGetCompaction(begin.CompactionId, out var snap));
        Assert.Equal(begin.SourceGeneration, snap.Retired!.Value.ExpectedGeneration);
        Assert.Equal(begin.SourceSegmentId, snap.Retired.Value.SourceSegmentId);
    }

    [Fact]
    public async Task R_EvictedInvalid_NotPresent_AllowsRetirement()
    {
        using var dir = TempStorageDir.Create();
        var keep = CreateRecord("<ret-r-keep@seg.test>");
        var drop = CreateRecord("<ret-r-drop@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(keep, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(drop, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(drop.ArtId));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();
        Assert.True(engine.Index.TryGet(keep.ArtId, out var meta));
        var compact = await engine.CompactClosedSegmentAsync(meta.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);

        var result = await engine.RetireCompactedSegmentAsync(compact.CompactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, result.Outcome);
        Assert.True(engine.Index.TryGet(drop.ArtId, out var dropMeta));
        Assert.Equal(ArticleStorageState.Evicted, dropMeta.State);
    }

    [Fact]
    public async Task S_NoSataDiscovery_UsesIndexAndJournal()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<ret-s@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sourceId = await AcceptCloseCompactAsync(engine, record);
        var present = engine.Index.Snapshot()
            .Count(m => m.State == ArticleStorageState.Present && m.Location.SegmentId.Value == sourceId.Value);
        Assert.Equal(0, present);
        var compactionId = engine.Journal.EnumerateCompactions()
            .Single(c => c.Begin.SourceSegmentId.Value == sourceId.Value).Begin.CompactionId;
        var result = await engine.RetireCompactedSegmentAsync(compactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, result.Outcome);
    }

    private static async Task<SegmentId> AcceptCloseCompactAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();
        var compact = await engine.CompactClosedSegmentAsync(meta.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        return meta.Location.SegmentId;
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
        _ = builder.Append("Subject: retire\r\n");
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

        public static TempStorageDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-retire-" + Guid.NewGuid().ToString("N"));
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
            catch
            {
                // best-effort
            }
        }
    }

    private sealed class TempControlDir : IDisposable
    {
        private TempControlDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public string Root { get; }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempControlDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-retire-ctl-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            Directory.CreateDirectory(control);
            return new TempControlDir(
                root,
                new ArticleStorageRuntimeOptions(
                    ControlDir: control,
                    SegmentDir: Path.Combine(root, "cache"),
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
            catch
            {
                // best-effort
            }
        }
    }

    private sealed class RecordingArticleMemoryCache : IArticleMemoryCache
    {
        private long _put;
        private long _remove;

        public RecordingArticleMemoryCache(IArticleMemoryCache inner) => Inner = inner;

        public IArticleMemoryCache Inner { get; }

        public long MaxBytes => Inner.MaxBytes;

        public long CurrentBytes => Inner.CurrentBytes;

        public int Count => Inner.Count;

        public int PutCount => (int)Interlocked.Read(ref _put);

        public int RemoveCount => (int)Interlocked.Read(ref _remove);

        public bool TryGet(ArticleId artId, out ArticleRecord record) => Inner.TryGet(artId, out record);

        public ArticleMemoryCachePutOutcome Put(in ArticleRecord record)
        {
            _ = Interlocked.Increment(ref _put);
            return Inner.Put(in record);
        }

        public bool Remove(ArticleId artId)
        {
            _ = Interlocked.Increment(ref _remove);
            return Inner.Remove(artId);
        }

        public void Clear() => Inner.Clear();
    }
}
