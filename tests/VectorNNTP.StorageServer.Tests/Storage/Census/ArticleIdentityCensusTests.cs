using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Census;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.FileIndex;
using VectorNNTP.StorageServer.Storage.Engine.FileJournal;

namespace VectorNNTP.StorageServer.Tests.Storage.Census;

public sealed class ArticleIdentityCensusTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-30T00:00:00Z");

    [Fact]
    public void PresentEvictedAndInvalid_AreMetadataRows()
    {
        using var dir = TempControl.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        using var journal = FileArticleJournal.Open(dir.Options);
        var present = Commit(index, "<present@example.test>");
        var evicted = Commit(index, "<evicted@example.test>");
        var invalid = Commit(index, "<invalid@example.test>");
        Assert.True(index.TrySetState(evicted.ArtId, ArticleStorageState.Evicted, Now));
        Assert.True(index.TrySetState(invalid.ArtId, ArticleStorageState.Invalid, Now));

        var census = ArticleIdentityCensus.Capture(index, journal, serverId: 7, pageSize: 1);
        Assert.Equal(3, census.PageCount);
        var rows = Pages(census);
        Assert.Equal(ArticleCensusState.Present, Row(rows, present.ArtId).State);
        Assert.True(Row(rows, present.ArtId).IsHolder);
        Assert.False(Row(rows, present.ArtId).HasPhysicalWritten);
        Assert.Equal(ArticleCensusState.Evicted, Row(rows, evicted.ArtId).State);
        Assert.False(Row(rows, evicted.ArtId).IsHolder);
        Assert.Equal(ArticleCensusState.Invalid, Row(rows, invalid.ArtId).State);
        Assert.False(Row(rows, invalid.ArtId).IsHolder);
        Assert.All(rows, static row => Assert.Equal(7, row.ServerId));
        var missing = ArticleId.FromMessageId("<missing@example.test>"u8);
        Assert.DoesNotContain(rows, row => row.ArticleId == missing);
        AssertNoArtData(typeof(ArticleCensusRow));
        AssertNoArtData(typeof(ArticleIndexIdentity));
    }

    [Fact]
    public async Task AcceptedPending_ProjectsHashAndPhysicalWritten_WithoutArtData()
    {
        using var dir = TempControl.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        using var journal = FileArticleJournal.Open(dir.Options);
        var bare = ArticleId.FromMessageId("<bare@example.test>"u8);
        var written = ArticleId.FromMessageId("<written@example.test>"u8);
        Assert.True(journal.TryAppendNewAccept(bare, 11, 4, Now, Bare, out _, out _));
        Assert.True(journal.TryAppendNewAccept(written, 22, 4, Now, Written, out var accept, out _));
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, accept.Sequence, Location(4)),
                CancellationToken.None));

        var census = ArticleIdentityCensus.Capture(index, journal, 4);
        var rows = Pages(census);
        var bareRow = Row(rows, bare);
        var writtenRow = Row(rows, written);
        Assert.Equal(ArticleCensusState.AcceptedPending, bareRow.State);
        Assert.True(bareRow.IsHolder);
        Assert.False(bareRow.HasPhysicalWritten);
        Assert.Equal(11UL, bareRow.ArtHash);
        Assert.Equal(4, bareRow.ArtSize);
        Assert.Equal(ArticleCensusState.AcceptedPending, writtenRow.State);
        Assert.True(writtenRow.HasPhysicalWritten);
        Assert.Equal(22UL, writtenRow.ArtHash);
        Assert.False(bareRow.ConflictingReservation);
        Assert.Contains(
            journal.EnumerateIncomplete(),
            static sequence => sequence.Accept.ArtData.Span.SequenceEqual(Bare));
        Assert.Contains(
            journal.EnumerateIncomplete(),
            static sequence => sequence.Accept.ArtData.Span.SequenceEqual(Written));
        AssertPayloadAbsent(census, Bare, Written);
        AssertPayloadAbsent(journal.CopyIncompleteIdentities(), Bare, Written);
    }

    [Fact]
    public async Task ConflictingIncompleteSequences_AreNotACleanTarget()
    {
        using var dir = TempControl.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        using var journal = FileArticleJournal.Open(dir.Options);
        var id = ArticleId.FromMessageId("<conflict@example.test>"u8);
        Assert.True(journal.TryAppendNewAccept(id, 1, 4, Now, Bare, out var first, out _));
        await journal.AppendAcceptAsync(
            new JournalAcceptRecord(1, first.Sequence + 1, id, 99, 4, Now, Written),
            CancellationToken.None);

        var row = Assert.Single(Pages(ArticleIdentityCensus.Capture(index, journal, 4)));
        Assert.Equal(ArticleCensusState.AcceptedPending, row.State);
        Assert.True(row.ConflictingReservation);
        Assert.True(row.IsHolder);
        Assert.Equal(1UL, row.ArtHash);
    }

    [Fact]
    public async Task MatchingIncompleteSequences_CollapseToOneHolder()
    {
        using var dir = TempControl.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        using var journal = FileArticleJournal.Open(dir.Options);
        var id = ArticleId.FromMessageId("<collapse@example.test>"u8);
        Assert.True(journal.TryAppendNewAccept(id, 11, 4, Now, Bare, out var first, out _));
        await journal.AppendAcceptAsync(
            new JournalAcceptRecord(1, first.Sequence + 1, id, 11, 4, Now, Bare),
            CancellationToken.None);
        Assert.Equal(
            JournalAppendOutcome.Applied,
            await journal.AppendPhysicalWrittenAsync(
                new JournalPhysicalWrittenRecord(1, first.Sequence + 1, Location(4)),
                CancellationToken.None));

        var identities = journal.CopyIncompleteIdentities();
        Assert.Equal(2, identities.Length);
        AssertNoArtData(typeof(JournalReservationIdentity));
        var row = Assert.Single(Pages(ArticleIdentityCensus.Capture(index, journal, 4)));
        Assert.Equal(id, row.ArticleId);
        Assert.Equal(ArticleCensusState.AcceptedPending, row.State);
        Assert.True(row.IsHolder);
        Assert.True(row.HasPhysicalWritten);
        Assert.False(row.ConflictingReservation);
        Assert.Equal(11UL, row.ArtHash);
        Assert.Equal(4, row.ArtSize);
    }

    [Fact]
    public async Task NewArticleDuringCensus_IsIncluded_AndPublishedAcceptBecomesPresent()
    {
        using var dir = TempControl.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        using var journal = FileArticleJournal.Open(dir.Options);
        var pending = ArticleId.FromMessageId("<pending@example.test>"u8);
        Assert.True(journal.TryAppendNewAccept(pending, 5, 4, Now, Bare, out var accept, out _));
        var arrived = false;
        var census = ArticleIdentityCensus.Capture(
            index,
            journal,
            9,
            betweenSamples: () =>
            {
                var created = Commit(index, "<arrived@example.test>");
                Assert.Equal(
                    JournalAppendOutcome.Applied,
                    Wait(journal.AppendPhysicalWrittenAsync(
                        new JournalPhysicalWrittenRecord(1, accept.Sequence, Location(4)),
                        CancellationToken.None)));
                Assert.True(index.TryCommitPresent(new StoredArticleMetadata(
                    pending,
                    5,
                    4,
                    Location(4),
                    ArticleStorageState.Present,
                    Now,
                    1UL)));
                Assert.Equal(
                    JournalAppendOutcome.Applied,
                    Wait(journal.AppendIndexCommittedAsync(
                        new JournalIndexCommittedRecord(1, accept.Sequence),
                        CancellationToken.None)));
                arrived = created.ArtId != default;
            });

        Assert.True(arrived);
        var rows = Pages(census);
        Assert.Contains(rows, static row => row.ArtHash == 0 || row.State == ArticleCensusState.Present);
        Assert.Equal(ArticleCensusState.Present, Row(rows, pending).State);
        Assert.True(Row(rows, pending).IsHolder);
        Assert.Contains(rows, row => row.ArticleId != pending && row.State == ArticleCensusState.Present);
    }

    [Fact]
    public void EvictionBetweenSamples_DoesNotDropAnObservedHolder()
    {
        using var dir = TempControl.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        using var journal = FileArticleJournal.Open(dir.Options);
        var present = Commit(index, "<held@example.test>");
        var census = ArticleIdentityCensus.Capture(
            index,
            journal,
            3,
            betweenSamples: () =>
            {
                var done = new ManualResetEventSlim(false);
                var evicted = false;
                Exception? error = null;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        evicted = index.TrySetState(present.ArtId, ArticleStorageState.Evicted, Now);
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    finally
                    {
                        done.Set();
                    }
                });
                Assert.True(done.Wait(TimeSpan.FromSeconds(2)));
                if (error is not null)
                {
                    throw error;
                }

                Assert.True(evicted);
            });
        var row = Assert.Single(Pages(census));
        Assert.Equal(present.ArtId, row.ArticleId);
        Assert.Equal(ArticleCensusState.Present, row.State);
        Assert.True(row.IsHolder);
    }

    [Fact]
    public void PagingDoesNotHoldIndexOrJournalLocks()
    {
        using var dir = TempControl.Create();
        using var index = FileArticleIndex.Open(dir.Options);
        using var journal = FileArticleJournal.Open(dir.Options);
        var present = Commit(index, "<page@example.test>");
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var census = ArticleIdentityCensus.Capture(
            index,
            journal,
            1,
            pageSize: 1,
            duringPage: page =>
            {
                if (page != 0)
                {
                    return;
                }

                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(2)));
            });

        ArticleCensusPage page = default;
        Exception? pageError = null;
        var pageDone = new ManualResetEventSlim(false);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                page = census.GetPage(0);
            }
            catch (Exception ex)
            {
                pageError = ex;
            }
            finally
            {
                pageDone.Set();
            }
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        var saw = false;
        Exception? readError = null;
        var readDone = new ManualResetEventSlim(false);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                saw = index.TryGet(present.ArtId, out StoredArticleMetadata metadata)
                    && metadata.ArtId == present.ArtId
                    && !journal.TryGetOutstanding(present.ArtId, out JournalAcceptRecord _);
            }
            catch (Exception ex)
            {
                readError = ex;
            }
            finally
            {
                readDone.Set();
            }
        });
        try
        {
            Assert.True(readDone.Wait(TimeSpan.FromSeconds(2)));
            if (readError is not null)
            {
                throw readError;
            }

            Assert.True(saw);
        }
        finally
        {
            release.Set();
            Assert.True(pageDone.Wait(TimeSpan.FromSeconds(2)));
        }

        if (pageError is not null)
        {
            throw pageError;
        }

        Assert.Equal(present.ArtId, Assert.Single(page.Rows).ArticleId);
    }

    [Fact]
    public void FrozenPagesRemainReadableAfterTheIndexIsDisposed()
    {
        using var dir = TempControl.Create();
        var index = FileArticleIndex.Open(dir.Options);
        var journal = FileArticleJournal.Open(dir.Options);
        var present = Commit(index, "<frozen@example.test>");
        var census = ArticleIdentityCensus.Capture(index, journal, 8);
        index.Dispose();
        journal.Dispose();
        var row = Assert.Single(Pages(census));
        Assert.Equal(present.ArtId, row.ArticleId);
        Assert.Equal(present.ArtHash, row.ArtHash);
        Assert.Equal(present.ArtSize, row.ArtSize);
    }

    private static readonly byte[] Bare = "bare"u8.ToArray();

    private static readonly byte[] Written = "wrot"u8.ToArray();

    private static T Wait<T>(ValueTask<T> task)
    {
        var done = new ManualResetEventSlim(false);
        T result = default!;
        Exception? error = null;
        _ = task.AsTask().ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    error = completed.Exception?.GetBaseException();
                }
                else
                {
                    result = completed.Result;
                }

                done.Set();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        Assert.True(done.Wait(TimeSpan.FromSeconds(2)));
        if (error is not null)
        {
            throw error;
        }

        return result;
    }

    private static void AssertPayloadAbsent(object root, params byte[][] payloads)
    {
        var pending = new Stack<object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            if (current is byte[] bytes)
            {
                Assert.DoesNotContain(payloads, payload => bytes.AsSpan().SequenceEqual(payload));
                continue;
            }

            var type = current.GetType();
            if (type.IsPrimitive || type.IsEnum || current is string)
            {
                continue;
            }

            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic;
            foreach (var field in type.GetFields(flags))
            {
                var value = field.GetValue(current);
                if (value is null || value is string || field.FieldType.IsPrimitive || field.FieldType.IsEnum)
                {
                    continue;
                }

                if (value is byte[] buffer)
                {
                    Assert.DoesNotContain(payloads, payload => buffer.AsSpan().SequenceEqual(payload));
                    continue;
                }

                if (value is ReadOnlyMemory<byte> memory)
                {
                    Assert.DoesNotContain(payloads, payload => memory.Span.SequenceEqual(payload));
                    continue;
                }

                pending.Push(value);
            }
        }
    }

    private static void AssertNoArtData(Type type)
    {
        Assert.DoesNotContain(
            type.GetProperties(),
            static property => property.PropertyType == typeof(byte[])
                || property.PropertyType == typeof(ReadOnlyMemory<byte>)
                || property.PropertyType == typeof(Memory<byte>)
                || property.Name.Contains("ArtData", StringComparison.Ordinal));
    }

    private static List<ArticleCensusRow> Pages(ArticleIdentityCensus census)
    {
        var rows = new List<ArticleCensusRow>();
        for (var i = 0; i < census.PageCount; i++)
        {
            rows.AddRange(census.GetPage(i).Rows);
        }

        return rows;
    }

    private static ArticleCensusRow Row(IEnumerable<ArticleCensusRow> rows, ArticleId id)
    {
        foreach (var row in rows)
        {
            if (row.ArticleId == id)
            {
                return row;
            }
        }

        throw new InvalidOperationException("Census row was not found.");
    }

    private static ArticleRecord Commit(FileArticleIndex index, string messageId)
    {
        var record = CreateRecord(messageId);
        Assert.True(index.TryCommitPresent(new StoredArticleMetadata(
            record.ArtId,
            record.ArtHash,
            record.ArtSize,
            Location(record.ArtSize),
            ArticleStorageState.Present,
            Now,
            1UL)));
        return record;
    }

    private static StoredArticleLocation Location(int artSize) =>
        new(new SegmentId(1), 0, artSize);

    private static ArticleRecord CreateRecord(string messageId)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: storage\r\n\r\n");
        _ = builder.Append("body\r\n");
        var created = ArticleRecordFactory.TryCreate(
            new NntpArticleParser("peer.example"),
            Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted);
        return created.Record;
    }

    private sealed class TempControl : IDisposable
    {
        private TempControl(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public string Root { get; }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempControl Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-census-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            Directory.CreateDirectory(control);
            return new TempControl(
                root,
                new ArticleStorageRuntimeOptions(
                    control,
                    Path.Combine(root, "cache"),
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
