using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// A closed-segment accounting proof must not hold the process-wide segment write gate,
/// and a proof that is no longer valid at commit time must not be published.
/// </summary>
public sealed class ClosedSegmentAccountingConcurrencyTests
{
    [Fact]
    public async Task Scan_does_not_block_a_read_of_another_segment()
    {
        using var dir = TempStorageDir.Create();
        var first = CreateRecord("<acct-conc-read-a@seg.test>");
        var second = CreateRecord("<acct-conc-read-b@seg.test>");
        _ = await SeedClosedAsync(dir, first);
        var other = await SeedClosedAsync(dir, second);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);

        await RunWhileScanHoldsNoWriteGateAsync(engine, () =>
        {
            Assert.True(engine.TryRead(second.ArtId, out var read));
            Assert.Equal(other, read.Metadata.Location);
            Assert.True(read.ArtData.Span.SequenceEqual(second.ArtData.Span));
            return Task.CompletedTask;
        });

        Assert.True(engine.IsUnreferencedExtentAccountingComplete);
    }

    [Fact]
    public async Task Scan_does_not_block_an_active_append()
    {
        using var dir = TempStorageDir.Create();
        var closed = CreateRecord("<acct-conc-append-closed@seg.test>");
        var appended = CreateRecord("<acct-conc-append-active@seg.test>");
        _ = await SeedClosedAsync(dir, closed);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);

        await RunWhileScanHoldsNoWriteGateAsync(engine, async () =>
        {
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await engine.AcceptAsync(appended, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None);
        });

        Assert.True(engine.Index.TryGet(appended.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.True(engine.TryRead(appended.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(appended.ArtData.Span));
    }

    [Fact]
    public async Task Invalidation_during_scan_does_not_publish_stale_live_bytes()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<acct-conc-invalidate@seg.test>");
        var location = await SeedClosedAsync(dir, record);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);

        await RunWhileScanHoldsNoWriteGateAsync(engine, () =>
        {
            Assert.True(engine.TryInvalidate(record.ArtId));
            return Task.CompletedTask;
        });

        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Invalid, meta.State);
        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var info));
        Assert.True(info.ExtentAccountingComplete);
        Assert.Equal(0, info.LiveBytes);
        Assert.Equal(location.Length, info.DeadBytes);
        Assert.Equal(info.SizeBytes, info.LiveBytes + info.DeadBytes);
    }

    [Fact]
    public async Task Retirement_during_scan_does_not_publish_accounting()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<acct-conc-retire@seg.test>");
        var location = await SeedClosedAsync(dir, record);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);
        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var before));

        await RunWhileScanHoldsNoWriteGateAsync(engine, () =>
        {
            Assert.True(engine.Segments.Catalogue.TryRetire(
                location.SegmentId,
                before.Generation,
                DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        });

        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var after));
        Assert.Equal(SegmentState.Retired, after.State);
        Assert.False(after.ExtentAccountingComplete);
    }

    [Fact]
    public async Task Corrupt_scan_leaves_accounting_incomplete()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<acct-conc-corrupt@seg.test>");
        var location = await SeedClosedAsync(dir, record);
        var path = Path.Combine(
            dir.Options.SegmentDir,
            SegmentFileNames.Format(location.SegmentId, SegmentFileKind.Closed));
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            stream.Position = location.Offset + location.Length - 1;
            var value = stream.ReadByte();
            Assert.True(value >= 0);
            stream.Position = location.Offset + location.Length - 1;
            stream.WriteByte((byte)(value ^ 0xFF));
            stream.Flush(flushToDisk: true);
        }

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();

        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var info));
        Assert.False(info.ExtentAccountingComplete);
        Assert.Equal(0, info.DeadBytes);
    }

    [Fact]
    public async Task Repeated_scan_does_not_change_a_completed_balance()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<acct-conc-idem@seg.test>");
        var location = await SeedClosedAsync(dir, record);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);
        engine.CompleteUnreferencedExtentAccounting();
        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var first));
        engine.CompleteUnreferencedExtentAccounting();
        engine.CompleteUnreferencedExtentAccounting();

        Assert.True(engine.Segments.TryGetSegmentInfo(location.SegmentId, out var again));
        Assert.True(again.ExtentAccountingComplete);
        Assert.Equal(first.LiveBytes, again.LiveBytes);
        Assert.Equal(first.DeadBytes, again.DeadBytes);
        Assert.Equal(again.SizeBytes, again.LiveBytes + again.DeadBytes);
    }

    private static async Task RunWhileScanHoldsNoWriteGateAsync(
        FileArticleStorageEngine engine,
        Func<Task> duringScan)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Segments.TestHookDuringClosedExtentScan = () =>
        {
            entered.TrySetResult();
            if (!release.Task.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Closed-segment accounting scan was not released.");
            }
        };

        var accounting = Task.Run(() =>
        {
            try
            {
                engine.CompleteUnreferencedExtentAccounting();
            }
            finally
            {
                engine.Segments.TestHookDuringClosedExtentScan = null;
            }
        });

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await duringScan();
        }
        finally
        {
            release.TrySetResult();
            await accounting.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<StoredArticleLocation> SeedClosedAsync(TempStorageDir dir, ArticleRecord record)
    {
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        return meta.Location;
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
        _ = builder.Append("Subject: accounting-concurrency\r\n");
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

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempStorageDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-acct-conc-" + Guid.NewGuid().ToString("N"));
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

        private string Root { get; }
    }
}
