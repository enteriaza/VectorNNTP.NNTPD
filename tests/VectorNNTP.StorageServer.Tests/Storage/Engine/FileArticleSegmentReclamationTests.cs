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

/// <summary>Phase 4C.2: physical reclamation of one Retired segment.</summary>
public sealed class FileArticleSegmentReclamationTests
{
    [Fact]
    public async Task A_ReclaimValidRetired_DeletesFileAndCatalogue()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rcl-a@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sourceId = await AcceptCloseCompactRetireAsync(engine, record);

        var retiredPath = Path.Combine(
            dir.Options.SegmentDir,
            SegmentFileNames.Format(sourceId, SegmentFileKind.Retired));
        Assert.True(File.Exists(retiredPath));

        var result = await engine.ReclaimRetiredSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, result.Outcome);
        Assert.True(result.PhysicalFileDeleted);
        Assert.True(result.CatalogueEntryRemoved);
        Assert.False(File.Exists(retiredPath));
        Assert.False(engine.Segments.TryGetSegmentInfo(sourceId, out _));
    }

    [Fact]
    public async Task B_ActiveRejected_FileRemains()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rcl-b@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.True(engine.TryEvict(record.ArtId));

        var result = await engine.ReclaimRetiredSegmentAsync(meta.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.RejectedActive, result.Outcome);
        Assert.True(Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.active").Any());
    }

    [Fact]
    public async Task C_ClosedRejected_FileRemains()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rcl-c@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.True(engine.TryEvict(record.ArtId));

        var result = await engine.ReclaimRetiredSegmentAsync(meta.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.RejectedClosed, result.Outcome);
        Assert.True(Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.closed").Any());
    }

    [Fact]
    public async Task D_PresentReference_Rejected_IndexUnchanged()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rcl-d@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        // Force Retired without relocating (unsafe production path; seals Present@retired).
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var info));
        Assert.True(engine.Catalogue.TryRetire(meta.Location.SegmentId, info.Generation, DateTimeOffset.UtcNow));

        var result = await engine.ReclaimRetiredSegmentAsync(meta.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.RejectedPresentRemain, result.Outcome);
        Assert.True(engine.Index.TryGet(record.ArtId, out var after));
        Assert.Equal(ArticleStorageState.Present, after.State);
        Assert.True(Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.retired").Any());
    }

    [Fact]
    public async Task E_F_MissingAndAlreadyReclaimed()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rcl-e@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sourceId = await AcceptCloseCompactRetireAsync(engine, record);
        _ = await engine.ReclaimRetiredSegmentAsync(sourceId, CancellationToken.None);

        var missing = await engine.ReclaimRetiredSegmentAsync(new SegmentId(sourceId.Value + 999), CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.IdempotentAlreadyReclaimed, missing.Outcome);

        var again = await engine.ReclaimRetiredSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.IdempotentAlreadyReclaimed, again.Outcome);
    }

    [Fact]
    public async Task G_H_U_WrongPhysicalRepresentation_NotDeleted()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rcl-g@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sourceId = await AcceptCloseCompactRetireAsync(engine, record);
        var retiredPath = Path.Combine(
            dir.Options.SegmentDir,
            SegmentFileNames.Format(sourceId, SegmentFileKind.Retired));
        var closedPath = Path.Combine(
            dir.Options.SegmentDir,
            SegmentFileNames.Format(sourceId, SegmentFileKind.Closed));
        await File.WriteAllBytesAsync(closedPath, [0x00]);

        var result = await engine.ReclaimRetiredSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.RejectedUnexpectedPhysical, result.Outcome);
        Assert.True(File.Exists(retiredPath));
        Assert.True(File.Exists(closedPath));
    }

    [Fact]
    public async Task I_J_BeforeDeleteFault_NoFalseSuccess()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rcl-j@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sourceId = await AcceptCloseCompactRetireAsync(engine, record);
        engine.TestReclamationFaultPoint =
            FileArticleStorageEngine.ReclamationFaultPoint.BeforeDelete;
        await Assert.ThrowsAsync<IOException>(() =>
            engine.ReclaimRetiredSegmentAsync(sourceId, CancellationToken.None));
        Assert.True(Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.retired").Any());
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var info));
        Assert.Equal(SegmentState.Retired, info.State);
    }

    [Fact]
    public async Task K_W_CrashAfterDelete_RestartAbsent()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rcl-k@seg.test>");
        SegmentId sourceId;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            sourceId = await AcceptCloseCompactRetireAsync(engine, record);
            engine.TestReclamationFaultPoint =
                FileArticleStorageEngine.ReclamationFaultPoint.AfterDeleteBeforeCatalogueRemove;
            await Assert.ThrowsAsync<IOException>(() =>
                engine.ReclaimRetiredSegmentAsync(sourceId, CancellationToken.None));
            Assert.False(Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.retired").Any());
            Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var mid));
            Assert.Equal(SegmentState.Retired, mid.State);
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.False(engineB.Segments.TryGetSegmentInfo(sourceId, out _));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*"),
            p => p.Contains(sourceId.Value.ToString("D20"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task L_RestartAfterSuccessfulReclaim_RemainsAbsent()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rcl-l@seg.test>");
        SegmentId sourceId;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            sourceId = await AcceptCloseCompactRetireAsync(engine, record);
            var result = await engine.ReclaimRetiredSegmentAsync(sourceId, CancellationToken.None);
            Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, result.Outcome);
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.False(engineB.Segments.TryGetSegmentInfo(sourceId, out _));
    }

    [Fact]
    public async Task M_T_RestartWithRetiredPresent_RemainsRetired_NotWritable()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rcl-m@seg.test>");
        SegmentId sourceId;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            sourceId = await AcceptCloseCompactRetireAsync(engine, record);
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.True(engineB.Segments.TryGetSegmentInfo(sourceId, out var info));
        Assert.Equal(SegmentState.Retired, info.State);
        var active = await engineB.Segments.GetActiveAppenderAsync(CancellationToken.None);
        Assert.NotEqual(sourceId, active.SegmentId);
    }

    [Fact]
    public async Task N_ConcurrentReclaim_NoCorruption()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rcl-n@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sourceId = await AcceptCloseCompactRetireAsync(engine, record);

        var t1 = engine.ReclaimRetiredSegmentAsync(sourceId, CancellationToken.None);
        var t2 = engine.ReclaimRetiredSegmentAsync(sourceId, CancellationToken.None);
        var results = await Task.WhenAll(t1, t2);
        Assert.Contains(results, r => r.Outcome is ArticleSegmentReclamationOutcome.Reclaimed
            or ArticleSegmentReclamationOutcome.IdempotentAlreadyReclaimed);
        Assert.All(
            results,
            r => Assert.True(
                r.Outcome is ArticleSegmentReclamationOutcome.Reclaimed
                    or ArticleSegmentReclamationOutcome.IdempotentAlreadyReclaimed
                    or ArticleSegmentReclamationOutcome.Failed));
        Assert.False(engine.Segments.TryGetSegmentInfo(sourceId, out _));
        Assert.Empty(Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.retired"));
    }

    [Fact]
    public async Task O_RetiredReadRejected()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rcl-o@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var loc = meta.Location;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(loc.SegmentId, out var info));
        Assert.True(engine.Catalogue.TryRetire(loc.SegmentId, info.Generation, DateTimeOffset.UtcNow));
        Assert.False(engine.Segments.TryRead(loc, out _));
    }

    [Fact]
    public async Task P_EvictionRace_NoResurrection()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rcl-p@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sourceId = await AcceptCloseCompactRetireAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.NotEqual(sourceId, meta.Location.SegmentId);
        Assert.True(engine.TryEvict(record.ArtId));
        var result = await engine.ReclaimRetiredSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, result.Outcome);
        Assert.True(engine.Index.TryGet(record.ArtId, out var after));
        Assert.Equal(ArticleStorageState.Evicted, after.State);
        Assert.NotEqual(sourceId, after.Location.SegmentId);
    }

    [Fact]
    public async Task Q_Accounting_OtherSegmentsUnchanged()
    {
        using var dir = TempStorageDir.Create();
        var a = CreateRecord("<rcl-q-a@seg.test>");
        var b = CreateRecord("<rcl-q-b@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(a, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(b, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(a.ArtId, out var metaA));
        Assert.True(engine.Index.TryGet(b.ArtId, out var metaB));
        var compact = await engine.CompactClosedSegmentAsync(metaA.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        _ = await engine.RetireCompactedSegmentAsync(compact.CompactionId, CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(metaB.Location.SegmentId, out var beforeB));

        var result = await engine.ReclaimRetiredSegmentAsync(metaA.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, result.Outcome);
        Assert.True(engine.Segments.TryGetSegmentInfo(metaB.Location.SegmentId, out var afterB));
        Assert.Equal(beforeB.SizeBytes, afterB.SizeBytes);
        Assert.Equal(beforeB.LiveBytes, afterB.LiveBytes);
        Assert.Equal(beforeB.DeadBytes, afterB.DeadBytes);
    }

    [Fact]
    public async Task R_CacheUntouched()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rcl-r@seg.test>");
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.TryRead(record.ArtId, out _));
        var puts = cache.PutCount;
        var removes = cache.RemoveCount;
        var sourceId = await CloseCompactRetireAsync(engine, record);

        var result = await engine.ReclaimRetiredSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, result.Outcome);
        Assert.Equal(puts, cache.PutCount);
        Assert.Equal(removes, cache.RemoveCount);
    }

    [Fact]
    public async Task S_NoSataDiscovery_UsesIndex()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rcl-s@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sourceId = await AcceptCloseCompactRetireAsync(engine, record);
        Assert.Equal(
            0,
            engine.Index.Snapshot()
                .Count(m => m.State == ArticleStorageState.Present && m.Location.SegmentId.Value == sourceId.Value));
        var result = await engine.ReclaimRetiredSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, result.Outcome);
    }

    [Fact]
    public async Task V_CancellationBeforeDelete()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<rcl-v@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var sourceId = await AcceptCloseCompactRetireAsync(engine, record);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.ReclaimRetiredSegmentAsync(sourceId, cts.Token));
        Assert.True(Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.retired").Any());
    }

    private static async Task<SegmentId> AcceptCloseCompactRetireAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        return await CloseCompactRetireAsync(engine, record);
    }

    private static async Task<SegmentId> CloseCompactRetireAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record)
    {
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var compact = await engine.CompactClosedSegmentAsync(meta.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        var retire = await engine.RetireCompactedSegmentAsync(compact.CompactionId, CancellationToken.None);
        Assert.Equal(ArticleSegmentRetirementOutcome.Retired, retire.Outcome);
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
        _ = builder.Append("Subject: reclaim\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-reclaim-" + Guid.NewGuid().ToString("N"));
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
