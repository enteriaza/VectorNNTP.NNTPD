using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Accept-only recovery adopts one proven physical copy. Other proved copies on a
/// Closed segment become dead exactly once. Unproven records are not published.
/// Active segments are not historically accounted.
/// </summary>
public sealed class AcceptOnlyCrashOrphanTests
{
    [Fact]
    public async Task Exact_match_is_adopted_without_a_second_append()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<orphan-exact@seg.test>");
        await AcceptWithoutPersistAsync(dir, record);
        var planted = Plant(dir, SegmentRecordCodec.Encode(record.ArtId, record.ArtHash, record.ArtData.Span));

        await using var engine = OpenSuspended(dir);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(0, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(0, read.Metadata.Location.Offset);
        Assert.Equal(planted, read.Metadata.Location.Length);
        Assert.True(engine.Segments.TryGetSegmentInfo(read.Metadata.Location.SegmentId, out var info));
        Assert.Equal(planted, info.LiveBytes);
        Assert.Equal(0, info.DeadBytes);
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Missing_record_appends_once()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<orphan-missing@seg.test>");
        await AcceptWithoutPersistAsync(dir, record);

        await using var engine = OpenSuspended(dir);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Empty(engine.Journal.EnumerateIncomplete());
    }

    [Fact]
    public async Task Wrong_article_id_is_not_adopted_and_is_marked_dead()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<orphan-id@seg.test>", "wanted-body\r\n");
        var other = CreateRecord("<orphan-other@seg.test>", "other-body\r\n");
        await AcceptWithoutPersistAsync(dir, record);
        var otherBytes = SegmentRecordCodec.Encode(other.ArtId, other.ArtHash, other.ArtData.Span);
        Plant(dir, otherBytes);

        await using var engine = OpenSuspended(dir);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.False(engine.Index.TryGet(other.ArtId, out _));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.False(engine.IsUnreferencedExtentAccountingComplete);
        Assert.True(engine.Segments.TryGetSegmentInfo(read.Metadata.Location.SegmentId, out var before));
        Assert.Equal(read.Metadata.Location.Length, before.LiveBytes);
        Assert.Equal(0, before.DeadBytes);
        engine.CompleteUnreferencedExtentAccounting();
        Assert.True(engine.IsUnreferencedExtentAccountingComplete);
        Assert.True(engine.Segments.TryGetSegmentInfo(read.Metadata.Location.SegmentId, out var info));
        Assert.Equal(read.Metadata.Location.Length, info.LiveBytes);
        Assert.Equal(otherBytes.Length, info.DeadBytes);
        Assert.True(info.ExtentAccountingComplete);
        Assert.Equal(info.SizeBytes, info.LiveBytes + info.DeadBytes);
    }

    [Fact]
    public async Task Wrong_hash_is_not_adopted()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<orphan-hash@seg.test>");
        await AcceptWithoutPersistAsync(dir, record);
        var encoded = SegmentRecordCodec.Encode(record.ArtId, record.ArtHash, record.ArtData.Span);
        BinaryPrimitives.WriteUInt64LittleEndian(encoded.AsSpan(8 + ArticleId.Length), record.ArtHash + 1);
        RewriteCrc(encoded);
        Plant(dir, encoded);

        await using var engine = OpenSuspended(dir);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.Equal(record.ArtHash, read.Metadata.ArtHash);
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task Wrong_size_is_not_adopted()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<orphan-size@seg.test>");
        await AcceptWithoutPersistAsync(dir, record);
        var encoded = SegmentRecordCodec.Encode(record.ArtId, record.ArtHash, record.ArtData.Span);
        var sizeOffset = SegmentRecordCodec.FixedHeaderLength - 4;
        BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(sizeOffset), record.ArtSize - 1);
        RewriteCrc(encoded);
        Plant(dir, encoded);

        await using var engine = OpenSuspended(dir);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.Equal(record.ArtSize, read.Metadata.ArtSize);
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task Corrupt_record_is_not_adopted()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<orphan-corrupt@seg.test>");
        await AcceptWithoutPersistAsync(dir, record);
        var encoded = SegmentRecordCodec.Encode(record.ArtId, record.ArtHash, record.ArtData.Span);
        encoded[^1] ^= 0xFF;
        Plant(dir, encoded);

        await using var engine = OpenSuspended(dir);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(1, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task Multiple_exact_copies_adopt_the_earliest_and_mark_the_rest_dead()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<orphan-multi@seg.test>");
        await AcceptWithoutPersistAsync(dir, record);
        var one = SegmentRecordCodec.Encode(record.ArtId, record.ArtHash, record.ArtData.Span);
        Plant(dir, [.. one, .. one]);

        await using var engine = OpenSuspended(dir);
        await engine.RecoverAsync(CancellationToken.None);

        Assert.Equal(0, engine.PhysicalAppendCount);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.Equal(0, read.Metadata.Location.Offset);
        Assert.Equal(one.Length, read.Metadata.Location.Length);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.False(engine.IsUnreferencedExtentAccountingComplete);
        Assert.True(engine.Segments.TryGetSegmentInfo(read.Metadata.Location.SegmentId, out var before));
        Assert.Equal(one.Length, before.LiveBytes);
        Assert.Equal(0, before.DeadBytes);
        engine.CompleteUnreferencedExtentAccounting();
        Assert.True(engine.Segments.TryGetSegmentInfo(read.Metadata.Location.SegmentId, out var info));
        Assert.Equal(one.Length, info.LiveBytes);
        Assert.Equal(one.Length, info.DeadBytes);
        Assert.Equal(info.SizeBytes, info.LiveBytes + info.DeadBytes);
        Assert.True(info.ExtentAccountingComplete);
    }

    [Fact]
    public async Task Restart_after_adoption_keeps_the_same_copy_and_dead_duplicate()
    {
        using var dir = TempStorageDir.Create();
        var record = CreateRecord("<orphan-restart@seg.test>");
        await AcceptWithoutPersistAsync(dir, record);
        var one = SegmentRecordCodec.Encode(record.ArtId, record.ArtHash, record.ArtData.Span);
        Plant(dir, [.. one, .. one]);

        await using (var engineA = OpenSuspended(dir))
        {
            await engineA.RecoverAsync(CancellationToken.None);
            Assert.Equal(0, engineA.PhysicalAppendCount);
            Assert.True(engineA.TryRead(record.ArtId, out var first));
            Assert.Equal(0, first.Metadata.Location.Offset);
            await engineA.Segments.CloseActiveAsync(CancellationToken.None);
        }

        await using var engineB = OpenSuspended(dir);
        await engineB.RecoverAsync(CancellationToken.None);

        Assert.Equal(0, engineB.PhysicalAppendCount);
        Assert.True(engineB.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(0, read.Metadata.Location.Offset);
        Assert.Equal(ArticleStorageState.Present, read.Metadata.State);
        Assert.False(engineB.IsUnreferencedExtentAccountingComplete);
        Assert.True(engineB.Segments.TryGetSegmentInfo(read.Metadata.Location.SegmentId, out var before));
        Assert.Equal(one.Length, before.LiveBytes);
        Assert.Equal(0, before.DeadBytes);
        engineB.CompleteUnreferencedExtentAccounting();
        Assert.True(engineB.Segments.TryGetSegmentInfo(read.Metadata.Location.SegmentId, out var info));
        Assert.Equal(one.Length, info.LiveBytes);
        Assert.Equal(one.Length, info.DeadBytes);
        Assert.Empty(engineB.Journal.EnumerateIncomplete());
    }

    private static async Task AcceptWithoutPersistAsync(TempStorageDir dir, ArticleRecord record)
    {
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.Single(engine.Journal.EnumerateIncomplete());
    }

    private static FileArticleStorageEngine OpenSuspended(TempStorageDir dir)
    {
        var engine = FileArticleStorageEngine.Open(dir.Options);
        engine.SuspendBackgroundPersist = true;
        return engine;
    }

    private static int Plant(TempStorageDir dir, byte[] bytes)
    {
        var path = Path.Combine(
            dir.Options.SegmentDir,
            SegmentFileNames.Format(new SegmentId(1), SegmentFileKind.Active));
        File.WriteAllBytes(path, bytes);
        return bytes.Length;
    }

    private static void RewriteCrc(byte[] encoded)
    {
        var crc = Crc32.HashToUInt32(encoded.AsSpan(0, encoded.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(encoded.Length - 4), crc);
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
        _ = builder.Append("Subject: orphan\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class TempStorageDir : IDisposable
    {
        private TempStorageDir(string root, ArticleStorageRuntimeOptions options)
        {
            _ = root;
            Options = options;
        }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempStorageDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-orphan-" + Guid.NewGuid().ToString("N"));
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
                var root = Path.GetDirectoryName(Options.ControlDir);
                if (root is not null && Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
