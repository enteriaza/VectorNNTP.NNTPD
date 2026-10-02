using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Single-pass physical proof: one read, one CRC, one identity check, one integrity pass, no payload copy.
/// </summary>
public sealed class PhysicalLocationProofTests
{
    [Fact]
    public async Task ValidRecord_Succeeds()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var article = CreateArtData("<proof-ok@example.test>");
        var location = await AppendAsync(store, article);
        var artId = ArticleId.FromMessageId("<proof-ok@example.test>"u8);
        var hash = XxHash3.HashToUInt64(article);

        Assert.True(store.TryProveStoredLocation(location, artId, hash, article.Length));
    }

    [Fact]
    public async Task BadCrc_Rejected()
    {
        using var dir = TempSegmentDir.Create();
        var article = CreateArtData("<proof-crc@example.test>");
        var location = await AppendAndCloseAsync(dir, article);
        CorruptFile(dir, location, span => span[^1] ^= 0xFF);

        using var store = FileSegmentStore.Open(dir.Options);
        Assert.False(store.TryProveStoredLocation(
            location,
            ArticleId.FromMessageId("<proof-crc@example.test>"u8),
            XxHash3.HashToUInt64(article),
            article.Length));
    }

    [Fact]
    public async Task WrongArticleId_Rejected()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var article = CreateArtData("<proof-id@example.test>");
        var location = await AppendAsync(store, article);

        Assert.False(store.TryProveStoredLocation(
            location,
            ArticleId.FromMessageId("<proof-other@example.test>"u8),
            XxHash3.HashToUInt64(article),
            article.Length));
    }

    [Fact]
    public async Task WrongArtHash_Rejected()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var article = CreateArtData("<proof-hash@example.test>");
        var location = await AppendAsync(store, article);

        Assert.False(store.TryProveStoredLocation(
            location,
            ArticleId.FromMessageId("<proof-hash@example.test>"u8),
            expectedArtHash: 1,
            article.Length));
    }

    [Fact]
    public async Task WrongArtSize_Rejected()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var article = CreateArtData("<proof-size@example.test>");
        var location = await AppendAsync(store, article);

        Assert.False(store.TryProveStoredLocation(
            location,
            ArticleId.FromMessageId("<proof-size@example.test>"u8),
            XxHash3.HashToUInt64(article),
            article.Length + 1));
    }

    [Fact]
    public async Task MalformedAndOutOfBounds_Rejected()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var article = CreateArtData("<proof-bounds@example.test>");
        var location = await AppendAsync(store, article);
        var artId = ArticleId.FromMessageId("<proof-bounds@example.test>"u8);
        var hash = XxHash3.HashToUInt64(article);

        Assert.False(store.TryProveStoredLocation(location with { Length = location.Length - 1 }, artId, hash, article.Length));
        Assert.False(store.TryProveStoredLocation(location with { Offset = -1 }, artId, hash, article.Length));
        Assert.False(store.TryProveStoredLocation(location with { Offset = location.Length }, artId, hash, article.Length));
        Assert.False(store.TryProveStoredLocation(location with { Length = 3 }, artId, hash, article.Length));
        Assert.False(store.TryProveStoredLocation(
            new StoredArticleLocation(new SegmentId(99), 0, location.Length),
            artId,
            hash,
            article.Length));
        await store.CloseActiveAsync(CancellationToken.None);
        store.Dispose();

        CorruptFile(dir, location, span =>
        {
            span[4] = 9;
            WriteCrc(span);
        });
        using var reopened = FileSegmentStore.Open(dir.Options);
        Assert.False(reopened.TryProveStoredLocation(location, artId, hash, article.Length));
    }

    [Fact]
    public async Task MessageIdDoesNotMatchArticleId_Rejected()
    {
        using var dir = TempSegmentDir.Create();
        var article = CreateArtData("<proof-mid@example.test>");
        var location = await AppendAndCloseAsync(dir, article);
        var originalId = ArticleId.FromMessageId("<proof-mid@example.test>"u8);

        ulong rewrittenHash = 0;
        CorruptFile(dir, location, span =>
        {
            var payload = span.Slice(SegmentRecordCodec.FixedHeaderLength, article.Length);
            var marker = "<proof-mid@example.test>"u8;
            var at = payload.IndexOf(marker);
            Assert.True(at >= 0);
            payload[at + 1] = (byte)'x';
            rewrittenHash = XxHash3.HashToUInt64(payload);
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(8 + ArticleId.Length, 8), rewrittenHash);
            WriteCrc(span);
        });

        using var store = FileSegmentStore.Open(dir.Options);
        Assert.False(store.TryProveStoredLocation(location, originalId, rewrittenHash, article.Length));
    }

    [Fact]
    public async Task MessageIdMissing_Rejected()
    {
        using var dir = TempSegmentDir.Create();
        var article = CreateArtData("<proof-nomid@example.test>");
        var location = await AppendAndCloseAsync(dir, article);
        var artId = ArticleId.FromMessageId("<proof-nomid@example.test>"u8);

        ulong rewrittenHash = 0;
        CorruptFile(dir, location, span =>
        {
            var payload = span.Slice(SegmentRecordCodec.FixedHeaderLength, article.Length);
            var marker = "Message-ID:"u8;
            var at = payload.IndexOf(marker);
            Assert.True(at >= 0);
            payload[at] = (byte)'X';
            rewrittenHash = XxHash3.HashToUInt64(payload);
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(8 + ArticleId.Length, 8), rewrittenHash);
            WriteCrc(span);
        });

        using var store = FileSegmentStore.Open(dir.Options);
        Assert.False(store.TryProveStoredLocation(location, artId, rewrittenHash, article.Length));
    }

    [Fact]
    public async Task RetiredSegment_Rejected()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var article = CreateArtData("<proof-retired@example.test>");
        var location = await AppendAsync(store, article);
        var artId = ArticleId.FromMessageId("<proof-retired@example.test>"u8);
        var hash = XxHash3.HashToUInt64(article);
        Assert.True(store.TryProveStoredLocation(location, artId, hash, article.Length));

        await store.CloseActiveAsync(CancellationToken.None);
        Assert.True(store.TryGetSegmentInfo(location.SegmentId, out var closed));
        Assert.True(store.Catalogue.TryRetire(location.SegmentId, closed.Generation, DateTimeOffset.UtcNow));

        Assert.False(store.TryProveStoredLocation(location, artId, hash, article.Length));
    }

    [Fact]
    public async Task ProofAllocatesTheRecordOnce()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var article = CreateArtData("<proof-alloc@example.test>", string.Join("\r\n", Enumerable.Repeat("yyyy", 400)) + "\r\n");
        var location = await AppendAsync(store, article);
        var artId = ArticleId.FromMessageId("<proof-alloc@example.test>"u8);
        var hash = XxHash3.HashToUInt64(article);

        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(store.TryProveStoredLocation(location, artId, hash, article.Length));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, location.Length, location.Length + article.Length);
    }

    [Fact]
    public void ExactRecord_RejectsDeclaredLengthMismatch()
    {
        var article = CreateArtData("<proof-decl@example.test>");
        var artId = ArticleId.FromMessageId("<proof-decl@example.test>"u8);
        var hash = XxHash3.HashToUInt64(article);
        var record = SegmentRecordCodec.Encode(artId, hash, article);
        Assert.True(SegmentRecordCodec.TryProveExactRecord(record, artId, hash, article.Length));

        var shortRecord = record.AsSpan(0, record.Length - 1).ToArray();
        Assert.False(SegmentRecordCodec.TryProveExactRecord(shortRecord, artId, hash, article.Length));
    }

    private static async Task<StoredArticleLocation> AppendAsync(FileSegmentStore store, byte[] article)
    {
        var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
        return await appender.AppendAsync(article, CancellationToken.None);
    }

    private static async Task<StoredArticleLocation> AppendAndCloseAsync(TempSegmentDir dir, byte[] article)
    {
        using var store = FileSegmentStore.Open(dir.Options);
        var location = await AppendAsync(store, article);
        await store.CloseActiveAsync(CancellationToken.None);
        return location;
    }

    private static void CorruptFile(TempSegmentDir dir, StoredArticleLocation location, Action<Span<byte>> mutate)
    {
        var path = Directory.EnumerateFiles(dir.SegmentDir, "seg-*").Single();
        var bytes = File.ReadAllBytes(path);
        mutate(bytes.AsSpan((int)location.Offset, location.Length));
        File.WriteAllBytes(path, bytes);
    }

    private static void WriteCrc(Span<byte> record)
    {
        var crc = Crc32.HashToUInt32(record[..^4]);
        BinaryPrimitives.WriteUInt32LittleEndian(record[^4..], crc);
    }

    private static byte[] CreateArtData(string messageId, string body = "line1\r\nline2\r\n")
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: proof\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record.ArtData.ToArray();
    }

    private sealed class TempSegmentDir : IDisposable
    {
        private TempSegmentDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public string Root { get; }

        public string SegmentDir => Options.SegmentDir;

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempSegmentDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-proof-" + Guid.NewGuid().ToString("N"));
            var segment = Path.Combine(root, "cache");
            Directory.CreateDirectory(segment);
            var options = new ArticleStorageRuntimeOptions(
                ControlDir: Path.Combine(root, "control"),
                SegmentDir: segment,
                JournalSoftLimitBytes: ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                JournalHardLimitBytes: ArticleStorageOptions.DefaultJournalHardLimitBytes,
                SegmentTargetSizeBytes: ArticleStorageOptions.DefaultSegmentTargetSizeBytes);
            return new TempSegmentDir(root, options);
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
