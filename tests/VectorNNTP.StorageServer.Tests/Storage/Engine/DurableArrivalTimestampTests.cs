using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// The original Accept instant stays on the index row through publication, relocation,
/// checkpoint, restart, eviction, and a torn final frame.
/// </summary>
public sealed class DurableArrivalTimestampTests
{
    private static readonly DateTimeOffset Arrival = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Accept_to_present_keeps_the_original_arrival()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time);
        var record = Article("<phase22-present@seg.test>");
        engine.SuspendBackgroundPersist = true;
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        Assert.True(engine.Journal.TryGetOutstanding(record.ArtId, out var accept));
        var accepted = accept.AcceptedUtc;
        time.Advance(TimeSpan.FromHours(5));
        await engine.DrainPendingAsync(CancellationToken.None);

        Assert.True(engine.Index.TryGet(record.ArtId, out var published));
        Assert.Equal(ArticleStorageState.Present, published.State);
        Assert.Equal(accepted, published.AcceptedUtc);
        Assert.True(published.LastAccessUtc > accepted);
        Assert.False(engine.Journal.TryGetOutstanding(record.ArtId, out _));
        Assert.True(engine.TryRead(record.ArtId, out var read));
        Assert.Equal(accepted, read.Metadata.AcceptedUtc);
    }

    [Fact]
    public async Task Restart_after_present_keeps_the_original_arrival()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var record = Article("<phase22-restart@seg.test>");
        DateTimeOffset accepted;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time))
        {
            engine.SuspendBackgroundPersist = true;
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            Assert.True(engine.Journal.TryGetOutstanding(record.ArtId, out var accept));
            accepted = accept.AcceptedUtc;
            await engine.DrainPendingAsync(CancellationToken.None);
        }

        time.Advance(TimeSpan.FromDays(9));
        await using var restarted = FileArticleStorageEngine.Open(dir.Options, timeProvider: time);
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.True(restarted.Index.TryGet(record.ArtId, out var restored));
        Assert.Equal(accepted, restored.AcceptedUtc);
        Assert.Equal(ArticleStorageState.Present, restored.State);
        Assert.NotEqual(time.GetUtcNow(), restored.AcceptedUtc);
        Assert.True(restarted.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task Relocation_preserves_arrival()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time);
        var record = Article("<phase22-move@seg.test>");
        engine.SuspendBackgroundPersist = true;
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        Assert.True(engine.Journal.TryGetOutstanding(record.ArtId, out var accept));
        await engine.DrainPendingAsync(CancellationToken.None);
        Assert.True(engine.Index.TryGet(record.ArtId, out var before));
        time.Advance(TimeSpan.FromHours(8));

        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        var compact = await engine.CompactClosedSegmentAsync(before.Location.SegmentId, CancellationToken.None);
        Assert.Equal(ArticleCompactionOutcome.Committed, compact.Outcome);
        Assert.True(engine.Index.TryGet(record.ArtId, out var moved));
        Assert.Equal(accept.AcceptedUtc, moved.AcceptedUtc);
        Assert.Equal(before.AcceptedUtc, moved.AcceptedUtc);
        Assert.NotEqual(before.Location.SegmentId.Value, moved.Location.SegmentId.Value);
        Assert.Equal(ArticleStorageState.Present, moved.State);
    }

    [Fact]
    public async Task Eviction_preserves_arrival()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        await using var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time);
        var record = Article("<phase22-evict@seg.test>");
        engine.SuspendBackgroundPersist = true;
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
        Assert.True(engine.Journal.TryGetOutstanding(record.ArtId, out var accept));
        await engine.DrainPendingAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromDays(3));
        Assert.True(engine.TryEvict(record.ArtId));
        Assert.True(engine.Index.TryGet(record.ArtId, out var tombstone));
        Assert.Equal(ArticleStorageState.Evicted, tombstone.State);
        Assert.Equal(accept.AcceptedUtc, tombstone.AcceptedUtc);
        Assert.False(engine.TryRead(record.ArtId, out _));
    }

    [Fact]
    public async Task Checkpoint_preserves_arrival()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var record = Article("<phase22-checkpoint@seg.test>");
        DateTimeOffset accepted;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time))
        {
            engine.SuspendBackgroundPersist = true;
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            Assert.True(engine.Journal.TryGetOutstanding(record.ArtId, out var accept));
            accepted = accept.AcceptedUtc;
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(engine.CheckpointIndex() > 0);
        }

        time.Advance(TimeSpan.FromDays(2));
        await using var restarted = FileArticleStorageEngine.Open(dir.Options, timeProvider: time);
        Assert.True(restarted.Index.TryGet(record.ArtId, out var restored));
        Assert.Equal(accepted, restored.AcceptedUtc);
        Assert.Equal(3u, ArticleIndexSnapshotCodec.Version);
    }

    [Fact]
    public async Task Crash_after_present_does_not_regenerate_arrival()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var record = Article("<phase22-crash@seg.test>");
        DateTimeOffset accepted;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time))
        {
            engine.SuspendBackgroundPersist = true;
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            Assert.True(engine.Journal.TryGetOutstanding(record.ArtId, out var accept));
            accepted = accept.AcceptedUtc;
            time.Advance(TimeSpan.FromHours(4));
            engine.TestFaultPoint = FileArticleStorageEngine.PersistFaultPoint.AfterIndexCommit;
            _ = await Assert.ThrowsAsync<IOException>(() => engine.RecoverAsync(CancellationToken.None));
        }

        time.Advance(TimeSpan.FromDays(11));
        await using var restarted = FileArticleStorageEngine.Open(dir.Options, timeProvider: time);
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.True(restarted.Index.TryGet(record.ArtId, out var restored));
        Assert.Equal(accepted, restored.AcceptedUtc);
        Assert.NotEqual(time.GetUtcNow(), restored.AcceptedUtc);
        Assert.NotEqual(DateTimeOffset.MinValue, restored.AcceptedUtc);
    }

    [Fact]
    public void Torn_schema3_tail_keeps_the_prior_arrival()
    {
        using var dir = TempDir.Create();
        var row = Row("<phase22-torn@seg.test>", Arrival, sequence: 4);
        string path;
        using (var index = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(index.TryCommitPresent(row));
            path = Path.Combine(dir.Control, FileArticleIndex.IndexFileName);
        }

        var intact = File.ReadAllBytes(path);
        Assert.Equal(ArticleIndexRecordCodec.RecordLength, intact.Length);
        var torn = new byte[intact.Length + 14];
        intact.CopyTo(torn, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(torn.AsSpan(intact.Length, 4), (uint)ArticleIndexRecordCodec.RecordLength);
        File.WriteAllBytes(path, torn);

        using var reopened = FileArticleIndex.Open(dir.Options);
        Assert.True(reopened.TryGet(row.ArtId, out var restored));
        Assert.Equal(Arrival, restored.AcceptedUtc);
        Assert.Equal(intact.Length, new FileInfo(path).Length);
    }

    [Fact]
    public void Noncanonical_index_length_is_rejected()
    {
        using var dir = TempDir.Create();
        var row = Row("<phase22-canonical@seg.test>", Arrival, sequence: 4);
        var intact = ArticleIndexRecordCodec.Encode(row);
        var path = Path.Combine(dir.Control, FileArticleIndex.IndexFileName);
        var corrupt = new byte[intact.Length + 4];
        intact.CopyTo(corrupt, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(corrupt.AsSpan(intact.Length, 4), 96);
        File.WriteAllBytes(path, corrupt);

        var ex = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(dir.Options));
        Assert.Contains("CorruptLength", ex.Message, StringComparison.Ordinal);
        Assert.Equal(corrupt.Length, new FileInfo(path).Length);
    }

    [Fact]
    public void Index_frame_without_a_stored_arrival_is_rejected()
    {
        using var dir = TempDir.Create();
        var row = Row("<phase22-missing@seg.test>", Arrival, sequence: 4);
        var frame = ArticleIndexRecordCodec.Encode(row);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(frame.Length - 12, 8), 0);
        var crc = Crc32.HashToUInt32(frame.AsSpan(0, frame.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(frame.Length - 4, 4), crc);
        File.WriteAllBytes(Path.Combine(dir.Control, FileArticleIndex.IndexFileName), frame);

        var ex = Assert.Throws<ArticleIndexCorruptException>(() => FileArticleIndex.Open(dir.Options));
        Assert.Contains("Corrupt", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Segment_record_without_a_valid_arrival_is_rejected()
    {
        var artId = ArticleId.FromMessageId("<phase22-bad-segment@seg.test>"u8);
        var payload = "body"u8.ToArray();
        var frame = SegmentRecordCodec.Encode(artId, 1, payload, Arrival);
        BinaryPrimitives.WriteInt64LittleEndian(
            frame.AsSpan(SegmentRecordCodec.FixedHeaderLength + payload.Length, 8),
            0);
        var crc = Crc32.HashToUInt32(frame.AsSpan(0, frame.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(frame.Length - 4, 4), crc);

        Assert.False(SegmentRecordCodec.TryProveFramedRecord(
            frame,
            out _,
            out _,
            out _,
            out _,
            out var missing));
        Assert.Equal(SegmentRecordCodec.DecodeError.Corrupt, missing);

        frame = SegmentRecordCodec.Encode(artId, 1, payload, Arrival);
        frame[4] = 1;
        crc = Crc32.HashToUInt32(frame.AsSpan(0, frame.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(frame.Length - 4, 4), crc);
        Assert.False(SegmentRecordCodec.TryProveFramedRecord(
            frame,
            out _,
            out _,
            out _,
            out _,
            out var wrongSchema));
        Assert.Equal(SegmentRecordCodec.DecodeError.Corrupt, wrongSchema);
    }

    [Fact]
    public async Task Segment_record_keeps_the_accept_instant_across_restart()
    {
        using var dir = TempDir.Create();
        var time = new FakeTimeProvider(Arrival);
        var record = Article("<phase22-segment@seg.test>");
        DateTimeOffset accepted;
        StoredArticleLocation location;
        byte[] payload;
        await using (var engine = FileArticleStorageEngine.Open(dir.Options, timeProvider: time))
        {
            engine.SuspendBackgroundPersist = true;
            Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(record, CancellationToken.None)).Outcome);
            Assert.True(engine.Journal.TryGetOutstanding(record.ArtId, out var accept));
            accepted = accept.AcceptedUtc;
            await engine.DrainPendingAsync(CancellationToken.None);
            Assert.True(engine.Index.TryGet(record.ArtId, out var published));
            location = published.Location;
            Assert.True(engine.Segments.TryReadStoredArrival(location, record.ArtSize, out var onSegment));
            Assert.Equal(accepted, onSegment);
            Assert.True(engine.TryRead(record.ArtId, out var read));
            payload = read.ArtData.ToArray();
            Assert.Equal(accepted, read.Metadata.AcceptedUtc);
        }

        time.Advance(TimeSpan.FromDays(3));
        await using var restarted = FileArticleStorageEngine.Open(dir.Options, timeProvider: time);
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.True(restarted.Index.TryGet(record.ArtId, out var restored));
        Assert.Equal(accepted, restored.AcceptedUtc);
        Assert.NotEqual(time.GetUtcNow(), restored.AcceptedUtc);
        Assert.True(restarted.Segments.TryReadStoredArrival(location, record.ArtSize, out var segmentAgain));
        Assert.Equal(accepted, segmentAgain);
        Assert.True(restarted.TryRead(record.ArtId, out var reread));
        Assert.Equal(payload, reread.ArtData.ToArray());
    }

    [Fact]
    public void Journal_accept_rejects_a_missing_arrival()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new JournalAcceptRecord(1, 1, ArticleId.FromMessageId("<missing@seg.test>"u8), 1, 1, default, new byte[] { 1 }));
        Assert.Equal("acceptedUtc", error.ParamName);
    }

    private static StoredArticleMetadata Row(string messageId, DateTimeOffset acceptedUtc, ulong sequence) =>
        new(
            ArticleId.FromMessageId(Encoding.ASCII.GetBytes(messageId)),
            17,
            10,
            new StoredArticleLocation(new SegmentId(3), 8, 10),
            ArticleStorageState.Present,
            new DateTimeOffset(2024, 8, 23, 7, 30, 10, TimeSpan.Zero),
            sequence,
            acceptedUtc);

    private static ArticleRecord Article(string messageId)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: phase22\r\n\r\nbody\r\n");
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

        public ArticleStorageRuntimeOptions Options { get; }

        public string Control => Options.ControlDir;

        private string Root { get; }

        public static TempDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase22-" + Guid.NewGuid().ToString("N"));
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
