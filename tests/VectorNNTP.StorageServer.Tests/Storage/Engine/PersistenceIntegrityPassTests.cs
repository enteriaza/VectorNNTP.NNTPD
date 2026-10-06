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
/// Persistence still rejects bad identity and bad segment bytes after the append path
/// stopped repeating a proof it had just computed.
/// </summary>
public sealed class PersistenceIntegrityPassTests
{
    [Fact]
    public async Task Valid_append_is_readable_and_survives_restart()
    {
        using var dir = TempDir.Create();
        var record = Article("<phase17-valid@seg.test>");
        StoredArticleMetadata metadata;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options))
        {
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(engine.TryRead(record.ArtId, out var read));
            Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
            Assert.True(engine.Index.TryGet(record.ArtId, out metadata));
            Assert.Equal(ArticleStorageState.Present, metadata.State);
        }

        await using var restarted = FileArticleStorageEngine.Open(dir.Options);
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.True(restarted.TryRead(record.ArtId, out var again));
        Assert.True(again.ArtData.Span.SequenceEqual(record.ArtData.Span));
        Assert.Equal(metadata.Location, again.Metadata.Location);
    }

    [Fact]
    public async Task Wrong_article_id_and_hash_are_rejected_at_accept()
    {
        using var dir = TempDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var record = Article("<phase17-id@seg.test>");
        var bytes = record.ArtData.ToArray();
        var fields = ArticleFieldTable.Locate(bytes, NntpArticleHeaderName.Date);
        var wrongId = new ArticleRecord(
            ArticleId.FromMessageId("<other@seg.test>"u8),
            record.ArtHash,
            record.ArtType,
            record.ArtLines,
            record.CanonicalUtc,
            record.ParseStatus,
            bytes,
            fields);
        Assert.Equal(
            ArticleAcceptOutcome.RejectedInvalid,
            (await engine.AcceptAsync(wrongId, CancellationToken.None)).Outcome);

        var wrongHash = new ArticleRecord(
            record.ArtId,
            record.ArtHash ^ 1,
            record.ArtType,
            record.ArtLines,
            record.CanonicalUtc,
            record.ParseStatus,
            bytes,
            fields);
        Assert.Equal(
            ArticleAcceptOutcome.RejectedInvalid,
            (await engine.AcceptAsync(wrongHash, CancellationToken.None)).Outcome);
        Assert.False(engine.Index.TryGet(record.ArtId, out _));
    }

    [Fact]
    public void Wrong_size_hash_and_corrupt_payload_fail_untrusted_proof()
    {
        var payload = Article("<phase17-proof@seg.test>").ArtData.Span;
        var artId = ArticleId.FromMessageId(MessageId(payload));
        var artHash = XxHash3.HashToUInt64(payload);
        Assert.True(ArticleStorageIntegrity.TryProve(payload, artId, artHash, payload.Length));
        Assert.False(ArticleStorageIntegrity.TryProve(payload, ArticleId.FromMessageId("<nope@seg.test>"u8), artHash, payload.Length));
        Assert.False(ArticleStorageIntegrity.TryProve(payload, artId, artHash ^ 1, payload.Length));
        Assert.False(ArticleStorageIntegrity.TryProve(payload, artId, artHash, payload.Length - 1));

        var mutated = payload.ToArray();
        mutated[^1] ^= 0xFF;
        Assert.False(ArticleStorageIntegrity.TryProve(mutated, artId, artHash, mutated.Length));

        var framed = SegmentRecordCodec.Encode(artId, artHash, payload);
        Assert.True(SegmentRecordCodec.TryDecode(framed, out _, out _, out _, out _, out _, out _));
        framed[^1] ^= 0xFF;
        Assert.False(SegmentRecordCodec.TryDecode(framed, out _, out _, out _, out _, out _, out var crcError));
        Assert.Equal(SegmentRecordCodec.DecodeError.CorruptChecksum, crcError);

        var payloadCorrupt = SegmentRecordCodec.Encode(artId, artHash, payload);
        payloadCorrupt[SegmentRecordCodec.FixedHeaderLength] ^= 0xFF;
        var crc = Crc32.HashToUInt32(payloadCorrupt.AsSpan(0, payloadCorrupt.Length - 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payloadCorrupt.AsSpan(payloadCorrupt.Length - 4, 4), crc);
        Assert.False(SegmentRecordCodec.TryDecode(payloadCorrupt, out _, out _, out _, out _, out _, out var proofError));
        Assert.Equal(SegmentRecordCodec.DecodeError.Corrupt, proofError);
        Assert.False(SegmentRecordCodec.TryProveExactRecord(payloadCorrupt, artId, artHash, payload.Length));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void Append_round_trip_and_crc_corruption_fails_closed_on_reopen(int writers)
    {
        using var dir = TempDir.Create(writers);
        var count = Math.Max(writers, 1);
        var articles = new ReadOnlyMemory<byte>[count];
        for (var i = 0; i < count; i++)
        {
            articles[i] = Article($"<phase17-w{writers}-{i}@seg.test>").ArtData;
        }

        FlushedSegmentAppend target;
        using (var store = FileSegmentStore.Open(dir.Options))
        {
            var receipts = store.AppendActiveBatch(articles);
            Assert.Equal(count, receipts.Length);
            for (var i = 0; i < count; i++)
            {
                Assert.True(store.TryReadProven(
                    receipts[i].Location,
                    receipts[i].ArtId,
                    receipts[i].ArtHash,
                    receipts[i].ArtSize,
                    out var read));
                Assert.True(read.Span.SequenceEqual(articles[i].Span));
            }

            target = receipts[0];
        }

        var path = Path.Combine(
            dir.SegmentDir,
            SegmentFileNames.Format(target.Location.SegmentId, SegmentFileKind.Active));
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            stream.Position = target.Location.Offset + target.Location.Length - 1;
            var b = stream.ReadByte();
            stream.Position = target.Location.Offset + target.Location.Length - 1;
            stream.WriteByte((byte)(b ^ 0xFF));
            stream.Flush(flushToDisk: true);
        }

        var error = Assert.Throws<SegmentStoreCorruptException>(() => FileSegmentStore.Open(dir.Options));
        Assert.Contains("CorruptChecksum", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Single_writer_pending_record_matches_only_the_same_article()
    {
        using var dir = TempDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var original = Article("<phase17-pending-one@seg.test>").ArtData;
        var other = Article("<phase17-pending-other@seg.test>").ArtData;
        store.TestBeforeDurableFlush = () => throw new IOException("segment-flush");
        Assert.Throws<UnreconciledDurableTailException>(() =>
            store.AppendToActiveReportingAsync(original, CancellationToken.None));

        store.TestBeforeDurableFlush = null;
        Assert.Throws<UnreconciledDurableTailException>(() =>
            store.AppendToActiveReportingAsync(other, CancellationToken.None));
        Assert.Equal(0, CountCopies(dir.SegmentDir, MessageId(other.Span)));

        var finished = await store.AppendToActiveReportingAsync(original, CancellationToken.None);
        Assert.True(store.TryReadProven(finished.Location, finished.ArtId, finished.ArtHash, finished.ArtSize, out var read));
        Assert.True(read.Span.SequenceEqual(original.Span));
        Assert.Equal(1, CountCopies(dir.SegmentDir, MessageId(original.Span)));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Multi_writer_pending_record_matches_only_the_same_article(int writers)
    {
        using var dir = TempDir.Create(writers);
        using var store = FileSegmentStore.Open(dir.Options);
        var original = Batch(writers, "pending");
        store.TestBeforeDurableFlush = () => throw new IOException("segment-flush");
        var failed = Assert.ThrowsAny<Exception>(() => store.AppendActiveBatch(original));
        Assert.IsAssignableFrom<UnreconciledDurableTailException>(Unwrap(failed));

        store.TestBeforeDurableFlush = null;
        var other = Batch(writers, "other");
        var mismatch = Assert.ThrowsAny<Exception>(() => store.AppendActiveBatch(other));
        Assert.IsAssignableFrom<UnreconciledDurableTailException>(Unwrap(mismatch));
        foreach (var article in other)
        {
            Assert.Equal(0, CountCopies(dir.SegmentDir, MessageId(article.Span)));
        }

        var receipts = store.AppendActiveBatch(original);
        Assert.Equal(writers, receipts.Length);
        foreach (var article in original)
        {
            Assert.Equal(1, CountCopies(dir.SegmentDir, MessageId(article.Span)));
        }
    }

    private static Exception Unwrap(Exception error) =>
        error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions[0] : error;

    private static ReadOnlyMemory<byte>[] Batch(int count, string tag)
    {
        var articles = new ReadOnlyMemory<byte>[count];
        for (var i = 0; i < count; i++)
        {
            articles[i] = Article($"<phase17-{tag}-{i}@seg.test>").ArtData;
        }

        return articles;
    }

    private static int CountCopies(string directory, ReadOnlySpan<byte> needle)
    {
        var found = 0;
        foreach (var file in Directory.GetFiles(directory))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            for (var i = 0; i <= bytes.Length - needle.Length; i++)
            {
                if (bytes.AsSpan(i, needle.Length).SequenceEqual(needle))
                {
                    found++;
                }
            }
        }

        return found;
    }

    private static ReadOnlySpan<byte> MessageId(ReadOnlySpan<byte> artData)
    {
        Assert.True(ArticleStorageIntegrity.TryExtractMessageIdValue(artData, out var messageId));
        return messageId;
    }

    private static ArticleRecord Article(string messageId)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: phase17\r\n\r\nbody\r\n");
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

        public static TempDir Create(int activeSegments = 1)
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase17-" + Guid.NewGuid().ToString("N"));
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
                    MaxSegmentSealDelay: TimeSpan.FromHours(1),
                    ActiveSegmentCount: activeSegments));
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
