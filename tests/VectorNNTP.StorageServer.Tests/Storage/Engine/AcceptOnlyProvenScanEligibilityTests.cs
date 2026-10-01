using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// A new in-process Accept that has not attempted a physical append does not scan historical
/// segments. Replay, a possible physical write, an ambiguous tail, and Evicted or Invalid
/// re-accept still do.
/// </summary>
public sealed class AcceptOnlyProvenScanEligibilityTests
{
    [Fact]
    public async Task New_accept_does_not_scan_historical_segments_and_still_persists()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = await OpenWithHistoryAsync(dir);
        var scans = AttachScanCounter(engine);

        var record = CreateRecord("<scan-new@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, scans());
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(2, engine.PhysicalAppendCount);
    }

    [Fact]
    public async Task Suspended_accept_drained_later_does_not_scan()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = await OpenWithHistoryAsync(dir);
        engine.SuspendBackgroundPersist = true;
        var scans = AttachScanCounter(engine);

        var record = CreateRecord("<scan-drain@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        Assert.Null(Assert.Single(engine.Journal.EnumerateIncomplete()).PhysicalWritten);

        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, scans());
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Retry_before_any_physical_write_does_not_scan()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = await OpenWithHistoryAsync(dir);
        engine.TestPersistRetryDelay = TimeSpan.Zero;
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforeSataAppend;
        var scans = AttachScanCounter(engine);

        var record = CreateRecord("<scan-before-write@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, scans());
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(2, engine.PhysicalAppendCount);
        Assert.True(engine.PersistRetryScheduledCount >= 1);
    }

    [Fact]
    public async Task Startup_accept_only_recovery_scans_for_an_orphan()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<scan-startup@seg.test>");
        await using (var engineA = FileArticleStorageEngine.Open(dir.Options))
        {
            engineA.SuspendBackgroundPersist = true;
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await engineA.AcceptAsync(record, CancellationToken.None)).Outcome);
        }

        Plant(dir, SegmentRecordCodec.Encode(record.ArtId, record.ArtHash, record.ArtData.Span));
        await using var engineB = FileArticleStorageEngine.Open(dir.Options);
        var scans = AttachScanCounter(engineB);
        await engineB.RecoverAsync(CancellationToken.None);

        Assert.True(scans() >= 1);
        Assert.Equal(0, engineB.PhysicalAppendCount);
        Assert.True(engineB.TryRead(record.ArtId, out var read));
        Assert.Equal(0, read.Metadata.Location.Offset);
        Assert.Empty(engineB.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Retry_after_append_that_may_have_landed_scans_and_adopts()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.TestPersistRetryDelay = TimeSpan.Zero;
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.AfterSataAppend;
        var scans = AttachScanCounter(engine);

        var record = CreateRecord("<scan-after-append@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(scans() >= 1);
        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Ambiguous_flush_retains_discovery_on_retry()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = await OpenWithHistoryAsync(dir);
        engine.TestPersistRetryDelay = TimeSpan.FromHours(1);
        var scans = AttachScanCounter(engine);
        var scheduled = engine.PersistRetryScheduledCount;
        engine.Segments.TestAfterWriteBeforeFlush = (_, _, _) => throw new IOException("first-flush");
        engine.Segments.TestBeforeDurableFlush = () => throw new IOException("second-flush");

        var record = CreateRecord("<scan-ambiguous@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await WaitUntilAsync(() => engine.PersistRetryScheduledCount > scheduled, TimeSpan.FromSeconds(5));

        Assert.Equal(0, scans());
        engine.Segments.TestAfterWriteBeforeFlush = null;
        engine.Segments.TestBeforeDurableFlush = null;
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(scans() >= 1);
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.Empty(engine.Journal.EnumerateIncomplete());
        Assert.Equal(2, engine.PhysicalAppendCount);
    }

    [Fact]
    public async Task Failed_physical_written_scans_on_retry_and_does_not_append_again()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.TestPersistRetryDelay = TimeSpan.Zero;
        engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.BeforePhysicalWritten;
        var scans = AttachScanCounter(engine);

        var record = CreateRecord("<scan-pw@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(scans() >= 1);
        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Evicted_reaccept_scans_and_adopts_an_alternate_copy()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.TestPersistRetryDelay = TimeSpan.Zero;
        var record = CreateRecord("<scan-evicted@seg.test>");
        await PersistAsync(engine, record);
        Assert.True(engine.TryEvict(record.ArtId));
        Assert.True(engine.Index.TryGet(record.ArtId, out var dead));
        Assert.Equal(ArticleStorageState.Evicted, dead.State);

        var appender = await engine.Segments.GetActiveAppenderAsync(CancellationToken.None);
        var alternate = await appender.AppendAsync(record.ArtData, CancellationToken.None);
        Assert.NotEqual(dead.Location.Offset, alternate.Offset);
        var scans = AttachScanCounter(engine);
        var appends = engine.PhysicalAppendCount;

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(scans() >= 1);
        Assert.Equal(appends, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.Equal(alternate.SegmentId.Value, read.Metadata.Location.SegmentId.Value);
        Assert.Equal(alternate.Offset, read.Metadata.Location.Offset);
        Assert.Equal(ArticleStorageState.Present, read.Metadata.State);
    }

    [Fact]
    public async Task Invalid_reaccept_scans_and_adopts_an_alternate_copy()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.TestPersistRetryDelay = TimeSpan.Zero;
        var record = CreateRecord("<scan-invalid@seg.test>");
        await PersistAsync(engine, record);
        Assert.True(engine.TryInvalidate(record.ArtId));
        Assert.True(engine.Index.TryGet(record.ArtId, out var dead));
        Assert.Equal(ArticleStorageState.Invalid, dead.State);

        var appender = await engine.Segments.GetActiveAppenderAsync(CancellationToken.None);
        var alternate = await appender.AppendAsync(record.ArtData, CancellationToken.None);
        Assert.NotEqual(dead.Location.Offset, alternate.Offset);
        var scans = AttachScanCounter(engine);
        var appends = engine.PhysicalAppendCount;

        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(scans() >= 1);
        Assert.Equal(appends, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.Equal(alternate.SegmentId.Value, read.Metadata.Location.SegmentId.Value);
        Assert.Equal(alternate.Offset, read.Metadata.Location.Offset);
        Assert.Equal(ArticleStorageState.Present, read.Metadata.State);
    }

    [Fact]
    public async Task Pending_tail_prevents_skip_for_a_never_written_accept()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = await OpenWithHistoryAsync(dir);
        engine.TestPersistRetryDelay = TimeSpan.FromHours(1);
        var scheduled = engine.PersistRetryScheduledCount;
        engine.Segments.TestAfterWriteBeforeFlush = (_, _, _) => throw new IOException("first-flush");
        engine.Segments.TestBeforeDurableFlush = () => throw new IOException("second-flush");

        var pendingOwner = CreateRecord("<scan-pending-owner@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(pendingOwner, CancellationToken.None)).Outcome);
        await WaitUntilAsync(() => engine.PersistRetryScheduledCount > scheduled, TimeSpan.FromSeconds(5));
        Assert.True(engine.Segments.HasPendingOrUnreconciledTail());

        var scanned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Segments.TestHookDuringProvenLocationScan = () => scanned.TrySetResult();
        var scheduledBeforeNext = engine.PersistRetryScheduledCount;
        var record = CreateRecord("<scan-pending-next@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);

        await scanned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => engine.PersistRetryScheduledCount > scheduledBeforeNext,
            TimeSpan.FromSeconds(5));
        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.Contains(engine.Journal.EnumerateIncomplete(), item => item.Accept.ArtId.Equals(record.ArtId));
    }

    [Fact]
    public async Task Duplicate_and_conflict_do_not_scan()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = await OpenWithHistoryAsync(dir);
        var history = CreateRecord("<scan-history@seg.test>");
        var scans = AttachScanCounter(engine);

        Assert.Equal(ArticleAcceptOutcome.Duplicate, (await engine.AcceptAsync(history, CancellationToken.None)).Outcome);
        var conflict = CreateRecord("<scan-history@seg.test>", "different-body\r\n");
        Assert.Equal(ArticleAcceptOutcome.Conflict, (await engine.AcceptAsync(conflict, CancellationToken.None)).Outcome);

        Assert.Equal(0, scans());
        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    private static async Task PersistAsync(FileArticleStorageEngine engine, ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(engine.TryRead(record.ArtId, out _));
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    private static async Task<FileArticleStorageEngine> OpenWithHistoryAsync(TempStorageDir dir)
    {
        var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.TestPersistRetryDelay = TimeSpan.Zero;
        var history = CreateRecord("<scan-history@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(history, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(engine.TryRead(history.ArtId, out _));
        Assert.Equal(1, engine.PhysicalAppendCount);
        return engine;
    }

    private static Func<int> AttachScanCounter(FileArticleStorageEngine engine)
    {
        var scans = 0;
        engine.Segments.TestHookDuringProvenLocationScan = () => Interlocked.Increment(ref scans);
        return () => Volatile.Read(ref scans);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }

    private static void Plant(TempStorageDir dir, byte[] bytes)
    {
        var path = Path.Combine(
            dir.Options.SegmentDir,
            SegmentFileNames.Format(new SegmentId(1), SegmentFileKind.Active));
        File.WriteAllBytes(path, bytes);
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
        _ = builder.Append("Subject: scan-eligibility\r\n");
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

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempStorageDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-scan-elig-" + Guid.NewGuid().ToString("N"));
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

        private string Root { get; }
    }
}
