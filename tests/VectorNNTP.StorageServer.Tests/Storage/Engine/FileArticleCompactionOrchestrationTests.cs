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

/// <summary>Phase 4B.4: single-Closed-segment compaction orchestration.</summary>
public sealed class FileArticleCompactionOrchestrationTests
{
    [Fact]
    public async Task A_ZeroLive_Commits_SourceRemainsClosed()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<co-a@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(record.ArtId));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;

        var result = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, result.Outcome);
        Assert.Equal(0, result.InitialCandidateCount);
        Assert.True(result.CompactionCommittedAppended);
        Assert.True(engine.Journal.TryGetCompaction(result.CompactionId, out var snap));
        Assert.True(snap.Committed);
        Assert.Null(snap.Retired);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var info));
        Assert.Equal(SegmentState.Closed, info.State);
        Assert.Empty(Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.retired"));
    }

    [Fact]
    public async Task B_OneLive_RelocatesAndCommits()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<co-b@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, _) = await AcceptCloseAsync(engine, record);

        var result = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, result.Outcome);
        Assert.Equal(1, result.InitialCandidateCount);
        Assert.Equal(1, result.RelocatedCount);
        Assert.Equal(0, result.RemainingPresentOnSource);
        Assert.True(result.CompactionCommittedAppended);

        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.NotEqual(sourceId, meta.Location.SegmentId);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var src));
        Assert.Equal(SegmentState.Closed, src.State);
        Assert.True(SegmentLifecycle.IsReclaimable(src));
    }

    [Fact]
    public async Task C_L_MultipleLive_DeterministicOrderAndIds()
    {
        using var dir = TempStorageDir.Create();
        var records = Enumerable.Range(0, 4)
            .Select(i => CreateRecord($"<co-c-{i}@seg.test>"))
            .OrderBy(r => r.ArtId, ArticleIdByteComparer.Instance)
            .ToArray();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        foreach (var r in records)
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(r, CancellationToken.None)).Outcome);
        }

        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(records[0].ArtId, out var meta0));
        var sourceId = meta0.Location.SegmentId;

        var result = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, result.Outcome);
        Assert.Equal(4, result.InitialCandidateCount);
        Assert.Equal(4, result.RelocatedCount);

        Assert.True(engine.Journal.TryGetCompaction(result.CompactionId, out var snap));
        var byArt = snap.Relocations.OrderBy(r => r.Intent.ArtId, ArticleIdByteComparer.Instance).ToArray();
        Assert.Equal(4, byArt.Length);
        for (ulong i = 0; i < 4; i++)
        {
            Assert.Equal(i + 1, byArt[i].Intent.RelocationId);
            Assert.Equal(records[i].ArtId, byArt[i].Intent.ArtId);
        }
    }

    [Fact]
    public async Task D_EvictionRace_Abandoned_StillCommits()
    {
        using var dir = TempStorageDir.Create();
        var keep = CreateRecord("<co-d-keep@seg.test>");
        var drop = CreateRecord("<co-d-drop@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(keep, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(drop, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(keep.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;

        engine.TestHookBeforeRelocateArticle = artId =>
        {
            if (artId == drop.ArtId)
            {
                Assert.True(engine.TryEvict(drop.ArtId));
            }
        };

        var result = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, result.Outcome);
        Assert.True(result.AbandonedCount >= 1);
        Assert.True(engine.Index.TryGet(drop.ArtId, out var dropMeta));
        Assert.Equal(ArticleStorageState.Evicted, dropMeta.State);
        Assert.True(engine.Index.TryGet(keep.ArtId, out var keepMeta));
        Assert.Equal(ArticleStorageState.Present, keepMeta.State);
        Assert.NotEqual(sourceId, keepMeta.Location.SegmentId);
    }

    [Fact]
    public async Task E_InvalidationRace_Abandoned_StillCommits()
    {
        using var dir = TempStorageDir.Create();
        var keep = CreateRecord("<co-e-keep@seg.test>");
        var drop = CreateRecord("<co-e-drop@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(keep, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(drop, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(keep.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;

        engine.TestHookBeforeRelocateArticle = artId =>
        {
            if (artId == drop.ArtId)
            {
                Assert.True(engine.TryInvalidate(drop.ArtId));
            }
        };

        var result = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, result.Outcome);
        Assert.True(result.AbandonedCount >= 1);
        Assert.True(engine.Index.TryGet(drop.ArtId, out var dropMeta));
        Assert.Equal(ArticleStorageState.Invalid, dropMeta.State);
    }

    [Fact]
    public async Task F_ConcurrentRelocation_NoResurrection()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<co-f@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, _) = await AcceptCloseAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var sourceLoc = meta.Location;

        // Move the article elsewhere before orchestration RelocateArticle runs.
        engine.TestHookBeforeRelocateArticle = _ =>
        {
            var appender = engine.Segments.GetActiveAppenderAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            var dest = appender.AppendAsync(record.ArtData, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            Assert.Equal(
                ArticleRelocateOutcome.Relocated,
                engine.Index.TryRelocate(record.ArtId, sourceLoc, dest, record.ArtHash, record.ArtSize));
        };

        var result = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, result.Outcome);
        Assert.True(engine.Index.TryGet(record.ArtId, out var after));
        Assert.Equal(ArticleStorageState.Present, after.State);
        Assert.NotEqual(sourceId, after.Location.SegmentId);
    }

    [Fact]
    public async Task G_PresentRemains_Incomplete_NoCommit()
    {
        using var dir = TempStorageDir.Create();
        var live = CreateRecord("<co-g-live@seg.test>");
        var phantom = CreateRecord("<co-g-phantom@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, _) = await AcceptCloseAsync(engine, live);
        Assert.True(engine.Index.TryGet(live.ArtId, out var liveMeta));

        // After worklist snapshot, inject an additional Present@source index entry so the
        // fresh completion check fails (index-authoritative Incomplete).
        engine.TestHookBeforeRelocateArticle = _ =>
        {
            Assert.True(engine.Index.TryCommitPresent(new StoredArticleMetadata(
                phantom.ArtId,
                phantom.ArtHash,
                phantom.ArtSize,
                liveMeta.Location,
                ArticleStorageState.Present,
                DateTimeOffset.UtcNow)));
        };

        var result = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Incomplete, result.Outcome);
        Assert.True(result.RemainingPresentOnSource >= 1);
        Assert.False(result.CompactionCommittedAppended);
        Assert.True(engine.Journal.TryGetCompaction(result.CompactionId, out var snap));
        Assert.False(snap.Committed);
    }

    [Fact]
    public async Task H_CancellationBeforeWork_NoCommit()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<co-h@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, _) = await AcceptCloseAsync(engine, record);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.CompactClosedSegmentAsync(sourceId, cts.Token));
        Assert.Empty(engine.Journal.EnumerateOpenCompactions());
    }

    [Fact]
    public async Task I_CancellationDuringWork_NoCommit()
    {
        using var dir = TempStorageDir.Create();
        var a = CreateRecord("<co-i-a@seg.test>");
        var b = CreateRecord("<co-i-b@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(a, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(b, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(a.ArtId, out var meta));
        var sourceId = meta.Location.SegmentId;

        using var cts = new CancellationTokenSource();
        var cancelOn = a.ArtId.ToLowerHexString().CompareTo(b.ArtId.ToLowerHexString(), StringComparison.Ordinal) < 0
            ? a.ArtId
            : b.ArtId;
        engine.TestHookBeforeRelocateArticle = artId =>
        {
            if (artId == cancelOn)
            {
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.CompactClosedSegmentAsync(sourceId, cts.Token));
        Assert.True(engine.Journal.TryGetCompaction(
            engine.Journal.EnumerateOpenCompactions().Single(c => c.Begin.SourceSegmentId.Value == sourceId.Value).Begin.CompactionId,
            out var snap));
        Assert.False(snap.Committed);
    }

    [Fact]
    public async Task J_RelocationDurabilityFailure_NoCommit()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<co-j@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, _) = await AcceptCloseAsync(engine, record);
        engine.TestRelocationFaultPoint =
            FileArticleStorageEngine.RelocationFaultPoint.AfterWrittenBeforeIndex;
        await Assert.ThrowsAsync<IOException>(() =>
            engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None));
        var open = engine.Journal.EnumerateOpenCompactions()
            .Single(c => c.Begin.SourceSegmentId.Value == sourceId.Value);
        Assert.False(open.Committed);
    }

    [Fact]
    public async Task M_ActiveRejected_RetiredRejected_ClosedAccepted()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<co-m@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        var activeId = meta.Location.SegmentId;
        var active = await engine.CompactClosedSegmentAsync(activeId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.RejectedSourceNotClosed, active.Outcome);

        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(activeId, out var closed));
        Assert.True(engine.Catalogue.TryRetire(activeId, closed.Generation, DateTimeOffset.UtcNow));
        var retired = await engine.CompactClosedSegmentAsync(activeId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.RejectedSourceNotClosed, retired.Outcome);
    }

    [Fact]
    public async Task N_GenerationPreservedFromBegin()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<co-n@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, generation) = await AcceptCloseAsync(engine, record);

        var observed = new List<ulong>();
        engine.TestHookBeforeRelocateArticle = _ =>
        {
            var begin = engine.Journal.EnumerateOpenCompactions()
                .Single(c => c.Begin.SourceSegmentId.Value == sourceId.Value).Begin;
            observed.Add(begin.SourceGeneration);
            Assert.Equal(generation, begin.SourceGeneration);
        };

        var result = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, result.Outcome);
        Assert.Equal(generation, result.SourceGeneration);
        Assert.All(observed, g => Assert.Equal(generation, g));
    }

    [Fact]
    public async Task P_ContinuesExistingOpenCompaction()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<co-p@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, _) = await AcceptCloseAsync(engine, record);

        engine.TestRelocationFaultPoint =
            FileArticleStorageEngine.RelocationFaultPoint.AfterIntentBeforeAppend;
        await Assert.ThrowsAsync<IOException>(() =>
            engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None));
        var firstId = engine.Journal.EnumerateOpenCompactions()
            .Single(c => c.Begin.SourceSegmentId.Value == sourceId.Value).Begin.CompactionId;

        var result = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, result.Outcome);
        Assert.Equal(firstId, result.CompactionId);
        Assert.Equal(1, engine.Journal.EnumerateOpenCompactions()
            .Count(c => c.Begin.SourceSegmentId.Value == sourceId.Value && !c.Committed) +
            engine.Journal.EnumerateOpenCompactions()
                .Count(c => c.Begin.SourceSegmentId.Value == sourceId.Value && c.Committed));
        Assert.Equal(1, engine.Journal.EnumerateOpenCompactions()
            .Count(c => c.Begin.SourceSegmentId.Value == sourceId.Value));
    }

    [Fact]
    public async Task Q_AlreadyCommitted_DoesNotDuplicateCommit()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<co-q@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, _) = await AcceptCloseAsync(engine, record);
        var first = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, first.Outcome);
        Assert.True(first.CompactionCommittedAppended);

        var second = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, second.Outcome);
        Assert.Equal(first.CompactionId, second.CompactionId);
        Assert.False(second.CompactionCommittedAppended);
        Assert.Equal("commit-idempotent", second.Reason);
    }

    [Fact]
    public async Task R_AccountingViaRelocationPrimitive()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<co-r@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, _) = await AcceptCloseAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var before));
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var srcBefore));
        Assert.True(srcBefore.LiveBytes >= before.Location.Length);

        var result = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, result.Outcome);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var srcAfter));
        Assert.Equal(0, srcAfter.LiveBytes);
        Assert.True(srcAfter.DeadBytes >= before.Location.Length);
        Assert.True(engine.Index.TryGet(record.ArtId, out var after));
        Assert.True(engine.Segments.TryGetSegmentInfo(after.Location.SegmentId, out var dest));
        Assert.True(dest.LiveBytes >= after.Location.Length);
    }

    [Fact]
    public async Task S_CacheUntouchedByOrchestration()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<co-s@seg.test>");
        var cache = new RecordingArticleMemoryCache(new ArticleMemoryCache(4L * 1024 * 1024));
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.TryRead(record.ArtId, out _));
        var puts = cache.PutCount;
        var removes = cache.RemoveCount;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));

        var result = await engine.CompactClosedSegmentAsync(meta.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, result.Outcome);
        Assert.Equal(puts, cache.PutCount);
        Assert.Equal(removes, cache.RemoveCount);
        Assert.True(cache.Inner.TryGet(record.ArtId, out _));
    }

    [Fact]
    public async Task O_WorklistFromIndexOnly_NoSegmentScanApi()
    {
        // Orchestration uses Index.Snapshot only; FileSegmentStore has no article-discovery scan API.
        // Prove candidates match index Present@source, not file enumeration.
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<co-o@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, _) = await AcceptCloseAsync(engine, record);
        var indexPresent = engine.Index.Snapshot()
            .Count(m => m.State == ArticleStorageState.Present && m.Location.SegmentId.Value == sourceId.Value);
        var result = await engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None);
        Assert.Equal(indexPresent, result.InitialCandidateCount);
        Assert.Equal(ArticleCompactionOutcome.Committed, result.Outcome);
    }

    private static async Task<(SegmentId SourceId, ulong Generation)> AcceptCloseAsync(
        FileArticleStorageEngine engine,
        ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var info));
        return (meta.Location.SegmentId, info.Generation);
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
        _ = builder.Append("Subject: compact-orch\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class ArticleIdByteComparer : IComparer<ArticleId>
    {
        public static ArticleIdByteComparer Instance { get; } = new();

        public int Compare(ArticleId x, ArticleId y)
        {
            Span<byte> left = stackalloc byte[ArticleId.Length];
            Span<byte> right = stackalloc byte[ArticleId.Length];
            x.CopyTo(left);
            y.CopyTo(right);
            return left.SequenceCompareTo(right);
        }
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-compact-orch-" + Guid.NewGuid().ToString("N"));
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
