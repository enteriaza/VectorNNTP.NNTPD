using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Listener;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Tests.Fixtures;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Read-after-ACK: a durable outstanding Accept is serveable before SATA Present.
/// </summary>
public sealed class JournalReadAfterAcceptTests
{
    [Fact]
    public async Task A_AcceptedArticle_IsOpenableBeforePersist()
    {
        using var dirs = TempDirs.Create();
        var service = CreateService(dirs);
        await service.StartAsync(CancellationToken.None);
        try
        {
            service.Engine.SuspendBackgroundPersist = true;
            var record = CreateRecord("<ack-open@seg.test>", "payload-a\r\n");
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await service.Engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            Assert.False(service.Engine.Index.TryGet(record.ArtId, out _));
            Assert.Equal(0, service.Engine.PhysicalAppendCount);

            var opened = new StorageArticleOpenBoundary(service).TryOpen(Guid.NewGuid(), record.ArtId);
            Assert.True(opened.Accepted);
            Assert.True(opened.Record.ArtData.Span.SequenceEqual(record.ArtData.Span));
            Assert.Equal(record.ArtId, opened.Record.ArtId);
            Assert.Equal(record.ArtHash, opened.Record.ArtHash);
            Assert.True(service.Engine.JournalArticleReadCount >= 1);
            Assert.Equal(0, service.Engine.SegmentArticleReadCount);
            Assert.Equal(0, service.Engine.CacheArticleReadCount);

            var presence = new DurableIndexArticlePresence(service);
            Assert.True(await presence.HasArticleAsync(record.ArtId, CancellationToken.None));
            Assert.False(service.Engine.Index.TryGet(record.ArtId, out _));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task B_JournalOnly_WithoutPresentRow_IsReadable()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<journal-only@seg.test>", "only-journal\r\n");
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);

        Assert.False(engine.Index.TryGet(record.ArtId, out _));
        Assert.True(engine.Journal.TryGetOutstanding(record.ArtId, out _));
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(record.ArtHash, read.Metadata.ArtHash);
        Assert.Equal(record.ArtSize, read.Metadata.ArtSize);
        Assert.Equal(1, engine.JournalArticleReadCount);
        Assert.Equal(0, engine.SegmentArticleReadCount);
    }

    [Fact]
    public async Task C_MissingArticle_StaysNotFound()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var missing = CreateRecord("<missing@seg.test>").ArtId;
        Assert.False(engine.TryRead(missing, out var read));
        Assert.True(read.ArtData.IsEmpty);
        Assert.Equal(0, engine.JournalArticleReadCount);
        Assert.Equal(0, engine.SegmentArticleReadCount);
        Assert.Equal(0, engine.CacheArticleReadCount);
        Assert.False(engine.Journal.TryGetOutstanding(missing, out _));
    }

    [Fact]
    public async Task D_TornJournalTail_IsNotServed()
    {
        using var dir = TempStorageDir.Create();
        var kept = CreateRecord("<torn-keep@seg.test>", "keep\r\n");
        var torn = CreateRecord("<torn-drop@seg.test>", "drop\r\n");
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            engine.SuspendBackgroundPersist = true;
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(kept, CancellationToken.None)).Outcome);
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(torn, CancellationToken.None)).Outcome);
        }

        var path = Path.Combine(dir.Options.ControlDir, FileArticleJournal.JournalFileName);
        var bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, bytes.AsSpan(0, bytes.Length - 9).ToArray());

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        restarted.SuspendBackgroundPersist = true;
        Assert.True(restarted.TryRead(kept.ArtId, out var keptRead));
        Assert.True(keptRead.ArtData.Span.SequenceEqual(kept.ArtData.Span));
        Assert.False(restarted.TryRead(torn.ArtId, out var tornRead));
        Assert.True(tornRead.ArtData.IsEmpty);
        Assert.False(restarted.Journal.TryGetOutstanding(torn.ArtId, out _));
        Assert.False(restarted.Index.TryGet(torn.ArtId, out _));
    }

    [Fact]
    public async Task E_CorruptJournalChecksum_FailsClosed()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<crc@seg.test>", "crc-body\r\n");
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            engine.SuspendBackgroundPersist = true;
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        }

        var path = Path.Combine(dir.Options.ControlDir, FileArticleJournal.JournalFileName);
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);

        var ex = Assert.Throws<ArticleJournalCorruptException>(() => FileArticleStorageEngine.Open(dir.Options));
        Assert.Contains("corrupt", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task F_RestartBeforeSata_IsReadable()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<restart-journal@seg.test>", "after-crash\r\n");
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            engine.SuspendBackgroundPersist = true;
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        restarted.SuspendBackgroundPersist = true;
        Assert.Equal(0, restarted.PhysicalAppendCount);
        Assert.False(restarted.Index.TryGet(record.ArtId, out _));
        Assert.True(restarted.Journal.TryGetOutstanding(record.ArtId, out _));
        Assert.True(restarted.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(1, restarted.JournalArticleReadCount);
        Assert.Equal(0, restarted.SegmentArticleReadCount);
    }

    [Fact]
    public async Task G_RestartAfterSataBeforeIndexCommit_RecoversOneArticle()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<sata-before-index@seg.test>", "one-copy\r\n");
        StoredArticleLocation location;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            engine.SuspendBackgroundPersist = true;
            var accept = await engine.AcceptAsync(record, CancellationToken.None);
            var appender = await engine.Segments.GetActiveAppenderAsync(CancellationToken.None);
            location = await appender.AppendAsync(record.ArtData, CancellationToken.None);
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await engine.Journal.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, accept.Sequence, location),
                    CancellationToken.None));
            Assert.False(engine.Index.TryGet(record.ArtId, out _));
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        restarted.SuspendBackgroundPersist = true;
        Assert.True(restarted.TryRead(record.ArtId, out var before));
        Assert.True(before.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.False(restarted.Index.TryGet(record.ArtId, out _));

        await restarted.RecoverAsync(CancellationToken.None);
        await restarted.RecoverAsync(CancellationToken.None);

        Assert.Equal(0, restarted.PhysicalAppendCount);
        Assert.Empty(restarted.Journal.EnumerateIncomplete());
        Assert.True(restarted.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.Equal(location, meta.Location);
        Assert.True(restarted.TryRead(record.ArtId, out var after));
        Assert.True(after.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(location, after.Metadata.Location);
        Assert.True(restarted.SegmentArticleReadCount >= 1);
    }

    [Fact]
    public async Task H_OpenDuringJournalToSata_HasNoGap()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = CreateRecord("<race@seg.test>", "during-copy\r\n");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.TestHookAfterSataBeforePhysicalWritten = (_, _) =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };

        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(engine.Index.TryGet(record.ArtId, out _));
        Assert.True(engine.TryRead(record.ArtId, out var duringCopy));
        Assert.True(duringCopy.ArtData.Span.SequenceEqual(record.ArtData.Span));

        var misses = 0;
        var stop = 0;
        var reader = Task.Run(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                if (!engine.TryRead(record.ArtId, out var read)
                    || !read.ArtData.Span.SequenceEqual(record.ArtData.Span))
                {
                    Interlocked.Increment(ref misses);
                }
            }
        });

        release.TrySetResult();
        await engine.DrainPendingAsync(CancellationToken.None);
        Volatile.Write(ref stop, 1);
        await reader.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, misses);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(record.ArtId, out var published));
        Assert.True(published.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(meta.Location, published.Metadata.Location);
    }

    [Fact]
    public async Task H_CheckpointWhileOutstanding_DoesNotOpenAGap()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<checkpoint-read@seg.test>", "still-here\r\n");
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);

        var misses = 0;
        var stop = 0;
        var reader = Task.Run(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                if (!engine.TryRead(record.ArtId, out var read)
                    || !read.ArtData.Span.SequenceEqual(record.ArtData.Span))
                {
                    Interlocked.Increment(ref misses);
                }
            }
        });

        var checkpoint = Task.Run(() => engine.CheckpointTruncateCommitted());
        await checkpoint.WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Write(ref stop, 1);
        await reader.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, misses);
        Assert.Single(engine.Journal.EnumerateIncomplete());
        Assert.False(engine.Index.TryGet(record.ArtId, out _));
        Assert.True(engine.TryRead(record.ArtId, out var after));
        Assert.True(after.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task I_CacheDoesNotResurrectAnUnserveableArticle()
    {
        using var dir = TempStorageDir.Create();
        var cache = new ArticleMemoryCache(4L * 1024 * 1024);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<cache-coherent@seg.test>", "cached\r\n");
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);

        Assert.True(engine.TryRead(record.ArtId, out var journalRead));
        Assert.True(journalRead.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.False(cache.TryGet(record.ArtId, out _));
        Assert.Equal(1, engine.JournalArticleReadCount);
        Assert.Equal(0, engine.CacheArticleReadCount);

        await engine.RecoverAsync(CancellationToken.None);
        Assert.True(cache.TryGet(record.ArtId, out _));
        Assert.True(engine.TryRead(record.ArtId, out var cached));
        Assert.True(cached.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(1, engine.CacheArticleReadCount);
        Assert.Equal(1, engine.JournalArticleReadCount);

        Assert.True(engine.TryEvict(record.ArtId));
        Assert.False(cache.TryGet(record.ArtId, out _));
        Assert.Equal(ArticleMemoryCachePutOutcome.Inserted, cache.Put(record));
        Assert.True(cache.TryGet(record.ArtId, out _));
        Assert.False(engine.TryRead(record.ArtId, out _));
        Assert.False(cache.TryGet(record.ArtId, out _));
        Assert.Equal(1, engine.JournalArticleReadCount);
        Assert.Equal(1, engine.CacheArticleReadCount);
    }

    private static StorageEngineApplicationService CreateService(TempDirs dirs) =>
        new(
            CreateRuntime(dirs),
            Options.Create(CreateBindable(dirs)),
            NullLogger<StorageEngineApplicationService>.Instance);

    private static StorageServerRuntimeOptions CreateRuntime(TempDirs dirs)
    {
        var options = CreateBindable(dirs);
        return StorageServerRuntimeOptionsFactory.Create(options, StorageServerTestOptions.CreateValidAcme(options));
    }

    private static StorageServerOptions CreateBindable(TempDirs dirs)
    {
        var options = StorageServerTestOptions.CreateValid();
        options.Storage.CacheDir = dirs.CacheDir;
        options.Storage.ControlDir = dirs.ControlDir;
        return options;
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
        _ = builder.Append("Subject: journal-read\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-journal-read-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(cache);
            var options = new ArticleStorageRuntimeOptions(
                ControlDir: control,
                SegmentDir: cache,
                JournalSoftLimitBytes: ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                JournalHardLimitBytes: ArticleStorageOptions.DefaultJournalHardLimitBytes,
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
            catch (IOException)
            {
            }
        }
    }

    private sealed class TempDirs : IDisposable
    {
        private TempDirs(string root, string cacheDir, string controlDir)
        {
            Root = root;
            CacheDir = cacheDir;
            ControlDir = controlDir;
        }

        public string Root { get; }

        public string CacheDir { get; }

        public string ControlDir { get; }

        public static TempDirs Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-journal-open-" + Guid.NewGuid().ToString("N"));
            var cache = Path.Combine(root, "cache");
            var control = Path.Combine(root, "control");
            Directory.CreateDirectory(cache);
            Directory.CreateDirectory(control);
            return new TempDirs(root, cache, control);
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
