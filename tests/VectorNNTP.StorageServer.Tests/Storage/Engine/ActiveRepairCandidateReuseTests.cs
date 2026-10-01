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
/// Active tail repair keeps record bounds and identity. Startup candidate discovery
/// re-reads only records that can match, and does not decode the Active file again.
/// </summary>
public sealed class ActiveRepairCandidateReuseTests
{
    [Fact]
    public async Task Active_orphan_recovery_does_not_decode_the_active_file_again()
    {
        using var dir = TempStorageDir.Create();
        var orphan = CreateRecord("<reuse-orphan@seg.test>");
        var decoy = CreateRecord("<reuse-decoy@seg.test>");
        await JournalAcceptsAsync(dir, orphan);
        var orphanBytes = SegmentRecordCodec.Encode(orphan.ArtId, orphan.ArtHash, orphan.ArtData.Span);
        var decoyBytes = SegmentRecordCodec.Encode(decoy.ArtId, decoy.ArtHash, decoy.ArtData.Span);
        Plant(dir, SegmentFileKind.Active, segmentId: 1, [.. orphanBytes, .. decoyBytes]);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var scans = AttachScanCounter(engine);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(1, scans());
        Assert.Equal(0, engine.Segments.ActiveCandidateSequentialWalkCount);
        Assert.Equal(orphanBytes.Length, engine.Segments.ActiveCandidatePayloadProofBytesRead);
        Assert.True(engine.Segments.DiscoveryPayloadBytesRead > engine.Segments.ActiveCandidatePayloadProofBytesRead);
        Assert.Equal(0, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(orphan.ArtId, out var read));
        Assert.Equal(0, read.Metadata.Location.Offset);
        Assert.True(read.ArtData.Span.SequenceEqual(orphan.ArtData.Span));
    }

    [Fact]
    public async Task Torn_active_tail_is_truncated_and_is_not_a_candidate()
    {
        using var dir = TempStorageDir.Create();
        var kept = CreateRecord("<reuse-torn-kept@seg.test>");
        var missing = CreateRecord("<reuse-torn-missing@seg.test>");
        await JournalAcceptsAsync(dir, kept, missing);
        var keptBytes = SegmentRecordCodec.Encode(kept.ArtId, kept.ArtHash, kept.ArtData.Span);
        var torn = new byte[keptBytes.Length + 3];
        keptBytes.CopyTo(torn, 0);
        torn[^3] = 0x11;
        torn[^2] = 0x22;
        torn[^1] = 0x33;
        Plant(dir, SegmentFileKind.Active, segmentId: 1, torn);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var active = Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.active").Single();
        Assert.Equal(torn.Length, engine.Segments.DiscoveryPayloadBytesRead);
        Assert.Equal(keptBytes.Length, new FileInfo(active).Length);
        Assert.True(engine.Segments.TryGetSegmentInfo(new SegmentId(1), out var info));
        Assert.Equal(keptBytes.Length, info.SizeBytes);

        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(0, engine.Segments.ActiveCandidateSequentialWalkCount);
        Assert.Equal(keptBytes.Length, engine.Segments.ActiveCandidatePayloadProofBytesRead);
        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(kept.ArtId, out var readKept));
        Assert.Equal(0, readKept.Metadata.Location.Offset);
        Assert.True(readKept.ArtData.Span.SequenceEqual(kept.ArtData.Span));
        Assert.True(engine.TryRead(missing.ArtId, out var readMissing));
        Assert.NotEqual(0, readMissing.Metadata.Location.Offset);
    }

    [Fact]
    public async Task Corrupt_complete_active_record_still_fails_startup()
    {
        using var dir = TempStorageDir.Create();
        StoredArticleLocation first;
        using (var store = FileSegmentStore.Open(dir.Options))
        {
            var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
            first = await appender.AppendAsync(CreateRecord("<reuse-corrupt-a@seg.test>").ArtData, CancellationToken.None);
            _ = await appender.AppendAsync(CreateRecord("<reuse-corrupt-b@seg.test>").ArtData, CancellationToken.None);
        }

        var active = Directory.EnumerateFiles(dir.Options.SegmentDir, "seg-*.active").Single();
        var lengthBefore = new FileInfo(active).Length;
        CorruptByte(active, first.Offset + first.Length - 1);

        var ex = Assert.Throws<SegmentStoreCorruptException>(() => FileArticleStorageEngine.Open(dir.Options));
        Assert.Contains("corrupt", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(lengthBefore, new FileInfo(active).Length);
    }

    [Fact]
    public async Task Same_article_id_with_different_content_is_not_adopted()
    {
        using var dir = TempStorageDir.Create();
        var journalled = CreateRecord("<reuse-same-id@seg.test>", "alpha-body\r\n");
        var planted = CreateRecord("<reuse-same-id@seg.test>", "bravo-body\r\n");
        Assert.Equal(journalled.ArtId, planted.ArtId);
        Assert.NotEqual(journalled.ArtHash, planted.ArtHash);
        await JournalAcceptsAsync(dir, journalled);
        var plantedBytes = SegmentRecordCodec.Encode(planted.ArtId, planted.ArtHash, planted.ArtData.Span);
        Plant(dir, SegmentFileKind.Active, segmentId: 1, plantedBytes);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(0, engine.Segments.ActiveCandidateSequentialWalkCount);
        Assert.Equal(0, engine.Segments.ActiveCandidatePayloadProofBytesRead);
        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(journalled.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(journalled.ArtData.Span));
        Assert.Equal(plantedBytes.Length, read.Metadata.Location.Offset);
    }

    [Fact]
    public async Task Different_article_id_is_not_adopted()
    {
        using var dir = TempStorageDir.Create();
        var wanted = CreateRecord("<reuse-wanted@seg.test>");
        var other = CreateRecord("<reuse-other@seg.test>");
        Assert.NotEqual(wanted.ArtId, other.ArtId);
        await JournalAcceptsAsync(dir, wanted);
        var otherBytes = SegmentRecordCodec.Encode(other.ArtId, other.ArtHash, other.ArtData.Span);
        Plant(dir, SegmentFileKind.Active, segmentId: 1, otherBytes);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(0, engine.Segments.ActiveCandidateSequentialWalkCount);
        Assert.Equal(0, engine.Segments.ActiveCandidatePayloadProofBytesRead);
        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(wanted.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(wanted.ArtData.Span));
        Assert.Equal(otherBytes.Length, read.Metadata.Location.Offset);
    }

    [Fact]
    public async Task Multiple_active_copies_keep_the_earliest_offset()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<reuse-copies@seg.test>");
        var decoy = CreateRecord("<reuse-copies-decoy@seg.test>");
        await JournalAcceptsAsync(dir, record);
        var encoded = SegmentRecordCodec.Encode(record.ArtId, record.ArtHash, record.ArtData.Span);
        var decoyBytes = SegmentRecordCodec.Encode(decoy.ArtId, decoy.ArtHash, decoy.ArtData.Span);
        Plant(dir, SegmentFileKind.Active, segmentId: 1, [.. encoded, .. decoyBytes, .. encoded]);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(0, engine.Segments.ActiveCandidateSequentialWalkCount);
        Assert.Equal(encoded.Length * 2, engine.Segments.ActiveCandidatePayloadProofBytesRead);
        Assert.Equal(0, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.Equal(0, read.Metadata.Location.Offset);
        Assert.Equal(encoded.Length, read.Metadata.Location.Length);
    }

    [Fact]
    public async Task Closed_orphan_is_still_adopted_without_an_active_walk()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<reuse-closed@seg.test>");
        await JournalAcceptsAsync(dir, record);
        var encoded = SegmentRecordCodec.Encode(record.ArtId, record.ArtHash, record.ArtData.Span);
        Plant(dir, SegmentFileKind.Closed, segmentId: 1, encoded);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var scans = AttachScanCounter(engine);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(1, scans());
        Assert.Equal(0, engine.Segments.ActiveCandidateSequentialWalkCount);
        Assert.Equal(0, engine.Segments.ActiveCandidatePayloadProofBytesRead);
        Assert.Equal(0, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.Equal(1UL, read.Metadata.Location.SegmentId.Value);
        Assert.Equal(0, read.Metadata.Location.Offset);
    }

    [Fact]
    public async Task Mixed_active_and_closed_orphans_keep_their_locations()
    {
        using var dir = TempStorageDir.Create();
        var closedArticle = CreateRecord("<reuse-mix-closed@seg.test>");
        var activeArticle = CreateRecord("<reuse-mix-active@seg.test>");
        var decoy = CreateRecord("<reuse-mix-decoy@seg.test>");
        await JournalAcceptsAsync(dir, closedArticle, activeArticle);
        var closedBytes = SegmentRecordCodec.Encode(closedArticle.ArtId, closedArticle.ArtHash, closedArticle.ArtData.Span);
        var activeBytes = SegmentRecordCodec.Encode(activeArticle.ArtId, activeArticle.ArtHash, activeArticle.ArtData.Span);
        var decoyBytes = SegmentRecordCodec.Encode(decoy.ArtId, decoy.ArtHash, decoy.ArtData.Span);
        Plant(dir, SegmentFileKind.Closed, segmentId: 1, closedBytes);
        Plant(dir, SegmentFileKind.Active, segmentId: 2, [.. activeBytes, .. decoyBytes]);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var scans = AttachScanCounter(engine);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(2, scans());
        Assert.Equal(0, engine.Segments.ActiveCandidateSequentialWalkCount);
        Assert.Equal(activeBytes.Length, engine.Segments.ActiveCandidatePayloadProofBytesRead);
        Assert.Equal(0, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(closedArticle.ArtId, out var readClosed));
        Assert.Equal(1UL, readClosed.Metadata.Location.SegmentId.Value);
        Assert.Equal(0, readClosed.Metadata.Location.Offset);
        Assert.True(engine.TryRead(activeArticle.ArtId, out var readActive));
        Assert.Equal(2UL, readActive.Metadata.Location.SegmentId.Value);
        Assert.Equal(0, readActive.Metadata.Location.Offset);
    }

    [Fact]
    public async Task Append_during_discovery_is_not_a_candidate()
    {
        using var dir = TempStorageDir.Create();
        var planted = CreateRecord("<reuse-planted@seg.test>");
        var appended = CreateRecord("<reuse-appended@seg.test>");
        await JournalAcceptsAsync(dir, planted, appended);
        var plantedBytes = SegmentRecordCodec.Encode(planted.ArtId, planted.ArtHash, planted.ArtData.Span);
        Plant(dir, SegmentFileKind.Active, segmentId: 1, plantedBytes);

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        StoredArticleLocation hookLocation = default;
        engine.Segments.TestHookDuringProvenLocationScan = () =>
        {
            var appender = engine.Segments.GetActiveAppenderAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            hookLocation = appender.AppendAsync(appended.ArtData, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        };
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(0, engine.Segments.ActiveCandidateSequentialWalkCount);
        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(planted.ArtId, out var readPlanted));
        Assert.Equal(0, readPlanted.Metadata.Location.Offset);
        Assert.True(engine.TryRead(appended.ArtId, out var readAppended));
        Assert.NotEqual(hookLocation.Offset, readAppended.Metadata.Location.Offset);
        Assert.True(readAppended.ArtData.Span.SequenceEqual(appended.ArtData.Span));
    }

    [Fact]
    public async Task Physical_written_recovery_does_not_walk_active_bytes()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<reuse-pw@seg.test>");
        StoredArticleLocation location;
        await using (var engineA = OpenSuspended(dir))
        {
            var accepted = await engineA.AcceptAsync(record, CancellationToken.None);
            var appender = await engineA.Segments.GetActiveAppenderAsync(CancellationToken.None);
            location = await appender.AppendAsync(record.ArtData, CancellationToken.None);
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await engineA.Journal.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, accepted.Sequence, location),
                    CancellationToken.None));
        }

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var scans = AttachScanCounter(engine);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(0, scans());
        Assert.Equal(0, engine.Segments.ActiveCandidateSequentialWalkCount);
        Assert.Equal(0, engine.Segments.ActiveCandidatePayloadProofBytesRead);
        Assert.Equal(0, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.Equal(location.Offset, read.Metadata.Location.Offset);
        Assert.Equal(location.SegmentId.Value, read.Metadata.Location.SegmentId.Value);
    }

    [Fact]
    public async Task In_process_accept_does_not_scan_after_active_repair()
    {
        using var dir = TempStorageDir.Create();
        var history = CreateRecord("<reuse-history@seg.test>");
        Plant(
            dir,
            SegmentFileKind.Active,
            segmentId: 1,
            SegmentRecordCodec.Encode(history.ArtId, history.ArtHash, history.ArtData.Span));

        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var scans = AttachScanCounter(engine);
        await engine.RecoverAsync(CancellationToken.None);
        Assert.True(engine.Segments.DiscoveryPayloadBytesRead > 0);

        var record = CreateRecord("<reuse-live@seg.test>");
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, scans());
        Assert.Equal(0, engine.Segments.ActiveCandidateSequentialWalkCount);
        Assert.Equal(0, engine.Segments.ActiveCandidatePayloadProofBytesRead);
        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
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

    private static void CorruptByte(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = offset;
        var value = stream.ReadByte();
        stream.Position = offset;
        stream.WriteByte((byte)(value ^ 0xFF));
        stream.Flush(flushToDisk: true);
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
        _ = builder.Append("Subject: active-reuse\r\n");
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-active-reuse-" + Guid.NewGuid().ToString("N"));
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
