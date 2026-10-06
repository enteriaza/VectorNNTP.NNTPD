using System.IO.Hashing;
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
/// Present after a flushed append uses the write receipt and a header read.
/// Locations this process did not just flush still use the full record proof.
/// </summary>
public sealed class WritePathPresentProofTests
{
    [Fact]
    public async Task FlushedAppend_PublishesPresentWithoutPayloadReread()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<write-path@seg.test>", "present-without-reread\r\n");
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        Assert.True(engine.TryRead(record.ArtId, out var before));
        Assert.True(before.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(0, engine.SegmentArticleReadCount);

        engine.SuspendBackgroundPersist = false;
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Equal(0, engine.Segments.PayloadLocationProofCount);
        Assert.Equal(2, engine.Segments.FlushedHeaderConfirmCount);
        Assert.True(engine.Index.TryGet(record.ArtId, out var published));
        Assert.Equal(ArticleStorageState.Present, published.State);
        Assert.True(engine.TryRead(record.ArtId, out var after));
        Assert.True(after.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(published.Location, after.Metadata.Location);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.True(engine.Segments.TryProveStoredLocation(
            published.Location,
            record.ArtId,
            record.ArtHash,
            record.ArtSize));
    }

    [Fact]
    public async Task ReplacedLocation_StillRequiresPayloadProof_AndDoesNotPublishPresent()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        engine.TestPersistRetryDelay = TimeSpan.FromDays(1);
        var record = CreateRecord("<replaced-location@seg.test>", "do-not-publish\r\n");
        Assert.Equal(
            ArticleAcceptOutcome.Accepted,
            (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        engine.TestRewritePhysicalLocationAfterAppend = location =>
            location with { Offset = location.Offset + location.Length };

        engine.SuspendBackgroundPersist = false;
        engine.TestEnqueueIncompleteWork();
        await WaitUntilAsync(() => engine.PersistBlockedRetryScheduledCount >= 1);

        Assert.True(engine.Segments.PayloadLocationProofCount >= 1);
        Assert.False(engine.Index.TryGet(record.ArtId, out var row) && row.State == ArticleStorageState.Present);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.NotEmpty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public void HeaderConfirm_RejectsWrongIdentityOffsetAndLength()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var article = CreateArtData("<header-confirm@seg.test>");
        var artId = ArticleId.FromMessageId("<header-confirm@seg.test>"u8);
        var hash = XxHash3.HashToUInt64(article);
        var receipt = store.AppendActiveBatch([article]).Single();

        Assert.True(store.TryConfirmFlushedAppend(receipt.Location, artId, hash, article.Length));
        Assert.False(store.TryConfirmFlushedAppend(
            receipt.Location,
            ArticleId.FromMessageId("<other@seg.test>"u8),
            hash,
            article.Length));
        Assert.False(store.TryConfirmFlushedAppend(receipt.Location, artId, artHash: 1, article.Length));
        Assert.False(store.TryConfirmFlushedAppend(receipt.Location, artId, hash, article.Length + 1));
        Assert.False(store.TryConfirmFlushedAppend(receipt.Location with { Offset = receipt.Location.Offset + 1 }, artId, hash, article.Length));
        Assert.False(store.TryConfirmFlushedAppend(receipt.Location with { Length = receipt.Location.Length - 1 }, artId, hash, article.Length));
        Assert.False(store.TryConfirmFlushedAppend(
            new StoredArticleLocation(new SegmentId(99), 0, receipt.Location.Length),
            artId,
            hash,
            article.Length));
        Assert.Equal(0, store.PayloadLocationProofCount);
    }

    [Fact]
    public void ShortWrite_DoesNotReturnAFlushedReceipt()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var article = CreateArtData("<short-write@seg.test>");
        store.TestAfterWriteBeforeFlush = (stream, offset, _) => stream.SetLength(offset);

        var ex = Assert.Throws<IOException>(() => store.AppendActiveBatch([article]));
        Assert.Contains("expected", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.PayloadLocationProofCount);
        Assert.Equal(0, store.FlushedHeaderConfirmCount);
    }

    [Fact]
    public void FlushFailure_DoesNotReturnAFlushedReceipt()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var article = CreateArtData("<flush-fail@seg.test>");
        store.TestBeforeDurableFlush = () => throw new IOException("flush-failed");

        var ex = Assert.Throws<IOException>(() => store.AppendActiveBatch([article]));
        Assert.Equal("flush-failed", ex.Message);
        Assert.Equal(0, store.FlushedHeaderConfirmCount);
    }

    [Fact]
    public async Task RecoveredPhysicalWritten_StillReadsThePayloadBeforePresent()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<recovered-pw@seg.test>", "prove-on-recovery\r\n");
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
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        restarted.SuspendBackgroundPersist = true;
        Assert.True(restarted.TryRead(record.ArtId, out var before));
        Assert.True(before.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.False(restarted.Index.TryGet(record.ArtId, out _));

        await restarted.RecoverAsync(CancellationToken.None);

        Assert.True(restarted.Segments.PayloadLocationProofCount >= 1);
        Assert.True(restarted.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.Equal(location, meta.Location);
        Assert.True(restarted.TryRead(record.ArtId, out var after));
        Assert.True(after.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task CorruptPresentRecord_IsNotServedAfterRestart()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<corrupt-present@seg.test>", "must-not-serve\r\n");
        StoredArticleLocation location;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            engine.SuspendBackgroundPersist = true;
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            engine.SuspendBackgroundPersist = false;
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(engine.Index.TryGet(record.ArtId, out var published));
            location = published.Location;
            Assert.Equal(0, engine.Segments.PayloadLocationProofCount);
        }

        var path = Directory.EnumerateFiles(dir.SegmentDir, "seg-*").Single();
        var bytes = File.ReadAllBytes(path);
        bytes[location.Offset + location.Length - 1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        var ex = Assert.Throws<SegmentStoreCorruptException>(() => FileArticleStorageEngine.Open(dir.Options));
        Assert.Contains("CorruptChecksum", ex.Message, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    private static ArticleRecord CreateRecord(string messageId, string body)
    {
        var created = ArticleRecordFactory.TryCreate(
            new NntpArticleParser("cache01.usenet.ninja"),
            Encoding.ASCII.GetBytes(ArticleText(messageId, body)));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private static byte[] CreateArtData(string messageId)
    {
        var created = ArticleRecordFactory.TryCreate(
            new NntpArticleParser("cache01.usenet.ninja"),
            Encoding.ASCII.GetBytes(ArticleText(messageId, "header-body\r\n")));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record.ArtData.ToArray();
    }

    private static string ArticleText(string messageId, string body)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: write-path\r\n");
        _ = builder.Append("\r\n").Append(body);
        return builder.ToString();
    }

    private sealed class TempStorageDir : IDisposable
    {
        private TempStorageDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
            SegmentDir = options.SegmentDir;
        }

        public string Root { get; }

        public string SegmentDir { get; }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempStorageDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-write-path-" + Guid.NewGuid().ToString("N"));
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
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class TempSegmentDir : IDisposable
    {
        private TempSegmentDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public string Root { get; }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempSegmentDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-write-path-seg-" + Guid.NewGuid().ToString("N"));
            var segment = Path.Combine(root, "cache");
            Directory.CreateDirectory(segment);
            return new TempSegmentDir(
                root,
                new ArticleStorageRuntimeOptions(
                    ControlDir: Path.Combine(root, "control"),
                    SegmentDir: segment,
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
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
