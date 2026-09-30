using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>Phase 5F.1c: UnauthorizedAccess retry + PhysicalWritten Rejected/Conflict handling.</summary>
public sealed class IncompleteAcceptPersistRetryClassificationTests
{
    [Fact]
    public async Task UnauthorizedAccess_is_retried_and_releases_reservation_after_PhysicalWritten()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        engine.TestPersistRetryDelay = TimeSpan.FromHours(1);
        engine.TestPersistFaultExceptionKind =
            FileArticleStorageEngine.PersistFaultExceptionKind.UnauthorizedAccess;
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;

        var record = CreateRecord("<5f1c-ua@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);

        await WaitUntilAsync(
            () => engine.PersistRetryScheduledCount >= 1,
            TimeSpan.FromSeconds(5));
        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);
        Assert.Single(engine.Journal.EnumerateIncomplete());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await engine.DrainPendingAsync(cts.Token);

        Assert.Equal(1, engine.PersistRetryScheduledCount);
        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.True(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task UnauthorizedAccess_no_hot_loop_with_long_backoff()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.TestPersistRetryDelay = TimeSpan.FromHours(1);
        engine.TestPersistFaultExceptionKind =
            FileArticleStorageEngine.PersistFaultExceptionKind.UnauthorizedAccess;
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;

        _ = await engine.AcceptAsync(CreateRecord("<5f1c-ua-backoff@seg.test>"), CancellationToken.None);
        await WaitUntilAsync(
            () => engine.PersistRetryScheduledCount >= 1,
            TimeSpan.FromSeconds(5));
        Assert.Equal(1, engine.PersistRetryScheduledCount);
        await Task.Delay(50);
        Assert.Equal(1, engine.PersistRetryScheduledCount);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await engine.DrainPendingAsync(cts.Token);
        Assert.Equal(1, engine.PersistRetryScheduledCount);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task InvalidOperation_is_not_retried_and_reservation_remains_held()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<5f1c-inv@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);

        engine.TestPersistFaultExceptionKind =
            FileArticleStorageEngine.PersistFaultExceptionKind.InvalidOperation;
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;

        var fault = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.RecoverAsync(CancellationToken.None));
        Assert.Contains("Injected persist fault", fault.Message, StringComparison.Ordinal);
        Assert.Equal(0, engine.PersistRetryScheduledCount);
        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);
        Assert.Null(Assert.Single(engine.Journal.EnumerateIncomplete()).PhysicalWritten);
        Assert.False(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task PhysicalWritten_Rejected_does_not_release_reservation_or_commit()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<5f1c-rej@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var accept = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);

        // Force PW Rejected: location.Length < ArtSize.
        engine.TestRewritePhysicalLocationAfterAppend = loc =>
            new StoredArticleLocation(loc.SegmentId, loc.Offset, Math.Max(1, record.ArtSize - 1));

        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.RecoverAsync(CancellationToken.None));
        Assert.Contains("PhysicalWritten rejected", rejected.Message, StringComparison.Ordinal);

        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);
        var incomplete = Assert.Single(engine.Journal.EnumerateIncomplete());
        Assert.Equal(accept.Sequence, incomplete.Accept.Sequence);
        Assert.Null(incomplete.PhysicalWritten);
        Assert.False(engine.TryRead(record.ArtId, out _));
        Assert.False(engine.Index.TryGet(record.ArtId, out _));
    }

    [Fact]
    public async Task Existing_identical_PhysicalWritten_is_idempotent_and_releases_reservation()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<5f1c-idemp@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var accept = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);

        engine.TestHookAfterSataBeforePhysicalWritten = (sequence, location) =>
        {
            Assert.Equal(accept.Sequence, sequence);
            Assert.Equal(
                JournalAppendOutcome.Applied,
                engine.Journal.AppendPhysicalWrittenAsync(
                        new JournalPhysicalWrittenRecord(1, sequence, location),
                        CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult());
        };

        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.True(engine.TryRead(record.ArtId, out _));
        // One Accept-only SATA append; hook wrote PW for that same location (IdempotentNoOp).
        Assert.Equal(1, engine.PhysicalAppendCount);
    }

    [Fact]
    public async Task Proven_prior_extent_is_adopted_without_a_second_append()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<5f1c-conflict@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);
        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);

        // Proven copy already on disk. Recovery adopts it instead of appending again.
        var priorAppender = await engine.Segments.GetActiveAppenderAsync(CancellationToken.None);
        var priorLocation = await priorAppender.AppendAsync(record.ArtData, CancellationToken.None);

        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);
        Assert.Equal(1, engine.ProcessLocalSegmentCopyCount);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.Equal(priorLocation.SegmentId.Value, read.Metadata.Location.SegmentId.Value);
        Assert.Equal(priorLocation.Offset, read.Metadata.Location.Offset);
        Assert.Equal(priorLocation.Length, read.Metadata.Location.Length);
        Assert.Equal(0, engine.PhysicalAppendCount);
    }

    [Fact]
    public async Task Restart_after_UnauthorizedAccess_incomplete_recovers_without_reservation()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<5f1c-restart@seg.test>");
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            engineA.SuspendBackgroundPersist = true;
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
            engineA.TestPersistFaultExceptionKind =
                FileArticleStorageEngine.PersistFaultExceptionKind.UnauthorizedAccess;
            engineA.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;
            _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => engineA.RecoverAsync(CancellationToken.None));
            Assert.Single(engineA.Journal.EnumerateIncomplete());
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        engineB.SuspendBackgroundPersist = true;
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.Empty(engineB.Journal.EnumerateIncomplete());
        Assert.Equal(0, engineB.ProcessLocalArticleReservedBytes);
        Assert.True(engineB.TryRead(record.ArtId, out _));
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
            CapacityAdmissionEnabled = true,
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
        _ = builder.Append("Subject: 5f1c\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-5f1c-" + Guid.NewGuid().ToString("N"));
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
