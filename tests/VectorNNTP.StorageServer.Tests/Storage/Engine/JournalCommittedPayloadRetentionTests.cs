using System.Buffers.Binary;
using System.IO.Hashing;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// After durable IndexCommitted, journal state no longer roots the Accept payload.
/// </summary>
public sealed class JournalCommittedPayloadRetentionTests
{
    [Fact]
    public void IncompleteAccept_RetainsTheAcceptRecord()
    {
        using var dir = TempControlDir.Create();
        var payload = Payload(0x11);
        using var journal = FileArticleJournal.Open(dir.Options);
        var accept = Append(journal, "<retain-accept@example.test>", payload);
        Assert.True(journal.TryGetSequenceRetention(accept.Sequence, out var retained));
        Assert.Same(accept, retained.RetainedAccept);
        Assert.False(retained.IndexCommitted);
        Assert.Equal(payload.Length, retained.ArtSize);
        Assert.True(payload.AsSpan().SequenceEqual(retained.RetainedAccept!.ArtData.Span));
    }

    [Fact]
    public async Task AcceptAndPhysicalWritten_RetainTheAcceptRecord()
    {
        using var dir = TempControlDir.Create();
        var payload = Payload(0x22);
        using var journal = FileArticleJournal.Open(dir.Options);
        var accept = Append(journal, "<retain-pw@example.test>", payload);
        var location = new StoredArticleLocation(new SegmentId(3), 10, payload.Length);
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, location),
                CancellationToken.None));

        Assert.True(journal.TryGetSequenceRetention(accept.Sequence, out var retained));
        Assert.Same(accept, retained.RetainedAccept);
        Assert.False(retained.IndexCommitted);
        Assert.Equal(location, retained.PhysicalWritten!.Value.Location);
        var incomplete = Assert.Single(journal.EnumerateIncomplete());
        Assert.Same(accept, incomplete.Accept);
        Assert.True(payload.AsSpan().SequenceEqual(incomplete.Accept.ArtData.Span));
    }

    [Fact]
    public async Task DurableIndexCommitted_ReleasesJournalRootAndKeepsCallerRecord()
    {
        using var dir = TempControlDir.Create();
        var payload = Payload(0x33);
        using var journal = FileArticleJournal.Open(dir.Options);
        var accept = Append(journal, "<retain-commit@example.test>", payload);
        var location = new StoredArticleLocation(new SegmentId(4), 0, payload.Length);
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

        Assert.True(payload.AsSpan().SequenceEqual(accept.ArtData.Span));
        Assert.True(journal.TryGetSequenceRetention(accept.Sequence, out var retained));
        Assert.Null(retained.RetainedAccept);
        Assert.True(retained.IndexCommitted);
        Assert.Equal(accept.Sequence, retained.Sequence);
        Assert.Equal(payload.Length, retained.ArtSize);
        Assert.Equal(location, retained.PhysicalWritten!.Value.Location);
        Assert.False(journal.TryGetOutstanding(accept.ArtId, out _));
        Assert.Empty(journal.EnumerateIncomplete());
        Assert.Equal(0, journal.OutstandingRecoverableBytes);
        Assert.Contains(
            (accept.Sequence, payload.Length),
            journal.CopyRetainedJournalSequences());
    }

    [Fact]
    public async Task ReplayedIndexCommitted_DoesNotRetainArtData()
    {
        using var dir = TempControlDir.Create();
        var payload = Payload(0x44);
        var location = new StoredArticleLocation(new SegmentId(8), 40, payload.Length);
        ulong sequence;
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            var accept = Append(journal, "<retain-replay@example.test>", payload);
            sequence = accept.Sequence;
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journal.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, sequence, location),
                    CancellationToken.None));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journal.AppendIndexCommittedAsync(
                    new JournalIndexCommittedRecord(1, sequence),
                    CancellationToken.None));
        }

        using var reopened = FileArticleJournal.Open(dir.Options);
        Assert.True(reopened.TryGetSequenceRetention(sequence, out var retained));
        Assert.Null(retained.RetainedAccept);
        Assert.True(retained.IndexCommitted);
        Assert.Equal(sequence, retained.Sequence);
        Assert.Equal(payload.Length, retained.ArtSize);
        Assert.Equal(location, retained.PhysicalWritten!.Value.Location);
        Assert.Empty(reopened.EnumerateIncomplete());
        Assert.Contains((sequence, payload.Length), reopened.CopyRetainedJournalSequences());
    }

    [Fact]
    public async Task PhysicalWrittenAfterIndexCommitted_UsesMetadataWithoutArtData()
    {
        using var dir = TempControlDir.Create();
        var payload = Payload(0x55);
        using var journal = FileArticleJournal.Open(dir.Options);
        var accept = Append(journal, "<retain-pw-after@example.test>", payload);
        var location = new StoredArticleLocation(new SegmentId(1), 0, payload.Length);
        var same = new JournalPhysicalWrittenRecord(1, accept.Sequence, location);
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendPhysicalWrittenAsync(same, CancellationToken.None));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendIndexCommittedAsync(
                new JournalIndexCommittedRecord(1, accept.Sequence),
                CancellationToken.None));

        Assert.Equal(
            JournalAppendOutcome.IdempotentNoOp,
            await journal.AppendPhysicalWrittenAsync(same, CancellationToken.None));
        Assert.Equal(
            JournalAppendOutcome.Conflict,
            await journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, new StoredArticleLocation(new SegmentId(2), 5, payload.Length)),
                CancellationToken.None));
        Assert.True(journal.TryGetSequenceRetention(accept.Sequence, out var retained));
        Assert.Null(retained.RetainedAccept);
        Assert.Equal(location, retained.PhysicalWritten!.Value.Location);
        Assert.True(payload.AsSpan().SequenceEqual(accept.ArtData.Span));
    }

    [Fact]
    public async Task AmbiguousIndexCommittedFlush_KeepsThePayloadUntilDurableSuccess()
    {
        using var dir = TempControlDir.Create();
        var payload = Payload(0x66);
        using var journal = FileArticleJournal.Open(dir.Options);
        var accept = Append(journal, "<retain-ambiguous@example.test>", payload);
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, new StoredArticleLocation(new SegmentId(1), 0, payload.Length)),
                CancellationToken.None));

        journal.TestBeforeDurableFlush = () => throw new IOException("flush");
        var pending = await Assert.ThrowsAsync<UnreconciledDurableTailException>(() =>
            journal.AppendIndexCommittedAsync(
                new JournalIndexCommittedRecord(1, accept.Sequence),
                CancellationToken.None).AsTask());
        Assert.Contains("not durable", pending.Message, StringComparison.Ordinal);
        Assert.True(journal.TryGetSequenceRetention(accept.Sequence, out var during));
        Assert.Same(accept, during.RetainedAccept);
        Assert.False(during.IndexCommitted);
        Assert.True(payload.AsSpan().SequenceEqual(accept.ArtData.Span));

        journal.TestBeforeDurableFlush = null;
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendIndexCommittedAsync(
                new JournalIndexCommittedRecord(1, accept.Sequence),
                CancellationToken.None));
        Assert.True(journal.TryGetSequenceRetention(accept.Sequence, out var after));
        Assert.Null(after.RetainedAccept);
        Assert.True(after.IndexCommitted);
        Assert.True(payload.AsSpan().SequenceEqual(accept.ArtData.Span));
    }

    [Fact]
    public async Task Checkpoint_OmitsCommittedPayloadAndKeepsIncompleteArtData()
    {
        using var dir = TempControlDir.Create();
        var committedPayload = Payload(0x71);
        var outstandingPayload = Payload(0x72);
        using (var journal = FileArticleJournal.Open(dir.Options))
        {
            var committed = Append(journal, "<retain-ck-done@example.test>", committedPayload);
            var outstanding = Append(journal, "<retain-ck-open@example.test>", outstandingPayload);
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journal.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, committed.Sequence, new StoredArticleLocation(new SegmentId(1), 0, committedPayload.Length)),
                    CancellationToken.None));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journal.AppendIndexCommittedAsync(
                    new JournalIndexCommittedRecord(1, committed.Sequence),
                    CancellationToken.None));
            Assert.Contains((committed.Sequence, committedPayload.Length), journal.CopyRetainedJournalSequences());
            Assert.Contains((outstanding.Sequence, outstandingPayload.Length), journal.CopyRetainedJournalSequences());

            _ = journal.CheckpointTruncateCommitted();
            Assert.False(journal.TryGetSequenceRetention(committed.Sequence, out _));
            Assert.True(journal.TryGetSequenceRetention(outstanding.Sequence, out var kept));
            Assert.Same(outstanding, kept.RetainedAccept);
            Assert.Equal([(outstanding.Sequence, outstandingPayload.Length)], journal.CopyRetainedJournalSequences());
        }

        var path = Path.Combine(dir.ControlDir, FileArticleJournal.JournalFileName);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(committedPayload));
        Assert.True(bytes.AsSpan().IndexOf(outstandingPayload) >= 0);
        Assert.Equal(
            [ArticleJournalFrameType.SequenceFence, ArticleJournalFrameType.Accept],
            FrameTypes(bytes));
    }

    [Fact]
    public async Task IndexCommittedArticleId_DoesNotBlockALaterJournalAccept()
    {
        using var dir = TempControlDir.Create();
        var first = Payload(0x81);
        var second = Payload(0x82);
        using var journal = FileArticleJournal.Open(dir.Options);
        var accept = Append(journal, "<retain-reaccept@example.test>", first);
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, new StoredArticleLocation(new SegmentId(1), 0, first.Length)),
                CancellationToken.None));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendIndexCommittedAsync(
                new JournalIndexCommittedRecord(1, accept.Sequence),
                CancellationToken.None));

        Assert.True(journal.TryAppendNewAccept(
            accept.ArtId,
            XxHash3.HashToUInt64(second),
            second.Length,
            DateTimeOffset.UtcNow,
            second,
            out var again,
            out var reject));
        Assert.Equal(default, reject);
        Assert.NotEqual(accept.Sequence, again.Sequence);
        Assert.True(journal.TryGetSequenceRetention(accept.Sequence, out var oldState));
        Assert.Null(oldState.RetainedAccept);
        Assert.True(journal.TryGetSequenceRetention(again.Sequence, out var newState));
        Assert.Same(again, newState.RetainedAccept);
        Assert.True(second.AsSpan().SequenceEqual(again.ArtData.Span));
    }

    private static JournalAcceptRecord Append(FileArticleJournal journal, string messageId, byte[] payload)
    {
        var artId = ArticleId.FromMessageId(System.Text.Encoding.ASCII.GetBytes(messageId));
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

    private static byte[] Payload(byte marker)
    {
        var payload = new byte[64 * 1024];
        payload.AsSpan().Fill(marker);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), 0xC0FFEE00 | marker);
        return payload;
    }

    private static ArticleJournalFrameType[] FrameTypes(byte[] bytes)
    {
        var types = new List<ArticleJournalFrameType>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            Assert.True(ArticleJournalFrameCodec.TryDecode(
                bytes.AsSpan(offset),
                out var type,
                out var length,
                out _,
                out var error), error.ToString());
            types.Add(type);
            offset += length;
        }

        return types.ToArray();
    }

    private sealed class TempControlDir : IDisposable
    {
        private TempControlDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public string ControlDir => Options.ControlDir;

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempControlDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-retain-" + Guid.NewGuid().ToString("N"));
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
