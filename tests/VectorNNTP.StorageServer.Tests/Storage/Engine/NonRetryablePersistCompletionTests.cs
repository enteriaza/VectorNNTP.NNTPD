using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// A non-retryable persist failure keeps the durable Accept, releases the capacity pin,
/// and retries on a path that is not the IO retry counter.
/// </summary>
public sealed class NonRetryablePersistCompletionTests
{
    [Fact]
    public async Task NonRetryable_releases_reservation_and_does_not_publish()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        engine.TestPersistRetryDelay = TimeSpan.FromHours(1);
        engine.TestPersistFaultExceptionKind =
            FileArticleStorageEngine.PersistFaultExceptionKind.InvalidOperation;
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;

        var record = CreateRecord("<blocked-park@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);

        await WaitUntilAsync(() => engine.PersistBlockedRetryScheduledCount >= 1, TimeSpan.FromSeconds(5));

        Assert.Equal(0, engine.PersistRetryScheduledCount);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Single(engine.Journal.EnumerateIncomplete());
        Assert.Null(Assert.Single(engine.Journal.EnumerateIncomplete()).PhysicalWritten);
        Assert.False(engine.TryRead(record.ArtId, out _));
        Assert.False(engine.Index.TryGet(record.ArtId, out _));

        var other = CreateRecord("<blocked-other@seg.test>");
        var otherAccepted = await engine.AcceptAsync(other, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, otherAccepted.Outcome);
        await WaitUntilAsync(
            () => engine.TryRead(other.ArtId, out _),
            TimeSpan.FromSeconds(5));
        Assert.False(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task NonRetryable_retries_on_blocked_path_and_then_publishes()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        engine.TestPersistRetryDelay = TimeSpan.Zero;
        engine.TestPersistFaultExceptionKind =
            FileArticleStorageEngine.PersistFaultExceptionKind.InvalidOperation;
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;

        var record = CreateRecord("<blocked-retry@seg.test>");
        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);

        await WaitUntilAsync(() => engine.TryRead(record.ArtId, out _), TimeSpan.FromSeconds(5));

        Assert.True(engine.PersistBlockedRetryScheduledCount >= 1);
        Assert.Equal(0, engine.PersistRetryScheduledCount);
        Assert.Equal(
            SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize),
            engine.ProcessLocalArticleReservedBytes);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
        Assert.Equal(ArticleStorageState.Present, meta.State);
        Assert.Equal(record.ArtHash, meta.ArtHash);
    }

    [Fact]
    public async Task NonRetryable_does_not_append_when_retry_cannot_reserve()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        engine.TestPersistRetryDelay = TimeSpan.FromSeconds(1);
        engine.TestPersistFaultExceptionKind =
            FileArticleStorageEngine.PersistFaultExceptionKind.InvalidOperation;
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;

        var record = CreateRecord("<blocked-full@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        await WaitUntilAsync(() => engine.PersistBlockedRetryScheduledCount >= 1, TimeSpan.FromSeconds(5));
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);

        capacity.UsedBytes = capacity.TotalBytes;
        await WaitUntilAsync(() => engine.PersistBlockedRetryScheduledCount >= 2, TimeSpan.FromSeconds(5));

        Assert.Equal(0, engine.PersistRetryScheduledCount);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(0, engine.PhysicalAppendCount);
        Assert.False(engine.TryRead(record.ArtId, out _));
        Assert.Single(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Restart_after_non_retryable_completes_without_false_present()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<blocked-restart@seg.test>");
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            engineA.TestPersistRetryDelay = TimeSpan.FromHours(1);
            engineA.TestPersistFaultExceptionKind =
                FileArticleStorageEngine.PersistFaultExceptionKind.InvalidOperation;
            engineA.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;
            var accepted = await engineA.AcceptAsync(record, CancellationToken.None);
            Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
            await WaitUntilAsync(() => engineA.PersistBlockedRetryScheduledCount >= 1, TimeSpan.FromSeconds(5));
            Assert.False(engineA.TryRead(record.ArtId, out _));
            Assert.Single(engineA.Journal.EnumerateIncomplete());
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        engineB.SuspendBackgroundPersist = true;
        Assert.False(engineB.TryRead(record.ArtId, out _));
        await engineB.RecoverAsync(CancellationToken.None);

        Assert.Empty(engineB.Journal.EnumerateIncomplete());
        Assert.Equal(
            SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize),
            engineB.ProcessLocalArticleReservedBytes);
        Assert.True(engineB.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(ArticleStorageState.Present, read.Metadata.State);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > timeout)
            {
                throw new TimeoutException("Condition not met within timeout.");
            }

            await Task.Delay(10);
        }
    }

    private static ArticleStorageRuntimeOptions WithCapacity(ArticleStorageRuntimeOptions options) =>
        options with
        {
            CapacityMaximumUtilization = ArticleCapacityOptions.DefaultMaximumUtilization,
        };

    private static ArticleRecord CreateRecord(string messageId, string body = "line1\r\nline2\r\n")
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: blocked\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class MutableCapacityReader : IStorageCapacityReader
    {
        public MutableCapacityReader(long total, long used)
        {
            TotalBytes = total;
            UsedBytes = used;
        }

        public long TotalBytes { get; set; }

        public long UsedBytes { get; set; }

        public StorageCapacitySnapshot Read() =>
            new(TotalBytes, UsedBytes, Math.Max(0L, TotalBytes - UsedBytes));
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-blocked-" + Guid.NewGuid().ToString("N"));
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
    }
}
