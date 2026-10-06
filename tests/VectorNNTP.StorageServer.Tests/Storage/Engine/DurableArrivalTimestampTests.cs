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

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// The original Accept instant stays on the index row through publication, relocation,
/// checkpoint, restart, eviction, and a torn schema 3 tail. Schema 2 rows stay unknown.
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
    public void Schema2_index_opens_without_inventing_an_arrival()
    {
        using var dir = TempDir.Create();
        var legacy = Row("<phase22-legacy@seg.test>", DateTimeOffset.MinValue, sequence: 4);
        var legacyFrame = EncodeSchema2(legacy);
        var path = Path.Combine(dir.Control, FileArticleIndex.IndexFileName);
        File.WriteAllBytes(path, legacyFrame);
        WriteSchema2Snapshot(dir.Control, legacy with { Sequence = 9UL }, coveredIndexLength: legacyFrame.Length);

        using (var index = FileArticleIndex.Open(dir.Options))
        {
            Assert.True(index.TryGet(legacy.ArtId, out var fromSnapshot));
            Assert.Equal(9UL, fromSnapshot.Sequence);
            Assert.Equal(DateTimeOffset.MinValue, fromSnapshot.AcceptedUtc);

            var current = Row("<phase22-new@seg.test>", Arrival, sequence: 11);
            Assert.True(index.TryCommitPresent(current));
            _ = index.Checkpoint();
        }

        using var reopened = FileArticleIndex.Open(dir.Options);
        Assert.True(reopened.TryGet(legacy.ArtId, out var oldRow));
        Assert.Equal(DateTimeOffset.MinValue, oldRow.AcceptedUtc);
        Assert.Equal(9UL, oldRow.Sequence);
        var currentId = ArticleId.FromMessageId("<phase22-new@seg.test>"u8);
        Assert.True(reopened.TryGet(currentId, out var newRow));
        Assert.Equal(Arrival, newRow.AcceptedUtc);
        Assert.Equal(ArticleStorageState.Present, newRow.State);
    }

    [Fact]
    public void Schema2_frame_then_torn_schema3_frame_keeps_the_legacy_row()
    {
        using var dir = TempDir.Create();
        var legacy = Row("<phase22-mixed-torn@seg.test>", DateTimeOffset.MinValue, sequence: 2);
        var path = Path.Combine(dir.Control, FileArticleIndex.IndexFileName);
        var legacyFrame = EncodeSchema2(legacy);
        var torn = new byte[legacyFrame.Length + 14];
        legacyFrame.CopyTo(torn, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(torn.AsSpan(legacyFrame.Length, 4), (uint)ArticleIndexRecordCodec.RecordLength);
        File.WriteAllBytes(path, torn);

        using var index = FileArticleIndex.Open(dir.Options);
        Assert.True(index.TryGet(legacy.ArtId, out var restored));
        Assert.Equal(DateTimeOffset.MinValue, restored.AcceptedUtc);
        Assert.Equal(legacy.Sequence, restored.Sequence);
        Assert.Equal(legacyFrame.Length, new FileInfo(path).Length);
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

    private static byte[] EncodeSchema2(in StoredArticleMetadata metadata)
    {
        var buffer = new byte[ArticleIndexRecordCodec.Schema2RecordLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)buffer.Length);
        buffer[4] = ArticleIndexRecordCodec.Schema2Version;
        var o = 8;
        metadata.ArtId.CopyTo(buffer.AsSpan(o, ArticleId.Length));
        o += ArticleId.Length;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(o, 8), metadata.ArtHash);
        o += 8;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(o, 4), metadata.ArtSize);
        o += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(o, 8), metadata.Location.SegmentId.Value);
        o += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(o, 8), metadata.Location.Offset);
        o += 8;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(o, 4), metadata.Location.Length);
        o += 4;
        buffer[o++] = (byte)metadata.State;
        o += 3;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(o, 8), metadata.LastAccessUtc.UtcTicks);
        o += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(o, 8), metadata.Sequence);
        o += 8;
        var crc = Crc32.HashToUInt32(buffer.AsSpan(0, o));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(o, 4), crc);
        return buffer;
    }

    private static void WriteSchema2Snapshot(string controlDir, in StoredArticleMetadata row, long coveredIndexLength)
    {
        var frame = EncodeSchema2(row);
        Span<byte> header = stackalloc byte[ArticleIndexSnapshotCodec.HeaderLength];
        "VNIS"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], ArticleIndexSnapshotCodec.Schema2Version);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], (ulong)coveredIndexLength);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], 1);
        var headerCrc = Crc32.HashToUInt32(header[..40]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], headerCrc);
        var bodyCrc = Crc32.HashToUInt32(frame);
        var snapshot = new byte[header.Length + frame.Length + 4];
        header.CopyTo(snapshot);
        frame.CopyTo(snapshot.AsSpan(header.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(snapshot.AsSpan(snapshot.Length - 4, 4), bodyCrc);
        File.WriteAllBytes(Path.Combine(controlDir, FileArticleIndex.SnapshotFileName), snapshot);
    }

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
