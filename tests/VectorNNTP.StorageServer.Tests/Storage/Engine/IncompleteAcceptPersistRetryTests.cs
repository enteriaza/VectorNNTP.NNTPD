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

/// <summary>Phase 5F.1a durable incomplete Accept persist retry / pending rebuild.</summary>
public sealed class IncompleteAcceptPersistRetryTests
{
    [Fact]
    public async Task Persist_failure_rethrows_requeues_and_retry_succeeds()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.TestPersistRetryDelay = TimeSpan.Zero;
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;

        var record = CreateRecord("<5f1a-retry@seg.test>");
        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);

        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.True(engine.PersistRetryScheduledCount >= 1);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.Equal(1, engine.PhysicalAppendCount);
    }

    [Fact]
    public async Task No_hot_loop_with_long_retry_delay()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.TestPersistRetryDelay = TimeSpan.FromHours(1);
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;

        _ = await engine.AcceptAsync(CreateRecord("<5f1a-backoff@seg.test>"), CancellationToken.None);

        await WaitUntilAsync(
            () => engine.PersistRetryScheduledCount >= 1,
            TimeSpan.FromSeconds(5));

        Assert.Equal(1, engine.PersistRetryScheduledCount);
        Assert.Single(engine.Journal.EnumerateIncomplete());

        // Wall-clock pause must not schedule another retry while the backoff timer is outstanding.
        await Task.Delay(100);
        Assert.Equal(1, engine.PersistRetryScheduledCount);

        // Drain rebuilds pending from the journal without waiting for the long backoff.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await engine.DrainPendingAsync(cts.Token);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(1, engine.PersistRetryScheduledCount);
    }

    [Fact]
    public async Task Reservation_held_across_retry_released_once_after_PhysicalWritten()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        engine.TestPersistRetryDelay = TimeSpan.FromHours(1);
        var record = CreateRecord("<5f1a-res@seg.test>");
        var required = SegmentRecordCodec.RecordLengthForArtSize(record.ArtSize);

        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);

        await WaitUntilAsync(
            () => engine.PersistRetryScheduledCount >= 1,
            TimeSpan.FromSeconds(5));
        Assert.Equal(required, engine.ProcessLocalArticleReservedBytes);
        Assert.Single(engine.Journal.EnumerateIncomplete());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await engine.DrainPendingAsync(cts.Token);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Drain_enqueues_stranded_incomplete_when_never_queued()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        var record = CreateRecord("<5f1a-drain@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Single(engine.Journal.EnumerateIncomplete());

        engine.SuspendBackgroundPersist = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await engine.DrainPendingAsync(cts.Token);

        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.True(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task Restart_RecoverAsync_preserves_Accept_only_semantics()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<5f1a-restart@seg.test>");
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            engineA.SuspendBackgroundPersist = true;
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
            Assert.Null(Assert.Single(engineA.Journal.EnumerateIncomplete()).PhysicalWritten);
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        engineB.SuspendBackgroundPersist = true;
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.Empty(engineB.Journal.EnumerateIncomplete());
        Assert.Equal(1, engineB.PhysicalAppendCount);
        Assert.True(engineB.TryRead(record.ArtId, out _));
        Assert.Equal(0, engineB.ProcessLocalArticleReservedBytes);
    }

    [Fact]
    public async Task Accept_plus_PhysicalWritten_does_not_append_again()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<5f1a-pw@seg.test>");
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
        Assert.Empty(engineB.Journal.EnumerateIncomplete());
        Assert.True(engineB.TryRead(record.ArtId, out var read));
        Assert.Equal(location.SegmentId.Value, read.Metadata.Location.SegmentId.Value);
        Assert.Equal(location.Offset, read.Metadata.Location.Offset);
    }

    [Fact]
    public async Task Index_fault_after_PhysicalWritten_leaves_reservation_zero_then_completes()
    {
        using var dir = TempStorageDir.Create();
        var capacity = new MutableCapacityReader(total: 10_000_000, used: 0);
        await using var engine = FileArticleStorageEngine.Open(
            WithCapacity(dir.Options),
            capacityReader: capacity);
        engine.TestPersistRetryDelay = TimeSpan.Zero;
        var record = CreateRecord("<5f1a-after-pw@seg.test>");

        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.AfterPhysicalWritten;
        _ = await engine.AcceptAsync(record, CancellationToken.None);

        await WaitUntilAsync(
            () => engine.PersistRetryScheduledCount >= 1
                  || engine.Journal.EnumerateIncomplete().Count == 0,
            TimeSpan.FromSeconds(5));

        // After durable PW, reservation must already be released even if index work failed once.
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);

        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.Equal(0, engine.ProcessLocalArticleReservedBytes);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.True(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task Option1_orphan_still_fresh_appends_on_Accept_only_recovery()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<5f1a-orphan@seg.test>");
        long sizeAfterOrphan;
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            engineA.SuspendBackgroundPersist = true;
            _ = await engineA.AcceptAsync(record, CancellationToken.None);
            var appender = await engineA.Segments.GetActiveAppenderAsync(CancellationToken.None);
            _ = await appender.AppendAsync(record.ArtData, CancellationToken.None);
            sizeAfterOrphan = engineA.Segments.GetActiveSizeBytes();
        }

        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        engineB.SuspendBackgroundPersist = true;
        await engineB.RecoverAsync(CancellationToken.None);
        Assert.True(engineB.Segments.GetActiveSizeBytes() > sizeAfterOrphan);
        Assert.Equal(1, engineB.PhysicalAppendCount);
    }

    [Fact]
    public async Task Cancellation_during_drain_is_respected()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        _ = await engine.AcceptAsync(CreateRecord("<5f1a-cancel@seg.test>"), CancellationToken.None);
        // Leave Suspend true so Drain takes RecoverAsync path — cancel before it runs by
        // using an already-canceled token with background path after clearing Suspend.
        engine.SuspendBackgroundPersist = false;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.DrainPendingAsync(cts.Token));
    }

    [Fact]
    public async Task Concurrent_Drain_and_worker_do_not_double_append()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.TestPersistRetryDelay = TimeSpan.Zero;
        var record = CreateRecord("<5f1a-single@seg.test>");
        _ = await engine.AcceptAsync(record, CancellationToken.None);

        var drain1 = engine.DrainPendingAsync(CancellationToken.None);
        var drain2 = engine.DrainPendingAsync(CancellationToken.None);
        await Task.WhenAll(drain1, drain2);

        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(1, engine.PhysicalAppendCount);
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

    private static ArticleStorageRuntimeOptions WithCapacity(
        ArticleStorageRuntimeOptions options,
        double maxUtil = 0.80) =>
        options with
        {
            CapacityAdmissionEnabled = true,
            CapacityMaximumUtilization = maxUtil,
            CapacityCompactionHeadroom = 0.10,
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
        _ = builder.Append("Subject: 5f1a\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-5f1a-" + Guid.NewGuid().ToString("N"));
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
