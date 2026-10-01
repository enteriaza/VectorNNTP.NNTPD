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
/// Startup recovery walks eligible segments once, then adopts or reappends each
/// Accept-only sequence in journal order.
/// </summary>
public sealed class AcceptOnlyRecoveryBatchTests
{
    [Fact]
    public async Task Two_orphans_in_one_segment_are_discovered_by_one_walk()
    {
        using var dir = TempStorageDir.Create();
        var first = CreateRecord("<batch-a@seg.test>");
        var second = CreateRecord("<batch-b@seg.test>");
        await JournalAcceptsAsync(dir, first, second);
        var firstBytes = SegmentRecordCodec.Encode(first.ArtId, first.ArtHash, first.ArtData.Span);
        var secondBytes = SegmentRecordCodec.Encode(second.ArtId, second.ArtHash, second.ArtData.Span);
        Plant(dir, SegmentFileKind.Active, segmentId: 1, [.. firstBytes, .. secondBytes]);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var scans = AttachScanCounter(engine);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(1, scans());
        Assert.Equal(0, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(first.ArtId, out var readFirst));
        Assert.Equal(0, readFirst.Metadata.Location.Offset);
        Assert.True(engine.TryRead(second.ArtId, out var readSecond));
        Assert.Equal(firstBytes.Length, readSecond.Metadata.Location.Offset);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Sequences_without_copies_reappend_after_one_walk_and_do_not_rescan()
    {
        using var dir = TempStorageDir.Create();
        var unrelated = CreateRecord("<batch-unrelated@seg.test>");
        var first = CreateRecord("<batch-missing-a@seg.test>");
        var second = CreateRecord("<batch-missing-b@seg.test>");
        await JournalAcceptsAsync(dir, first, second);
        Plant(
            dir,
            SegmentFileKind.Active,
            segmentId: 1,
            SegmentRecordCodec.Encode(unrelated.ArtId, unrelated.ArtHash, unrelated.ArtData.Span));

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var appendsDuringScan = -1;
        var scans = 0;
        engine.Segments.TestHookDuringProvenLocationScan = () =>
        {
            scans++;
            appendsDuringScan = (int)engine.PhysicalAppendCount;
        };
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(1, scans);
        Assert.Equal(0, appendsDuringScan);
        Assert.Equal(2, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(first.ArtId, out _));
        Assert.True(engine.TryRead(second.ArtId, out _));
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Mixed_orphan_missing_and_physical_written_keep_their_paths()
    {
        using var dir = TempStorageDir.Create();
        var written = CreateRecord("<batch-pw@seg.test>");
        var orphan = CreateRecord("<batch-orphan@seg.test>");
        var missing = CreateRecord("<batch-missing@seg.test>");
        StoredArticleLocation writtenLocation;
        StoredArticleLocation orphanLocation;
        await using (var engineA = OpenSuspended(dir))
        {
            var accepted = await engineA.AcceptAsync(written, CancellationToken.None);
            var appender = await engineA.Segments.GetActiveAppenderAsync(CancellationToken.None);
            writtenLocation = await appender.AppendAsync(written.ArtData, CancellationToken.None);
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await engineA.Journal.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, accepted.Sequence, writtenLocation),
                    CancellationToken.None));
            _ = await engineA.AcceptAsync(orphan, CancellationToken.None);
            appender = await engineA.Segments.GetActiveAppenderAsync(CancellationToken.None);
            orphanLocation = await appender.AppendAsync(orphan.ArtData, CancellationToken.None);
            _ = await engineA.AcceptAsync(missing, CancellationToken.None);
        }

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var scans = AttachScanCounter(engine);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(1, scans());
        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(written.ArtId, out var readWritten));
        Assert.Equal(writtenLocation.Offset, readWritten.Metadata.Location.Offset);
        Assert.True(engine.TryRead(orphan.ArtId, out var readOrphan));
        Assert.Equal(orphanLocation.Offset, readOrphan.Metadata.Location.Offset);
        Assert.True(engine.TryRead(missing.ArtId, out _));
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Multiple_exact_copies_keep_the_earliest_location()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<batch-copies@seg.test>");
        await JournalAcceptsAsync(dir, record);
        var encoded = SegmentRecordCodec.Encode(record.ArtId, record.ArtHash, record.ArtData.Span);
        Plant(dir, SegmentFileKind.Active, segmentId: 1, [.. encoded, .. encoded]);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(0, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.Equal(0, read.Metadata.Location.Offset);
        Assert.Equal(encoded.Length, read.Metadata.Location.Length);
    }

    [Fact]
    public async Task Evicted_location_is_not_adopted_when_an_alternate_copy_exists()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<batch-evicted@seg.test>");
        var alternate = await JournalReacceptAfterDeathAsync(dir, record, evict: true);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var scans = AttachScanCounter(engine);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.True(scans() >= 1);
        Assert.Equal(0, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.Equal(alternate.Offset, read.Metadata.Location.Offset);
        Assert.Equal(ArticleStorageState.Present, read.Metadata.State);
    }

    [Fact]
    public async Task Invalid_location_is_not_adopted_when_an_alternate_copy_exists()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<batch-invalid@seg.test>");
        var alternate = await JournalReacceptAfterDeathAsync(dir, record, evict: false);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var scans = AttachScanCounter(engine);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.True(scans() >= 1);
        Assert.Equal(0, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.Equal(alternate.Offset, read.Metadata.Location.Offset);
        Assert.Equal(ArticleStorageState.Present, read.Metadata.State);
    }

    [Fact]
    public async Task Corrupt_record_stops_that_segment_and_keeps_the_earlier_candidate()
    {
        using var dir = TempStorageDir.Create();
        var kept = CreateRecord("<batch-kept@seg.test>");
        var dropped = CreateRecord("<batch-dropped@seg.test>");
        await JournalAcceptsAsync(dir, kept, dropped);
        var keptBytes = SegmentRecordCodec.Encode(kept.ArtId, kept.ArtHash, kept.ArtData.Span);
        var droppedBytes = SegmentRecordCodec.Encode(dropped.ArtId, dropped.ArtHash, dropped.ArtData.Span);
        droppedBytes[^1] ^= 0xFF;
        Plant(dir, SegmentFileKind.Closed, segmentId: 1, [.. keptBytes, .. droppedBytes]);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(kept.ArtId, out var readKept));
        Assert.Equal(1UL, readKept.Metadata.Location.SegmentId.Value);
        Assert.Equal(0, readKept.Metadata.Location.Offset);
        Assert.True(engine.TryRead(dropped.ArtId, out var readDropped));
        Assert.NotEqual(1UL, readDropped.Metadata.Location.SegmentId.Value);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Scan_io_failure_fails_recovery_and_does_not_publish()
    {
        using var dir = TempStorageDir.Create();
        var first = CreateRecord("<batch-io-a@seg.test>");
        var second = CreateRecord("<batch-io-b@seg.test>");
        await JournalAcceptsAsync(dir, first, second);
        var filler = CreateRecord("<batch-io-filler@seg.test>");
        Plant(
            dir,
            SegmentFileKind.Active,
            segmentId: 1,
            SegmentRecordCodec.Encode(filler.ArtId, filler.ArtHash, filler.ArtData.Span));

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.Segments.TestHookDuringProvenLocationScan = () => throw new IOException("batch-scan");

        var failed = await Assert.ThrowsAsync<IOException>(() => engine.RecoverAsync(CancellationToken.None));
        Assert.Equal("batch-scan", failed.Message);
        Assert.Equal(0, engine.PhysicalAppendCount);
        Assert.False(engine.TryRead(first.ArtId, out _));
        Assert.False(engine.TryRead(second.ArtId, out _));
        Assert.Equal(2, engine.Journal.EnumerateIncomplete().Count);
    }

    [Fact]
    public async Task Sealed_candidate_is_rejected_and_the_sequence_reappends()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<batch-stale@seg.test>");
        await JournalAcceptsAsync(dir, record);
        var encoded = SegmentRecordCodec.Encode(record.ArtId, record.ArtHash, record.ArtData.Span);
        var filler = CreateRecord("<batch-stale-filler@seg.test>");
        var fillerBytes = SegmentRecordCodec.Encode(filler.ArtId, filler.ArtHash, filler.ArtData.Span);
        Plant(dir, SegmentFileKind.Closed, segmentId: 1, encoded);
        Plant(dir, SegmentFileKind.Closed, segmentId: 2, fillerBytes);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var opens = 0;
        engine.Segments.TestHookDuringProvenLocationScan = () =>
        {
            if (Interlocked.Increment(ref opens) == 2)
            {
                engine.TestSealSegmentForPrePhysicalWritten(1);
            }
        };
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(2, opens);
        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.NotEqual(1UL, read.Metadata.Location.SegmentId.Value);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    private static async Task JournalAcceptsAsync(TempStorageDir dir, params ArticleRecord[] records)
    {
        await using var engine = OpenSuspended(dir);
        foreach (var record in records)
        {
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        }
    }

    private static async Task<StoredArticleLocation> JournalReacceptAfterDeathAsync(
        TempStorageDir dir,
        ArticleRecord record,
        bool evict)
    {
        StoredArticleLocation alternate;
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.TestPersistRetryDelay = TimeSpan.Zero;
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(evict ? engine.TryEvict(record.ArtId) : engine.TryInvalidate(record.ArtId));
        var appender = await engine.Segments.GetActiveAppenderAsync(CancellationToken.None);
        alternate = await appender.AppendAsync(record.ArtData, CancellationToken.None);
        engine.SuspendBackgroundPersist = true;
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        return alternate;
    }

    private static FileArticleStorageEngine OpenSuspended(TempStorageDir dir)
    {
        var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        return engine;
    }

    private static Func<int> AttachScanCounter(FileArticleStorageEngine engine)
    {
        var scans = 0;
        engine.Segments.TestHookDuringProvenLocationScan = () => Interlocked.Increment(ref scans);
        return () => Volatile.Read(ref scans);
    }

    private static void Plant(TempStorageDir dir, SegmentFileKind kind, ulong segmentId, byte[] bytes)
    {
        var path = Path.Combine(
            dir.Options.SegmentDir,
            SegmentFileNames.Format(new SegmentId(segmentId), kind));
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
        _ = builder.Append("Subject: batch-recovery\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-batch-" + Guid.NewGuid().ToString("N"));
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
