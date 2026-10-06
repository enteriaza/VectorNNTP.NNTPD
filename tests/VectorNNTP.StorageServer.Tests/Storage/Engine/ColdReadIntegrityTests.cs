using System.IO.Hashing;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Cache;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// A cold indexed read proves the segment record once. A later cache insert proves only its own copy.
/// </summary>
public sealed class ColdReadIntegrityTests
{
    [Fact]
    public async Task Cold_read_serves_the_article_and_the_next_read_hits_the_cache()
    {
        using var dir = TempDir.Create();
        var cache = new ArticleMemoryCache(4L * 1024 * 1024);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = Article("<phase18-cold@seg.test>");
        await AcceptAsync(engine, record);
        _ = cache.Remove(record.ArtId);

        Assert.Equal(0, engine.SegmentArticleReadCount);
        Assert.True(engine.TryRead(record.ArtId, out var cold));
        Assert.True(cold.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(1, engine.SegmentArticleReadCount);
        Assert.Equal(0, engine.CacheArticleReadCount);

        Assert.True(engine.TryRead(record.ArtId, out var hit));
        Assert.True(hit.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(1, engine.SegmentArticleReadCount);
        Assert.Equal(1, engine.CacheArticleReadCount);
    }

    [Fact]
    public async Task Restart_reads_the_same_proved_record()
    {
        using var dir = TempDir.Create();
        var record = Article("<phase18-restart@seg.test>");
        StoredArticleLocation location;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            await AcceptAsync(engine, record);
            Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
            location = meta.Location;
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.True(restarted.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(location, read.Metadata.Location);
        Assert.Equal(1, restarted.SegmentArticleReadCount);
    }

    [Fact]
    public void Wrong_identity_and_corrupt_record_fail_the_segment_proof()
    {
        var payload = Article("<phase18-proof@seg.test>").ArtData.Span;
        var artId = ArticleId.FromMessageId(MessageId(payload));
        var hash = XxHash3.HashToUInt64(payload);
        var framed = SegmentRecordCodec.Encode(artId, hash, payload);
        Assert.True(SegmentRecordCodec.TryProveExactRecord(framed, artId, hash, payload.Length));
        Assert.False(SegmentRecordCodec.TryProveExactRecord(framed, ArticleId.FromMessageId("<other@seg.test>"u8), hash, payload.Length));
        Assert.False(SegmentRecordCodec.TryProveExactRecord(framed, artId, hash ^ 1, payload.Length));
        Assert.False(SegmentRecordCodec.TryProveExactRecord(framed, artId, hash, payload.Length - 1));

        var badCrc = framed.ToArray();
        badCrc[^1] ^= 0xFF;
        Assert.False(SegmentRecordCodec.TryDecode(badCrc, out _, out _, out _, out _, out _, out var crcError));
        Assert.Equal(SegmentRecordCodec.DecodeError.CorruptChecksum, crcError);

        var badHeader = framed.ToArray();
        badHeader[4] ^= 0xFF;
        var headerCrc = Crc32.HashToUInt32(badHeader.AsSpan(0, badHeader.Length - 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(badHeader.AsSpan(badHeader.Length - 4), headerCrc);
        Assert.False(SegmentRecordCodec.TryDecode(badHeader, out _, out _, out _, out _, out _, out var headerError));
        Assert.Equal(SegmentRecordCodec.DecodeError.Corrupt, headerError);

        var badPayload = framed.ToArray();
        badPayload[SegmentRecordCodec.FixedHeaderLength] ^= 0xFF;
        var payloadCrc = Crc32.HashToUInt32(badPayload.AsSpan(0, badPayload.Length - 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(badPayload.AsSpan(badPayload.Length - 4), payloadCrc);
        Assert.False(SegmentRecordCodec.TryProveExactRecord(badPayload, artId, hash, payload.Length));
    }

    [Fact]
    public async Task Corrupt_closed_record_is_not_served_after_restart()
    {
        using var dir = TempDir.Create();
        var record = Article("<phase18-crc@seg.test>");
        StoredArticleLocation location;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            await AcceptAsync(engine, record);
            Assert.True(engine.Index.TryGet(record.ArtId, out var meta));
            location = meta.Location;
            await engine.Segments.CloseActiveAsync(CancellationToken.None);
        }

        var path = Path.Combine(dir.SegmentDir, SegmentFileNames.Format(location.SegmentId, SegmentFileKind.Closed));
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            stream.Position = location.Offset + location.Length - 1;
            var value = stream.ReadByte();
            stream.Position = location.Offset + location.Length - 1;
            stream.WriteByte((byte)(value ^ 0xFF));
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.False(restarted.TryRead(record.ArtId, out _));
        Assert.True(restarted.Index.TryGet(record.ArtId, out var row));
        Assert.Equal(ArticleStorageState.Invalid, row.State);
    }

    [Fact]
    public async Task Missing_article_is_not_readable()
    {
        using var dir = TempDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        Assert.False(engine.TryRead(Article("<phase18-missing@seg.test>").ArtId, out _));
    }

    [Fact]
    public async Task Relocated_article_is_read_from_the_destination()
    {
        using var dir = TempDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = Article("<phase18-move@seg.test>");
        await AcceptAsync(engine, record);
        Assert.True(engine.Index.TryGet(record.ArtId, out var before));
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var compact = await engine.CompactClosedSegmentAsync(before.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);

        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.NotEqual(before.Location.SegmentId, read.Metadata.Location.SegmentId);
    }

    [Fact]
    public async Task Concurrent_cold_reads_share_one_segment_proof()
    {
        using var dir = TempDir.Create();
        var cache = new ArticleMemoryCache(4L * 1024 * 1024);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, articleCache: cache);
        var record = Article("<phase18-join@seg.test>");
        await AcceptAsync(engine, record);
        _ = cache.Remove(record.ArtId);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.TestHookBeforeProvenSegmentRead = () =>
        {
            entered.TrySetResult();
            Assert.True(release.Task.Wait(TimeSpan.FromSeconds(5)));
        };
        engine.TestWhenPhysicalReadWaitersChanged = _ => joined.TrySetResult();

        ArticleReadResult first = default;
        ArticleReadResult second = default;
        var owner = Task.Run(() => Assert.True(engine.TryRead(record.ArtId, out first)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiter = Task.Run(() => Assert.True(engine.TryRead(record.ArtId, out second)));
        await joined.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        await Task.WhenAll(owner, waiter);

        Assert.Equal(1, engine.ArticleReadCoalescedCount);
        Assert.Equal(1, engine.SegmentArticleReadCount);
        Assert.True(first.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.True(second.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    private static ReadOnlySpan<byte> MessageId(ReadOnlySpan<byte> artData)
    {
        Assert.True(ArticleStorageIntegrity.TryExtractMessageIdValue(artData, out var messageId));
        return messageId;
    }

    private static async Task AcceptAsync(FileArticleStorageEngine engine, ArticleRecord record)
    {
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
    }

    private static ArticleRecord Article(string messageId)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: phase18\r\n\r\nbody\r\n");
        var created = ArticleRecordFactory.TryCreate(
            new NntpArticleParser("cache01.usenet.ninja"),
            Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class TempDir : IDisposable
    {
        private TempDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public string SegmentDir => Options.SegmentDir;

        public ArticleStorageRuntimeOptions Options { get; }

        private string Root { get; }

        public static TempDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase18-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "control"));
            Directory.CreateDirectory(Path.Combine(root, "cache"));
            return new TempDir(
                root,
                new ArticleStorageRuntimeOptions(
                    ControlDir: Path.Combine(root, "control"),
                    SegmentDir: Path.Combine(root, "cache"),
                    JournalSoftLimitBytes: ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                    JournalHardLimitBytes: ArticleStorageOptions.DefaultJournalHardLimitBytes,
                    SegmentTargetSizeBytes: ArticleStorageOptions.DefaultSegmentTargetSizeBytes,
                    MaxSegmentSealDelay: TimeSpan.FromHours(1)));
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
