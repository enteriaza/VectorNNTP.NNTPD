using System.IO.Hashing;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Checkpoint base-image work stays outside the journal lock. Cutover must still keep
/// records that land during that window, and must not install over an unreconciled tail.
/// </summary>
public sealed class CheckpointCutoverTests
{
    [Fact]
    public async Task Pinned_payload_cannot_be_detached_until_the_pin_is_released()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var accept = new JournalAcceptRecord(
            1,
            4,
            ArticleId.FromMessageId("<pin@example.test>"u8),
            XxHash3.HashToUInt64(payload),
            payload.Length,
            DateTimeOffset.UnixEpoch,
            payload);
        accept.PermitPayloadDetach();
        accept.PinForCheckpoint();

        byte[]? detached = null;
        var detach = Task.Run(() => detached = accept.DetachPayload());
        Assert.True(SpinWait.SpinUntil(() => accept.DetachIsBlockedOnCheckpointPin, TimeSpan.FromSeconds(5)));
        Assert.False(detach.IsCompleted);
        Assert.True(accept.ArtData.Span.SequenceEqual(payload));
        Assert.True(accept.CheckpointPinnedArtData().Span.SequenceEqual(payload));

        accept.UnpinForCheckpoint();
        await detach.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(detached);
        Assert.Equal(payload, detached);
        Assert.Equal(0, accept.ArtData.Length);
    }

    [Fact]
    public async Task Accept_during_base_encode_is_retained_in_the_replacement()
    {
        using var dir = TempStorageDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        await CommitAsync(journal, "<cutover-committed@example.test>", [1, 2, 3]);
        JournalAcceptRecord? arrived = null;
        journal.TestWhileCheckpointEncoding = () =>
        {
            Assert.Empty(TempJournals(dir));
            arrived = Append(journal, "<cutover-arrived@example.test>", [4, 5, 6, 7]);
        };

        Assert.True(journal.CheckpointTruncateCommitted() > 0);
        Assert.NotNull(arrived);
        var arrivedId = arrived.ArtId;
        var arrivedSequence = arrived.Sequence;
        var arrivedBytes = arrived.ArtData.ToArray();
        journal.Dispose();

        using var reopened = FileArticleJournal.Open(dir.Options);
        Assert.True(reopened.TryGetOutstanding(arrivedId, out var recovered));
        Assert.Equal(arrivedSequence, recovered.Sequence);
        Assert.True(recovered.ArtData.Span.SequenceEqual(arrivedBytes));
        Assert.Null(reopened.EnumerateIncomplete().Single(item => item.Accept.Sequence == arrivedSequence).PhysicalWritten);
    }

    [Fact]
    public async Task Index_commit_completes_while_the_base_image_build_is_blocked()
    {
        using var dir = TempStorageDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        await CommitAsync(journal, "<cutover-held@example.test>", [1]);
        var open = Append(journal, "<cutover-open@example.test>", [2, 2]);
        var encoding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committedDuringEncode = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        journal.TestWhileCheckpointEncoding = () =>
        {
            encoding.TrySetResult();
            Wait(committedDuringEncode.Task);
        };

        var checkpoint = Task.Run(() => journal.CheckpointTruncateCommitted());
        try
        {
            await encoding.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var arrived = Append(journal, "<cutover-during@example.test>", [3, 3, 3]);
            var location = new StoredArticleLocation(new SegmentId(1), 0, arrived.ArtSize);
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journal.AppendPhysicalWrittenAsync(
                    new JournalPhysicalWrittenRecord(1, arrived.Sequence, location),
                    CancellationToken.None));
            Assert.Equal(
                JournalAppendOutcome.Applied,
                await journal.AppendIndexCommittedAsync(
                    new JournalIndexCommittedRecord(1, arrived.Sequence),
                    CancellationToken.None));
            Assert.False(checkpoint.IsCompleted);
        }
        finally
        {
            committedDuringEncode.TrySetResult();
        }

        Assert.True(await checkpoint.WaitAsync(TimeSpan.FromSeconds(5)) > 0);
        Assert.False(journal.TryGetOutstanding(
            ArticleId.FromMessageId("<cutover-during@example.test>"u8),
            out _));
        Assert.True(journal.TryGetOutstanding(open.ArtId, out var stillOpen));
        Assert.Equal(open.Sequence, stillOpen.Sequence);
    }

    [Fact]
    public async Task PhysicalWritten_after_snapshot_is_retained_even_if_index_committed_follows()
    {
        using var dir = TempStorageDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        await CommitAsync(journal, "<pw-committed@example.test>", [1]);
        var open = Append(journal, "<pw-open@example.test>", [9, 8, 7, 6]);
        var location = new StoredArticleLocation(new SegmentId(3), 10, open.ArtSize);
        journal.TestWhileCheckpointEncoding = () =>
        {
            Assert.Equal(JournalAppendOutcome.Applied, Wait(journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, open.Sequence, location),
                CancellationToken.None)));
            Assert.Equal(JournalAppendOutcome.Applied, Wait(journal.AppendIndexCommittedAsync(
                new JournalIndexCommittedRecord(1, open.Sequence),
                CancellationToken.None)));
        };

        Assert.True(journal.CheckpointTruncateCommitted() > 0);
        journal.Dispose();

        using var reopened = FileArticleJournal.Open(dir.Options);
        var recovered = Assert.Single(reopened.EnumerateIncomplete());
        Assert.Equal(open.Sequence, recovered.Accept.Sequence);
        Assert.True(recovered.Accept.ArtData.Span.SequenceEqual(open.ArtData.Span));
        Assert.Equal(location, recovered.PhysicalWritten?.Location);
        Assert.Equal(open.Sequence + 1, reopened.NextSequence);
    }

    [Fact]
    public async Task Fence_does_not_regress_when_a_later_sequence_is_committed_during_encode()
    {
        using var dir = TempStorageDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        await CommitAsync(journal, "<fence-old@example.test>", [1, 1]);
        var during = 0UL;
        journal.TestWhileCheckpointEncoding = () =>
        {
            var extra = Append(journal, "<fence-new@example.test>", [2, 2, 2]);
            during = extra.Sequence;
            Assert.Equal(JournalAppendOutcome.Applied, Wait(journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, extra.Sequence, new StoredArticleLocation(new SegmentId(1), 0, extra.ArtSize)),
                CancellationToken.None)));
            Assert.Equal(JournalAppendOutcome.Applied, Wait(journal.AppendIndexCommittedAsync(
                new JournalIndexCommittedRecord(1, extra.Sequence),
                CancellationToken.None)));
        };

        Assert.True(journal.CheckpointTruncateCommitted() > 0);
        Assert.Equal(during + 1, journal.NextSequence);
        journal.Dispose();

        using var reopened = FileArticleJournal.Open(dir.Options);
        Assert.Equal(during + 1, reopened.NextSequence);
        Assert.Empty(reopened.EnumerateIncomplete());
        var again = Append(reopened, "<fence-after@example.test>", [3]);
        Assert.Equal(during + 1, again.Sequence);
    }

    [Fact]
    public async Task Pending_tail_during_encode_prevents_cutover()
    {
        using var dir = TempStorageDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        var committed = await CommitAsync(journal, "<tail-committed@example.test>", [7, 7]);
        var afterTail = 0L;
        journal.TestWhileCheckpointEncoding = () =>
        {
            journal.TestBeforeDurableFlush = () => throw new IOException("tail");
            var pending = Assert.Throws<UnreconciledDurableTailException>(() =>
                Append(journal, "<tail-pending@example.test>", [8]));
            Assert.Contains("not durable", pending.Message, StringComparison.Ordinal);
            journal.TestBeforeDurableFlush = null;
            afterTail = journal.JournalPhysicalBytes;
        };

        var blocked = Assert.Throws<UnreconciledDurableTailException>(() => journal.CheckpointTruncateCommitted());
        Assert.Contains("pending", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(TempJournals(dir));
        Assert.Equal(afterTail, journal.JournalPhysicalBytes);
        Assert.True(journal.TryGetSequenceRetention(committed.Sequence, out var retained));
        Assert.True(retained.IndexCommitted);
    }

    [Fact]
    public async Task Flushed_temp_is_not_installed_when_cutover_faults_before_the_move()
    {
        using var dir = TempStorageDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        await CommitAsync(journal, "<order-committed@example.test>", [4, 5]);
        var open = Append(journal, "<order-open@example.test>", [6, 6, 6]);
        var before = journal.JournalPhysicalBytes;
        var sawLiveJournal = false;
        journal.TestWhileCheckpointEncoding = () =>
        {
            sawLiveJournal = journal.JournalPhysicalBytes == before;
            Assert.Empty(TempJournals(dir));
        };
        journal.CheckpointTestFault = point =>
        {
            if (point == FileArticleJournal.CheckpointFaultPoint.AfterTempFlushed)
            {
                Assert.NotEmpty(TempJournals(dir));
                Assert.Equal(before, journal.JournalPhysicalBytes);
                throw new IOException("stop-before-move");
            }
        };

        var fault = Assert.Throws<IOException>(() => journal.CheckpointTruncateCommitted());
        Assert.Contains("stop-before-move", fault.Message, StringComparison.Ordinal);
        Assert.True(sawLiveJournal);
        Assert.Empty(TempJournals(dir));
        Assert.Equal(before, journal.JournalPhysicalBytes);
        Assert.True(journal.TryGetOutstanding(open.ArtId, out var still));
        Assert.Equal(open.Sequence, still.Sequence);
    }

    [Fact]
    public async Task Checkpoint_encoding_holds_the_pin_until_the_payload_has_been_read()
    {
        using var dir = TempStorageDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        await CommitAsync(journal, "<detach-committed@example.test>", [1]);
        var open = Append(journal, "<detach-open@example.test>", [5, 5, 5, 5]);
        var owned = open.ArtData.ToArray();
        Task? detach = null;
        journal.TestWhileCheckpointEncoding = () =>
        {
            Assert.Equal(JournalAppendOutcome.Applied, Wait(journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, open.Sequence, new StoredArticleLocation(new SegmentId(1), 0, open.ArtSize)),
                CancellationToken.None)));
            Assert.Equal(JournalAppendOutcome.Applied, Wait(journal.AppendIndexCommittedAsync(
                new JournalIndexCommittedRecord(1, open.Sequence),
                CancellationToken.None)));
            detach = Task.Run(() => open.DetachPayload());
            Assert.True(SpinWait.SpinUntil(() => open.DetachIsBlockedOnCheckpointPin, TimeSpan.FromSeconds(5)));
            Assert.True(open.ArtData.Span.SequenceEqual(owned));
        };

        Assert.True(journal.CheckpointTruncateCommitted() > 0);
        Assert.NotNull(detach);
        await detach.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, open.ArtData.Length);
        journal.Dispose();

        using var reopened = FileArticleJournal.Open(dir.Options);
        var recovered = Assert.Single(reopened.EnumerateIncomplete());
        Assert.True(recovered.Accept.ArtData.Span.SequenceEqual(owned));
    }

    [Fact]
    public async Task Capacity_reservation_is_released_when_cutover_growth_is_refused()
    {
        using var dir = TempStorageDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        await CommitAsync(journal, "<cap-committed@example.test>", [1, 2]);
        var capacity = new LedgerCapacity { DenyIncrease = true };
        journal.CheckpointCapacity = capacity.Create();
        journal.TestWhileCheckpointEncoding = () =>
            Append(journal, "<cap-arrived@example.test>", [3, 3, 3]);

        var denied = Assert.Throws<CheckpointCapacityDeniedException>(() => journal.CheckpointTruncateCommitted());
        Assert.True(denied.RequiredBytes > 0);
        Assert.Equal(0, capacity.Ledger.CheckpointReservedBytes);
        Assert.Empty(TempJournals(dir));
        Assert.Single(capacity.ReserveCalls);
        Assert.NotEmpty(capacity.IncreaseCalls);
        Assert.True(journal.TryGetOutstanding(
            ArticleId.FromMessageId("<cap-arrived@example.test>"u8),
            out _));
    }

    [Fact]
    public async Task Capacity_reservation_is_released_when_install_faults()
    {
        using var dir = TempStorageDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        await CommitAsync(journal, "<cap-fault@example.test>", [1]);
        var capacity = new LedgerCapacity();
        journal.CheckpointCapacity = capacity.Create();
        var held = 0L;
        journal.CheckpointTestFault = point =>
        {
            if (point != FileArticleJournal.CheckpointFaultPoint.AfterTempFlushed)
            {
                return;
            }

            held = capacity.Ledger.CheckpointReservedBytes;
            throw new IOException("release-me");
        };

        Assert.Throws<IOException>(() => journal.CheckpointTruncateCommitted());
        Assert.True(held > 0);
        Assert.Equal(0, capacity.Ledger.CheckpointReservedBytes);
        Assert.Empty(TempJournals(dir));
    }

    [Fact]
    public async Task Compaction_change_during_encode_defers_instead_of_retrying()
    {
        using var dir = TempStorageDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        var committed = await CommitAsync(journal, "<cutover-compaction@example.test>", [1, 2]);
        var capacity = new LedgerCapacity();
        journal.CheckpointCapacity = capacity.Create();
        var encodes = 0;
        journal.TestWhileCheckpointEncoding = () =>
        {
            var n = Interlocked.Increment(ref encodes);
            if (n > 1)
            {
                throw new InvalidOperationException("checkpoint retried after compaction drift");
            }

            var compactionId = journal.AllocateCompactionId();
            Assert.Equal(JournalAppendOutcome.Applied, Wait(journal.AppendCompactionBeginAsync(
                new JournalCompactionBeginRecord(1, compactionId, new SegmentId(9), 1),
                CancellationToken.None)));
        };

        var deferred = Assert.Throws<CheckpointCompactionChangedException>(() => journal.CheckpointTruncateCommitted());
        Assert.Contains("not installed", deferred.Message, StringComparison.Ordinal);
        Assert.Equal(1, encodes);
        Assert.Equal(0, capacity.Ledger.CheckpointReservedBytes);
        Assert.Empty(TempJournals(dir));
        Assert.True(journal.TryGetSequenceRetention(committed.Sequence, out var stillCommitted));
        Assert.True(stillCommitted.IndexCommitted);
        Assert.Single(journal.EnumerateCompactions());

        journal.TestWhileCheckpointEncoding = null;
        Assert.True(journal.CheckpointTruncateCommitted() > 0);
        Assert.False(journal.TryGetSequenceRetention(committed.Sequence, out _));
        Assert.Single(journal.EnumerateCompactions());
        Assert.Equal(0, capacity.Ledger.CheckpointReservedBytes);
        Assert.Empty(TempJournals(dir));
    }

    [Fact]
    public async Task Two_checkpoints_cannot_overlap()
    {
        using var dir = TempStorageDir.Create();
        using var journal = FileArticleJournal.Open(dir.Options);
        var entered = 0;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        journal.TestBeforeCheckpointSnapshot = () =>
        {
            var n = Interlocked.Increment(ref entered);
            if (n == 1)
            {
                firstEntered.TrySetResult();
                Wait(releaseFirst.Task);
            }
        };

        var first = Task.Run(() => journal.CheckpointTruncateCommitted());
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Task.Run(() =>
        {
            secondStarted.TrySetResult();
            return journal.CheckpointTruncateCommitted();
        });
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var overlapped = await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(1)), second);
        Assert.NotSame(second, overlapped);
        Assert.Equal(1, Volatile.Read(ref entered));

        releaseFirst.TrySetResult();
        Assert.Equal(0, await first);
        Assert.Equal(0, await second);
        Assert.Equal(2, Volatile.Read(ref entered));
    }

    /// <summary>
    /// Checkpoint test hooks are synchronous. Blocking here is the coordination, not a sleep.
    /// </summary>
    private static T Wait<T>(ValueTask<T> task)
    {
#pragma warning disable xUnit1031
        return task.AsTask().GetAwaiter().GetResult();
#pragma warning restore xUnit1031
    }

    /// <summary>
    /// Checkpoint test hooks are synchronous. Blocking here is the coordination, not a sleep.
    /// </summary>
    private static void Wait(Task task)
    {
#pragma warning disable xUnit1031
        task.GetAwaiter().GetResult();
#pragma warning restore xUnit1031
    }

    private static JournalAcceptRecord Append(FileArticleJournal journal, string messageId, byte[] body)
    {
        Assert.True(journal.TryAppendNewAccept(
            ArticleId.FromMessageId(Encoding.ASCII.GetBytes(messageId)),
            XxHash3.HashToUInt64(body),
            body.Length,
            DateTimeOffset.UtcNow,
            body,
            out var accept,
            out _));
        return accept;
    }

    private static async Task<JournalAcceptRecord> CommitAsync(FileArticleJournal journal, string messageId, byte[] body)
    {
        var accept = Append(journal, messageId, body);
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, new StoredArticleLocation(new SegmentId(1), 0, accept.ArtSize)),
                CancellationToken.None));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendIndexCommittedAsync(new JournalIndexCommittedRecord(1, accept.Sequence), CancellationToken.None));
        return accept;
    }

    private static string JournalPath(TempStorageDir dir) =>
        Path.Combine(dir.Options.ControlDir, FileArticleJournal.JournalFileName);

    private static string[] TempJournals(TempStorageDir dir) =>
        Directory.GetFiles(dir.Options.ControlDir, ".article.journal.*.tmp");

    private sealed class LedgerCapacity
    {
        public ProcessLocalCapacityLedger Ledger { get; } = new();

        public bool DenyIncrease { get; set; }

        public List<long> ReserveCalls { get; } = [];

        public List<long> IncreaseCalls { get; } = [];

        public CheckpointCapacityReservation Create() =>
            new()
            {
                TryReserve = bytes =>
                {
                    ReserveCalls.Add(bytes);
                    return Ledger.ReserveCheckpoint(bytes);
                },
                TryIncrease = (id, extra) =>
                {
                    IncreaseCalls.Add(extra);
                    if (DenyIncrease)
                    {
                        return false;
                    }

                    return Ledger.TryIncreaseCheckpoint(id, extra);
                },
                Release = id => Ledger.ReleaseCheckpoint(id),
            };
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
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-cutover-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            Directory.CreateDirectory(control);
            return new TempStorageDir(
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
                // Best-effort cleanup for a file still open in a failed test.
            }
        }
    }
}
