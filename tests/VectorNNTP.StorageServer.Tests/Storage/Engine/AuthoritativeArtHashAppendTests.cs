using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Accept persistence stores the journal ArtHash. Append does not hash that payload again.
/// A hash that does not match the payload cannot be constructed, and disk proof still hashes.
/// </summary>
public sealed class AuthoritativeArtHashAppendTests : IDisposable
{
    private TempStorageDir? _directory;

    public void Dispose() => _directory?.Dispose();

    [Fact]
    public void Supplied_hash_is_the_hash_written_and_proved_on_disk()
    {
        var record = CreateRecord("<hash-reuse-known@seg.test>");
        Assert.True(AuthoritativeArticleAppend.TryMatch(
            record.ArtData,
            record.ArtId,
            record.ArtHash,
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
            out var article));

        using var dir = TempStorageDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var before = store.DerivedPayloadArtHashCount;
        var receipt = store.AppendActiveBatch([article]).Single();

        Assert.Equal(before, store.DerivedPayloadArtHashCount);
        Assert.Equal(record.ArtHash, receipt.ArtHash);
        Assert.Equal(record.ArtId, receipt.ArtId);
        Assert.Equal(record.ArtSize, receipt.ArtSize);
        Assert.True(store.TryConfirmFlushedAppend(
            receipt.Location,
            record.ArtId,
            record.ArtHash,
            record.ArtSize));
        Assert.True(store.TryReadProven(
            receipt.Location,
            record.ArtId,
            record.ArtHash,
            record.ArtSize,
            out var proved));
        Assert.True(proved.Span.SequenceEqual(record.ArtData.Span));
        Assert.False(store.TryReadProven(
            receipt.Location,
            record.ArtId,
            record.ArtHash ^ 1,
            record.ArtSize,
            out _));
    }

    [Fact]
    public void Mismatched_supplied_hash_is_rejected_and_not_appended()
    {
        var record = CreateRecord("<hash-reuse-mismatch@seg.test>");
        using var dir = TempStorageDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);

        Assert.False(AuthoritativeArticleAppend.TryMatch(
            record.ArtData,
            record.ArtId,
            record.ArtHash ^ 1,
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
            out _));
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => store.AppendActiveBatch([default(AuthoritativeArticleAppend)]));

        Assert.Equal("article", error.ParamName);
        Assert.Equal(0, store.GetActiveSizeBytes());
        Assert.Equal(0, store.DerivedPayloadArtHashCount);
    }

    [Fact]
    public void Unproved_buffer_still_derives_its_own_hash()
    {
        var record = CreateRecord("<hash-reuse-derive@seg.test>");
        using var dir = TempStorageDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);

        var receipt = store.AppendActiveBatch([record.ArtData]).Single();

        Assert.Equal(1, store.DerivedPayloadArtHashCount);
        Assert.Equal(record.ArtHash, receipt.ArtHash);
        Assert.True(store.TryReadProven(
            receipt.Location,
            record.ArtId,
            record.ArtHash,
            record.ArtSize,
            out _));
    }

    [Fact]
    public async Task Normal_accept_persistence_reuses_the_journal_hash()
    {
        await using var engine = Open();
        var record = CreateRecord("<hash-reuse-persist@seg.test>");
        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);

        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.Equal(0, engine.Segments.DerivedPayloadArtHashCount);
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.Equal(record.ArtHash, read.Metadata.ArtHash);
        Assert.Equal(record.ArtSize, read.Metadata.ArtSize);
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    [Fact]
    public async Task Restart_before_persistence_keeps_the_same_hash_without_rehashing_the_append()
    {
        var engine = Open();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PersistCoalesceMaxDelay = TimeSpan.FromHours(1);
        engine.TestPersistCoalesceDelay = (_, ct) =>
        {
            entered.TrySetResult();
            return Task.Delay(Timeout.Infinite, ct);
        };

        var record = CreateRecord("<hash-reuse-replay@seg.test>");
        var accepting = engine.AcceptAsync(record, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var accepted = await accepting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        Assert.Equal(0, engine.Segments.DurableFlushCount);
        Assert.Equal(0, engine.Segments.DerivedPayloadArtHashCount);

        await engine.DisposeAsync();

        await using var restarted = FileArticleStorageEngine.Open(_directory!.Options);
        Assert.Equal(0, restarted.Segments.DerivedPayloadArtHashCount);
        await restarted.RecoverAsync(CancellationToken.None);

        Assert.Equal(0, restarted.Segments.DerivedPayloadArtHashCount);
        Assert.True(restarted.TryRead(record.ArtId, out var read));
        Assert.Equal(record.ArtHash, read.Metadata.ArtHash);
        Assert.True(read.ArtData.Span.SequenceEqual(record.ArtData.Span));
    }

    private FileArticleStorageEngine Open()
    {
        _directory = TempStorageDir.Create();
        return FileArticleStorageEngine.Open(_directory.Options);
    }

    private static ArticleRecord CreateRecord(string messageId)
    {
        var parser = new NntpArticleParser("hash-reuse.usenet.ninja");
        var text = "Path: peer.example\r\n"
            + "Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n"
            + "Message-ID: " + messageId + "\r\n"
            + "Newsgroups: alt.test\r\n"
            + "From: user@example.test\r\n"
            + "Subject: hash-reuse\r\n"
            + "\r\n"
            + "line1\r\nline2\r\n";
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(text));
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

        public string Root { get; }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempStorageDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-hash-reuse-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            var segment = Path.Combine(root, "segment");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(segment);
            return new TempStorageDir(
                root,
                new ArticleStorageRuntimeOptions(
                    control,
                    segment,
                    ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                    ArticleStorageOptions.DefaultJournalHardLimitBytes,
                    ArticleStorageOptions.DefaultSegmentTargetSizeBytes));
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
