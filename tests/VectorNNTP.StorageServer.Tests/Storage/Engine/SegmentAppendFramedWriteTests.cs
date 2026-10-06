using System.IO.Hashing;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

public sealed class SegmentAppendFramedWriteTests
{
    [Fact]
    public void Incremental_header_crc_and_hash_match_contiguous_encode()
    {
        var article = CreateArtData("<frame-codec@example.test>", "payload\r\n");
        var (artId, artHash) = Identity(article);
        var encoded = SegmentRecordCodec.Encode(artId, artHash, article);
        Span<byte> header = stackalloc byte[SegmentRecordCodec.FixedHeaderLength];
        Span<byte> crc = stackalloc byte[4];
        SegmentRecordCodec.PrepareProductionFrame(artId, artHash, article, header, crc, out var framedHash);

        Assert.Equal(SegmentRecordCodec.FixedHeaderLength, header.Length);
        Assert.True(encoded.AsSpan(0, header.Length).SequenceEqual(header));
        Assert.True(encoded.AsSpan(header.Length, article.Length).SequenceEqual(article));
        Assert.True(encoded.AsSpan(encoded.Length - 4).SequenceEqual(crc));
        Assert.Equal(XxHash3.HashToUInt64(encoded), framedHash);
    }

    [Fact]
    public async Task Production_append_matches_encode_without_calling_encode()
    {
        var articles = new[]
        {
            CreateArtData("<frame-small@example.test>", "body\r\n"),
            CreateExactSizeArtData("<frame-mid@example.test>", 4096),
            CreateExactSizeArtData("<frame-wide@example.test>", 80 * 1024),
        };
        var snapshots = articles.Select(static article => article.ToArray()).ToArray();

        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        SegmentRecordCodec.ResetEncodeCalls();
        var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
        var locations = new StoredArticleLocation[articles.Length];
        for (var i = 0; i < articles.Length; i++)
        {
            locations[i] = await appender.AppendAsync(articles[i], CancellationToken.None);
        }

        Assert.Equal(0, SegmentRecordCodec.EncodeCalls);
        for (var i = 0; i < articles.Length; i++)
        {
            Assert.True(articles[i].AsSpan().SequenceEqual(snapshots[i]));
            Assert.True(store.TryRead(locations[i], out var read));
            Assert.True(read.Span.SequenceEqual(articles[i]));
        }

        AssertFileMatchesEncode(ActivePath(dir), articles);
        Assert.Equal(1UL, locations[0].SegmentId.Value);
        Assert.Equal(locations[0].SegmentId, locations[1].SegmentId);
        Assert.Equal(locations[1].SegmentId, locations[2].SegmentId);
        Assert.Equal(0, locations[0].Offset);
        Assert.Equal(locations[0].Length, locations[1].Offset);
        Assert.Equal(locations[1].Offset + locations[1].Length, locations[2].Offset);
    }

    [Fact]
    public async Task Empty_payload_is_rejected_without_encoding()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
        SegmentRecordCodec.ResetEncodeCalls();

        var error = await Record.ExceptionAsync(
            () => appender.AppendAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None).AsTask());

        Assert.IsType<ArgumentOutOfRangeException>(error);
        Assert.Equal(0, SegmentRecordCodec.EncodeCalls);
        Assert.Equal(0, appender.SizeBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Torn_tail_is_truncated_and_the_next_append_resumes(int kind)
    {
        using var dir = TempSegmentDir.Create();
        var first = CreateArtData("<torn-keep@example.test>", "keep\r\n");
        var second = CreateArtData("<torn-cut@example.test>", "torn-body-is-long-enough\r\n");
        StoredArticleLocation firstLoc;
        SegmentRecordCodec.ResetEncodeCalls();
        using (var storeA = FileSegmentStore.Open(dir.Options))
        {
            var appender = await storeA.GetActiveAppenderAsync(CancellationToken.None);
            firstLoc = await appender.AppendAsync(first, CancellationToken.None);
            _ = await appender.AppendAsync(second, CancellationToken.None);
        }

        Assert.Equal(0, SegmentRecordCodec.EncodeCalls);
        var path = ActivePath(dir);
        var bytes = File.ReadAllBytes(path);
        var tornLength = kind switch
        {
            0 => firstLoc.Length + 10,
            1 => firstLoc.Length + SegmentRecordCodec.FixedHeaderLength + 8,
            2 => bytes.Length - 2,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        Assert.InRange(tornLength, firstLoc.Length + 1, bytes.Length - 1);
        File.WriteAllBytes(path, bytes.AsSpan(0, tornLength).ToArray());

        using var storeB = FileSegmentStore.Open(dir.Options);
        Assert.True(storeB.TryRead(firstLoc, out var kept));
        Assert.True(kept.Span.SequenceEqual(first));
        Assert.True(storeB.TryGetSegmentInfo(firstLoc.SegmentId, out var info));
        Assert.Equal(firstLoc.Length, info.SizeBytes);

        var third = CreateArtData("<torn-after@example.test>", "after\r\n");
        var thirdLoc = await (await storeB.GetActiveAppenderAsync(CancellationToken.None))
            .AppendAsync(third, CancellationToken.None);
        Assert.Equal(0, SegmentRecordCodec.EncodeCalls);
        Assert.Equal(firstLoc.Length, thirdLoc.Offset);
        Assert.True(storeB.TryRead(thirdLoc, out var after));
        Assert.True(after.Span.SequenceEqual(third));
        AssertFileMatchesEncode(path, first, third);
    }

    [Fact]
    public async Task Complete_bad_crc_fails_closed()
    {
        using var dir = TempSegmentDir.Create();
        using (var storeA = FileSegmentStore.Open(dir.Options))
        {
            var appender = await storeA.GetActiveAppenderAsync(CancellationToken.None);
            _ = await appender.AppendAsync(CreateArtData("<bad-crc-a@example.test>", "keep\r\n"), CancellationToken.None);
            _ = await appender.AppendAsync(CreateArtData("<bad-crc-b@example.test>", "bad\r\n"), CancellationToken.None);
        }

        var path = ActivePath(dir);
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        var error = Assert.Throws<SegmentStoreCorruptException>(() => FileSegmentStore.Open(dir.Options));
        Assert.Contains("CorruptChecksum", error.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Ambiguous_flush_retries_the_same_record_then_appends_the_next()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var article = CreateArtData("<ambiguous@example.test>", "pending\r\n");
        var recordLength = SegmentRecordCodec.RecordLengthForArtSize(article.Length);
        var afterWrites = 0;
        var flushAttempts = 0;
        store.TestAfterWriteBeforeFlush = (_, offset, length) =>
        {
            afterWrites++;
            Assert.Equal(0L, offset);
            Assert.Equal(recordLength, length);
            throw new IOException("after write");
        };
        store.TestBeforeDurableFlush = () =>
        {
            flushAttempts++;
            if (flushAttempts == 1)
            {
                throw new IOException("ambiguous flush");
            }
        };

        var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
        SegmentRecordCodec.ResetEncodeCalls();
        var ambiguous = await Record.ExceptionAsync(
            () => appender.AppendAsync(article, CancellationToken.None).AsTask());
        Assert.IsType<UnreconciledDurableTailException>(ambiguous);
        Assert.Equal(1, afterWrites);
        Assert.Equal(0, store.DurableFlushCount);

        var recovered = await appender.AppendAsync(article, CancellationToken.None);
        Assert.Equal(1, afterWrites);
        Assert.Equal(2, flushAttempts);
        Assert.Equal(1, store.DurableFlushCount);
        Assert.Equal(0, recovered.Offset);
        Assert.Equal(recordLength, recovered.Length);
        Assert.Equal(0, SegmentRecordCodec.EncodeCalls);
        Assert.True(store.TryRead(recovered, out var read));
        Assert.True(read.Span.SequenceEqual(article));

        store.TestAfterWriteBeforeFlush = null;
        store.TestBeforeDurableFlush = null;
        var next = CreateArtData("<after-ambiguous@example.test>", "next\r\n");
        var nextLoc = await appender.AppendAsync(next, CancellationToken.None);
        Assert.Equal(0, SegmentRecordCodec.EncodeCalls);
        Assert.Equal(recovered.Length, nextLoc.Offset);
        Assert.True(store.TryRead(nextLoc, out var nextRead));
        Assert.True(nextRead.Span.SequenceEqual(next));
        AssertFileMatchesEncode(ActivePath(dir), article, next);
    }

    [Fact]
    public async Task Batch_appends_every_record_then_flushes_once()
    {
        var articles = new[]
        {
            CreateArtData("<batch-a@example.test>", "one\r\n"),
            CreateExactSizeArtData("<batch-b@example.test>", 70 * 1024),
            CreateArtData("<batch-c@example.test>", "three\r\n"),
        };
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var durableFlushes = 0;
        store.TestBeforeDurableFlush = () => durableFlushes++;
        SegmentRecordCodec.ResetEncodeCalls();

        var locations = store.AppendActiveBatch(articles.Select(static article => (ReadOnlyMemory<byte>)article).ToArray());

        Assert.Equal(0, SegmentRecordCodec.EncodeCalls);
        Assert.Equal(1, durableFlushes);
        Assert.Equal(1, store.DurableFlushCount);
        Assert.Equal(3, locations.Length);
        Assert.Equal(0, store.PayloadLocationProofCount);
        Assert.Equal(3, store.FlushedHeaderConfirmCount);
        Assert.Equal(0, locations[0].Location.Offset);
        Assert.Equal(locations[0].Location.Length, locations[1].Location.Offset);
        Assert.Equal(locations[1].Location.Offset + locations[1].Location.Length, locations[2].Location.Offset);
        for (var i = 0; i < articles.Length; i++)
        {
            Assert.True(store.TryRead(locations[i].Location, out var read));
            Assert.True(read.Span.SequenceEqual(articles[i]));
        }

        AssertFileMatchesEncode(ActivePath(dir), articles);
    }

    private static void AssertFileMatchesEncode(string path, params byte[][] articles)
    {
        using var expected = new MemoryStream();
        foreach (var article in articles)
        {
            var (artId, artHash) = Identity(article);
            var encoded = SegmentRecordCodec.Encode(artId, artHash, article);
            expected.Write(encoded);
        }

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var actual = new byte[file.Length];
        file.ReadExactly(actual);
        Assert.Equal(expected.ToArray(), actual);
    }

    private static (ArticleId ArtId, ulong ArtHash) Identity(byte[] article)
    {
        Assert.True(ArticleStorageIntegrity.TryExtractMessageIdValue(article, out var messageId));
        return (ArticleId.FromMessageId(messageId), XxHash3.HashToUInt64(article));
    }

    private static string ActivePath(TempSegmentDir dir) =>
        Directory.EnumerateFiles(dir.SegmentDir, "seg-*.active").Single();

    private static byte[] CreateArtData(string messageId, string body)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: segment\r\n");
        _ = builder.Append("\r\n").Append(body);
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record.ArtData.ToArray();
    }

    private static byte[] CreateExactSizeArtData(string messageId, int size)
    {
        var prefix = Encoding.ASCII.GetBytes(
            "Path: peer.example\r\nDate: Fri, 23 Aug 2024 07:30:10 +0000\r\nMessage-ID: "
            + messageId
            + "\r\nNewsgroups: alt.test\r\nFrom: user@example.test\r\nSubject: max\r\n\r\n");
        if (prefix.Length > size)
        {
            throw new InvalidOperationException("Prefix larger than requested size.");
        }

        var data = new byte[size];
        prefix.CopyTo(data.AsSpan());
        data.AsSpan(prefix.Length).Fill((byte)'x');
        return data;
    }

    private sealed class TempSegmentDir : IDisposable
    {
        private TempSegmentDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public string SegmentDir => Options.SegmentDir;

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempSegmentDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-framed-" + Guid.NewGuid().ToString("N"));
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

        private string Root { get; }
    }
}
