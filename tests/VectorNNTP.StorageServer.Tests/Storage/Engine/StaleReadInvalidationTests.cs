using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Phase 5F.11: a failed physical read may invalidate only the location that was just read.
/// </summary>
public sealed class StaleReadInvalidationTests
{
    [Fact]
    public async Task A_StaleReadAfterRelocationAndRetirement_DoesNotInvalidateDestination()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<p5f11-a@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, _, sourceLoc) = await AcceptCloseAndLocateAsync(engine, record);
        engine.CompleteUnreferencedExtentAccounting();

        engine.TestHookAfterIndexSnapshotBeforeSegmentRead = (_, location) =>
        {
            Assert.Equal(sourceLoc, location);
            var compact = engine.CompactClosedSegmentAsync(sourceId, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
            var retired = engine.RetireCompactedSegmentAsync(compact.CompactionId, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Assert.Equal(ArticleSegmentRetirementOutcome.Retired, retired.Outcome);
        };

        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.NotEqual(sourceLoc, meta.Location);
        Assert.Equal(meta.Location, read.Metadata.Location);
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var source));
        Assert.Equal(SegmentState.Retired, source.State);

        _ = engine.ArticleCache.Remove(record.ArtId);
        Assert.True(engine.TryRead(record.ArtId, out var again));
        Assert.True(again.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.True(engine.Index.TryGet(record.ArtId, out meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
    }

    [Fact]
    public async Task B_FailedReadOfCurrentLocation_StillInvalidates()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<p5f11-b@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (_, _, sourceLoc) = await AcceptCloseAndLocateAsync(engine, record);
        CorruptRecordPayload(dir.Options.SegmentDir, sourceLoc);

        Assert.False(engine.TryRead(record.ArtId, out _));
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Invalid, meta.State);
        Assert.Equal(sourceLoc, meta.Location);
    }

    [Fact]
    public async Task C_LocationChangesToValidDestination_RetriesWithoutInvalidating()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<p5f11-c@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, generation, sourceLoc) = await AcceptCloseAndLocateAsync(engine, record);

        engine.TestHookAfterIndexSnapshotBeforeSegmentRead = (_, location) =>
        {
            Assert.Equal(sourceLoc, location);
            var compactionId = engine.Journal.AllocateCompactionId();
            Assert.Equal(
                JournalAppendOutcome.Applied,
                engine.Journal.AppendCompactionBeginAsync(
                        new JournalCompactionBeginRecord(1, compactionId, sourceId, generation),
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult());
            var relocated = engine.RelocateArticleAsync(
                    compactionId,
                    1,
                    sourceId,
                    generation,
                    record.ArtId,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Assert.Equal(ArticleRelocationOutcome.Relocated, relocated.Outcome);
            CorruptRecordPayload(dir.Options.SegmentDir, sourceLoc);
        };

        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.NotEqual(sourceLoc, meta.Location);
        Assert.Equal(meta.Location, read.Metadata.Location);
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task D_EvictionDuringStaleRead_DoesNotInvalidateOrResurrect()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<p5f11-d@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (_, _, sourceLoc) = await AcceptCloseAndLocateAsync(engine, record);

        engine.TestHookAfterIndexSnapshotBeforeSegmentRead = (_, location) =>
        {
            Assert.Equal(sourceLoc, location);
            Assert.True(engine.TryEvict(record.ArtId));
            CorruptRecordPayload(dir.Options.SegmentDir, sourceLoc);
        };

        Assert.False(engine.TryRead(record.ArtId, out _));
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Evicted, meta.State);
        Assert.Equal(sourceLoc, meta.Location);
        Assert.False(engine.TryRead(record.ArtId, out _));
        Assert.True(engine.Index.TryGet(record.ArtId, out meta));
        Assert.Equal(ArticleStorageState.Evicted, meta.State);
    }

    [Fact]
    public async Task E_RelocationDestinationCorruption_InvalidatesCurrentLocationOnly()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<p5f11-e@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, generation, sourceLoc) = await AcceptCloseAndLocateAsync(engine, record);
        var compactionId = engine.Journal.AllocateCompactionId();
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await engine.Journal.AppendCompactionBeginAsync(
                new JournalCompactionBeginRecord(1, compactionId, sourceId, generation),
                CancellationToken.None));
        var relocated = await engine.RelocateArticleAsync(
            compactionId, 1, sourceId, generation, record.ArtId, CancellationToken.None);
        Assert.Equal(ArticleRelocationOutcome.Relocated, relocated.Outcome);
        var destination = relocated.DestinationLocation!.Value;
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        CorruptRecordPayload(dir.Options.SegmentDir, destination);

        Assert.False(engine.TryRead(record.ArtId, out _));
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Invalid, meta.State);
        Assert.Equal(destination, meta.Location);
        Assert.NotEqual(sourceLoc, meta.Location);
        Assert.False(engine.TryRead(record.ArtId, out _));
        Assert.True(engine.Index.TryGet(record.ArtId, out meta));
        Assert.Equal(ArticleStorageState.Invalid, meta.State);
        Assert.Equal(destination, meta.Location);
    }

    [Fact]
    public async Task F_InvalidationCompareAndSetLosesToRelocation()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<p5f12-f@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, generation, sourceLoc) = await AcceptCloseAndLocateAsync(engine, record);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var before));
        engine.TestFailNextIndexedProvenReads = 1;
        engine.TestHookBeforeExpectedInvalidation = (_, location) =>
        {
            Assert.Equal(sourceLoc, location);
            var relocated = RelocateSync(engine, sourceId, generation, record.ArtId);
            Assert.NotEqual(sourceLoc, relocated);
        };

        Assert.False(engine.TryRead(record.ArtId, out _));
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.NotEqual(sourceLoc, meta.Location);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(meta.Location, read.Metadata.Location);

        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var source));
        Assert.Equal(before.LiveBytes - sourceLoc.Length, source.LiveBytes);
        Assert.Equal(before.DeadBytes + sourceLoc.Length, source.DeadBytes);
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var destination));
        Assert.Equal(0, destination.DeadBytes);
        Assert.True(destination.LiveBytes >= meta.Location.Length);
    }

    [Fact]
    public async Task G_CurrentLocationCorruption_InvalidatesAndMovesLiveToDead()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<p5f12-g@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, _, sourceLoc) = await AcceptCloseAndLocateAsync(engine, record);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var before));
        CorruptRecordPayload(dir.Options.SegmentDir, sourceLoc);

        Assert.False(engine.TryRead(record.ArtId, out _));
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Invalid, meta.State);
        Assert.Equal(sourceLoc, meta.Location);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var after));
        Assert.Equal(before.LiveBytes - sourceLoc.Length, after.LiveBytes);
        Assert.Equal(before.DeadBytes + sourceLoc.Length, after.DeadBytes);
    }

    [Fact]
    public async Task H_RetryFailureLosesSecondRelocationCompareAndSet()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<p5f12-h@seg.test>");
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var (sourceId, generation, sourceLoc) = await AcceptCloseAndLocateAsync(engine, record);
        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var sourceBefore));
        StoredArticleLocation firstDestination = default;
        engine.TestFailNextIndexedProvenReads = 2;

        engine.TestHookAfterIndexSnapshotBeforeSegmentRead = (_, location) =>
        {
            Assert.Equal(sourceLoc, location);
            firstDestination = RelocateSync(engine, sourceId, generation, record.ArtId);
            engine.Segments.CloseActiveAsync(CancellationToken.None).GetAwaiter().GetResult();
        };

        engine.TestHookBeforeExpectedInvalidation = (_, location) =>
        {
            Assert.Equal(firstDestination, location);
            Assert.True(engine.Segments.TryGetSegmentInfo(firstDestination.SegmentId, out var closed));
            Assert.Equal(SegmentState.Closed, closed.State);
            var relocated = RelocateSync(engine, firstDestination.SegmentId, closed.Generation, record.ArtId);
            Assert.NotEqual(firstDestination, relocated);
        };

        Assert.False(engine.TryRead(record.ArtId, out _));
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.NotEqual(sourceLoc, meta.Location);
        Assert.NotEqual(firstDestination, meta.Location);

        Assert.True(engine.Segments.TryGetSegmentInfo(sourceId, out var source));
        Assert.Equal(sourceBefore.LiveBytes - sourceLoc.Length, source.LiveBytes);
        Assert.Equal(sourceBefore.DeadBytes + sourceLoc.Length, source.DeadBytes);
        Assert.True(engine.Segments.TryGetSegmentInfo(firstDestination.SegmentId, out var middle));
        Assert.Equal(0, middle.LiveBytes);
        Assert.Equal(firstDestination.Length, middle.DeadBytes);
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var finalSegment));
        Assert.Equal(0, finalSegment.DeadBytes);
        Assert.True(finalSegment.LiveBytes >= meta.Location.Length);

        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(meta.Location, read.Metadata.Location);
        Assert.True(engine.Index.TryGet(record.ArtId, out meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
    }

    private static StoredArticleLocation RelocateSync(
        FileArticleStorageEngine engine,
        SegmentId sourceId,
        ulong generation,
        ArticleId artId)
    {
        var compactionId = engine.Journal.AllocateCompactionId();
        Assert.Equal(
            JournalAppendOutcome.Applied,
            engine.Journal.AppendCompactionBeginAsync(
                    new JournalCompactionBeginRecord(1, compactionId, sourceId, generation),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult());
        var relocated = engine.RelocateArticleAsync(
                compactionId,
                1,
                sourceId,
                generation,
                artId,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        Assert.Equal(ArticleRelocationOutcome.Relocated, relocated.Outcome);
        return relocated.DestinationLocation!.Value;
    }

    private static void CorruptRecordPayload(string segmentDir, StoredArticleLocation location)
    {
        var path = Directory.EnumerateFiles(segmentDir, "seg-*")
            .Single(p => p.Contains(location.SegmentId.Value.ToString("D20"), StringComparison.Ordinal));
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        fs.Position = location.Offset + SegmentRecordCodec.FixedHeaderLength;
        var b = new byte[1];
        _ = fs.Read(b, 0, 1);
        b[0] ^= 0xFF;
        fs.Position = location.Offset + SegmentRecordCodec.FixedHeaderLength;
        fs.Write(b, 0, 1);
        fs.Flush(true);
    }

    private static async Task<(SegmentId SourceId, ulong Generation, StoredArticleLocation Location)>
        AcceptCloseAndLocateAsync(FileArticleStorageEngine engine, ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(meta.Location.SegmentId, out var info));
        Assert.Equal(SegmentState.Closed, info.State);
        return (meta.Location.SegmentId, info.Generation, meta.Location);
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
        _ = builder.Append("Subject: stale-read\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-stale-read-" + Guid.NewGuid().ToString("N"));
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
}
