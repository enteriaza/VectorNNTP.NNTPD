using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// <see cref="JournalAcceptRecord.DetachPayload"/> transfers one owned buffer after durable IndexCommitted.
/// </summary>
public sealed class JournalAcceptRecordDetachTests
{
    [Fact]
    public void Detach_returns_the_owned_buffer()
    {
        var record = CreateRecord([1, 2, 3, 4]);
        var owned = Payload(record.ArtData);
        record.PermitPayloadDetach();

        var detached = record.DetachPayload();

        Assert.Same(owned, detached);
        Assert.Equal([1, 2, 3, 4], detached);
    }

    [Fact]
    public void Detach_stops_exposing_the_buffer()
    {
        var record = CreateRecord([9, 8, 7]);
        var owned = Payload(record.ArtData);
        record.PermitPayloadDetach();

        var detached = record.DetachPayload();

        Assert.Same(owned, detached);
        Assert.Equal(0, record.ArtData.Length);
        Assert.False(Exposes(record.ArtData, detached));
        detached[0] ^= 0xFF;
        Assert.Equal(0, record.ArtData.Length);
    }

    [Fact]
    public void Second_detach_does_not_return_the_buffer()
    {
        var record = CreateRecord([4, 5, 6, 7]);
        record.PermitPayloadDetach();
        var detached = record.DetachPayload();

        var again = Assert.Throws<InvalidOperationException>(record.DetachPayload);

        Assert.Contains("already detached", again.Message, StringComparison.Ordinal);
        Assert.False(Exposes(record.ArtData, detached));
        Assert.Equal([4, 5, 6, 7], detached);
    }

    [Fact]
    public async Task Release_drops_the_sequence_root_and_leaves_the_caller_buffer()
    {
        using var dir = TempControlDir.Create();
        var input = new byte[] { 1, 2, 3, 4 };
        using var journal = FileArticleJournal.Open(dir.Options);
        var accept = Append(journal, "<detach-release@example.test>", input);
        var owned = Payload(accept.ArtData);
        var location = new StoredArticleLocation(new SegmentId(1), 0, input.Length);
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, location),
                CancellationToken.None));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendIndexCommittedAsync(
                new JournalIndexCommittedRecord(1, accept.Sequence),
                CancellationToken.None));

        Assert.True(journal.TryGetSequenceRetention(accept.Sequence, out var retained));
        Assert.Null(retained.RetainedAccept);
        Assert.True(retained.IndexCommitted);
        Assert.Empty(journal.EnumerateIncomplete());
        Assert.Same(owned, Payload(accept.ArtData));

        var detached = accept.DetachPayload();
        Assert.Same(owned, detached);
        Assert.False(Exposes(accept.ArtData, detached));
        Assert.Throws<InvalidOperationException>(accept.DetachPayload);
    }

    [Fact]
    public async Task Detach_before_durable_index_committed_is_refused()
    {
        using var dir = TempControlDir.Create();
        var input = new byte[] { 5, 6, 7, 8 };
        using var journal = FileArticleJournal.Open(dir.Options);
        var accept = Append(journal, "<detach-early@example.test>", input);
        var owned = Payload(accept.ArtData);

        var beforeCommit = Assert.Throws<InvalidOperationException>(accept.DetachPayload);
        Assert.Contains("only after durable IndexCommitted", beforeCommit.Message, StringComparison.Ordinal);
        Assert.Same(owned, Payload(accept.ArtData));

        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, new StoredArticleLocation(new SegmentId(1), 0, input.Length)),
                CancellationToken.None));
        var beforeIndexCommitted = Assert.Throws<InvalidOperationException>(accept.DetachPayload);
        Assert.Contains("only after durable IndexCommitted", beforeIndexCommitted.Message, StringComparison.Ordinal);
        Assert.Same(owned, Payload(accept.ArtData));
    }

    [Fact]
    public async Task IndexCommitted_flush_failure_keeps_the_payload_attached()
    {
        using var dir = TempControlDir.Create();
        var input = new byte[] { 9, 9, 1, 2 };
        using var journal = FileArticleJournal.Open(dir.Options);
        var accept = Append(journal, "<detach-flush@example.test>", input);
        var owned = Payload(accept.ArtData);
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, new StoredArticleLocation(new SegmentId(2), 0, input.Length)),
                CancellationToken.None));

        journal.TestBeforeDurableFlush = () => throw new IOException("index committed flush failed");
        var pending = await Assert.ThrowsAsync<UnreconciledDurableTailException>(() =>
            journal.AppendIndexCommittedAsync(
                new JournalIndexCommittedRecord(1, accept.Sequence),
                CancellationToken.None).AsTask());
        Assert.Contains("not durable", pending.Message, StringComparison.Ordinal);

        Assert.True(journal.TryGetSequenceRetention(accept.Sequence, out var during));
        Assert.Same(accept, during.RetainedAccept);
        Assert.False(during.IndexCommitted);
        Assert.Same(owned, Payload(accept.ArtData));
        var refused = Assert.Throws<InvalidOperationException>(accept.DetachPayload);
        Assert.Contains("only after durable IndexCommitted", refused.Message, StringComparison.Ordinal);
        Assert.Same(owned, Payload(accept.ArtData));

        journal.TestBeforeDurableFlush = null;
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendIndexCommittedAsync(
                new JournalIndexCommittedRecord(1, accept.Sequence),
                CancellationToken.None));
        Assert.True(journal.TryGetSequenceRetention(accept.Sequence, out var after));
        Assert.Null(after.RetainedAccept);
        Assert.True(after.IndexCommitted);
        Assert.Same(owned, accept.DetachPayload());
        Assert.False(Exposes(accept.ArtData, owned));
    }

    private static bool Exposes(ReadOnlyMemory<byte> artData, byte[] payload) =>
        MemoryMarshal.TryGetArray(artData, out ArraySegment<byte> segment) && ReferenceEquals(segment.Array, payload);

    private static byte[] Payload(ReadOnlyMemory<byte> artData)
    {
        Assert.True(MemoryMarshal.TryGetArray(artData, out ArraySegment<byte> segment));
        Assert.NotNull(segment.Array);
        Assert.Equal(0, segment.Offset);
        Assert.Equal(artData.Length, segment.Count);
        return segment.Array;
    }

    private static JournalAcceptRecord CreateRecord(byte[] artData)
    {
        var artId = ArticleId.FromMessageId("<detach-unit@example.test>"u8);
        return new JournalAcceptRecord(1, 7, artId, 11, artData.Length, DateTimeOffset.UnixEpoch, artData);
    }

    private static JournalAcceptRecord Append(FileArticleJournal journal, string messageId, byte[] payload)
    {
        var artId = ArticleId.FromMessageId(Encoding.ASCII.GetBytes(messageId));
        Assert.True(journal.TryAppendNewAccept(
            artId,
            XxHash3.HashToUInt64(payload),
            payload.Length,
            DateTimeOffset.UtcNow,
            payload,
            out var accept,
            out _));
        return accept;
    }

    private sealed class TempControlDir : IDisposable
    {
        private TempControlDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempControlDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-detach-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            Directory.CreateDirectory(control);
            return new TempControlDir(
                root,
                new ArticleStorageRuntimeOptions(
                    ControlDir: control,
                    SegmentDir: Path.Combine(root, "cache"),
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
        }

        private string Root { get; }
    }
}
