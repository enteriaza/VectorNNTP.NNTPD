using System.Text;
using Microsoft.Data.Sqlite;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.ArticleIndex;
using VectorNNTP.StorageServer.Storage.SegmentPools;
using VectorNNTP.StorageServer.Storage.SpoolShards;

namespace VectorNNTP.StorageServer.Tests.Storage;

public sealed class ArticleIndexTests
{
    [Fact]
    public async Task Database_reopens_with_schema_version_2_and_page_size_8192()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var spool = ArticleIndexTestSupport.NewDirectory();
        try
        {
            var first = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
            Assert.Equal(2, first.SchemaVersion);
            Assert.Equal(8192, first.PageSize);
            Assert.Equal(0, first.PresenceCount);
            await ArticleIndexTestSupport.StopAsync(first);

            var second = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
            Assert.Equal(2, second.SchemaVersion);
            Assert.Equal(8192, second.PageSize);
            Assert.Equal(0, second.Counters.StartupRows);
            await ArticleIndexTestSupport.StopAsync(second);
        }
        finally
        {
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(spool);
        }
    }

    [Fact]
    public async Task Unknown_schema_version_fails_startup_without_publishing_membership()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var spool = ArticleIndexTestSupport.NewDirectory();
        try
        {
            var first = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
            var databasePath = first.DatabasePath;
            await ArticleIndexTestSupport.StopAsync(first);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
            }.ConnectionString))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = "UPDATE ArticleIndexMeta SET SchemaVersion = 99 WHERE Id = 1;";
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            var second = ArticleIndexTestSupport.Create(root, ArticleIndexTestSupport.Catalog(spool));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => second.StartAsync(CancellationToken.None));
            Assert.Contains("schema version 99", error.Message, StringComparison.Ordinal);
            Assert.False(second.IsReady);
            Assert.Throws<InvalidOperationException>(() => second.Contains(ArticleIndexTestSupport.Id(1)));
        }
        finally
        {
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(spool);
        }
    }

    [Fact]
    public async Task Hash_primary_key_must_be_32_bytes_and_timestamps_round_trip()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var spool = ArticleIndexTestSupport.NewDirectory();
        try
        {
            var index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
            const long received = 1_760_332_800_000;
            var id = ArticleIndexTestSupport.Id(7);
            var inserted = await index.InsertStoredAsync(id, spool, received, CancellationToken.None);
            Assert.True(inserted.Committed);
            Assert.True(inserted.Inserted);
            Assert.True(index.TryGetIndexedRecord(id, out var record));
            Assert.Equal(received, record.ReceivedAtUnixMilliseconds);
            Assert.Equal(received, record.LastAccessedAtUnixMilliseconds);
            Assert.Equal(spool, record.SpoolDirectory);
            Assert.Equal(id, record.ArticleId);

            var duplicate = await index.InsertStoredAsync(id, spool, received + 50, CancellationToken.None);
            Assert.True(duplicate.Committed);
            Assert.False(duplicate.Inserted);
            Assert.True(index.TryGetIndexedRecord(id, out var kept));
            Assert.Equal(received, kept.ReceivedAtUnixMilliseconds);
            Assert.Equal(spool, kept.SpoolDirectory);

            var databasePath = index.DatabasePath;
            await ArticleIndexTestSupport.StopAsync(index);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
            }.ConnectionString);
            await connection.OpenAsync();
            var reject = connection.CreateCommand();
            reject.CommandText =
                """
                INSERT INTO ArticleIndex (MessageIdHash, ReceivedAt, LastAccessedAt)
                VALUES (x'00', 1, 1);
                """;
            Assert.Throws<SqliteException>(() => reject.ExecuteNonQuery());
        }
        finally
        {
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(spool);
        }
    }

    [Fact]
    public async Task LastAccessedAt_index_returns_oldest_rows_and_a_threshold()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var spool = ArticleIndexTestSupport.NewDirectory();
        try
        {
            var index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
            await index.InsertStoredAsync(ArticleIndexTestSupport.Id(1), spool, 30, CancellationToken.None);
            await index.InsertStoredAsync(ArticleIndexTestSupport.Id(2), spool, 10, CancellationToken.None);
            await index.InsertStoredAsync(ArticleIndexTestSupport.Id(3), spool, 20, CancellationToken.None);

            var oldest = new ArticleIndexRecord[8];
            Assert.Equal(3, index.CopyOldest(spool, oldest));
            Assert.Equal(10, oldest[0].LastAccessedAtUnixMilliseconds);
            Assert.Equal(20, oldest[1].LastAccessedAtUnixMilliseconds);
            Assert.Equal(30, oldest[2].LastAccessedAtUnixMilliseconds);

            var bounded = new ArticleIndexRecord[8];
            Assert.Equal(2, index.CopyAccessedAtOrBefore(spool, 20, bounded));
            Assert.Equal(10, bounded[0].LastAccessedAtUnixMilliseconds);
            Assert.Equal(20, bounded[1].LastAccessedAtUnixMilliseconds);

            var databasePath = index.DatabasePath;
            await ArticleIndexTestSupport.StopAsync(index);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
            }.ConnectionString);
            await connection.OpenAsync();
            var plan = connection.CreateCommand();
            plan.CommandText =
                """
                EXPLAIN QUERY PLAN
                SELECT MessageIdHash FROM ArticleIndex
                WHERE LastAccessedAt <= 20
                ORDER BY LastAccessedAt
                LIMIT 10;
                """;
            var text = new StringBuilder();
            await using var reader = await plan.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                text.Append(reader.GetString(3));
            }

            Assert.Contains("IX_ArticleIndex_LastAccessedAt", text.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(spool);
        }
    }

    [Fact]
    public async Task Touch_never_changes_ReceivedAt_and_a_missing_row_is_not_inserted()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var spool = ArticleIndexTestSupport.NewDirectory();
        try
        {
            var index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
            var id = ArticleIndexTestSupport.Id(4);
            await index.InsertStoredAsync(id, spool, 100, CancellationToken.None);
            var older = await index.TouchAsync(id, spool, 100, CancellationToken.None);
            Assert.Equal(ArticleIndexTouchDisposition.Unchanged, older.Disposition);
            var newer = await index.TouchAsync(id, spool, 250, CancellationToken.None);
            Assert.Equal(ArticleIndexTouchDisposition.Updated, newer.Disposition);
            Assert.True(index.TryGetIndexedRecord(id, out var record));
            Assert.Equal(100, record.ReceivedAtUnixMilliseconds);
            Assert.Equal(250, record.LastAccessedAtUnixMilliseconds);

            var missing = ArticleIndexTestSupport.Id(5);
            var absent = await index.TouchAsync(missing, spool, 250, CancellationToken.None);
            Assert.Equal(ArticleIndexTouchDisposition.Missing, absent.Disposition);
            Assert.False(index.Contains(missing));
            Assert.False(index.TryGetIndexedRecord(missing, out _));
            await ArticleIndexTestSupport.StopAsync(index);
        }
        finally
        {
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(spool);
        }
    }

    [Fact]
    public async Task Several_producers_commit_once_per_identity_and_readers_can_run()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var spool = ArticleIndexTestSupport.NewDirectory();
        try
        {
            var index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
            var producers = Enumerable.Range(0, 8).Select(producer => Task.Run(async () =>
            {
                for (var item = 0; item < 25; item++)
                {
                    var id = ArticleIndexTestSupport.Id((producer * 25) + item);
                    ArticleIndexTestSupport.PlacePayload(spool, id);
                    var result = await index.InsertStoredAsync(id, spool, 1_000 + item, CancellationToken.None);
                    Assert.True(result.Inserted);
                    Assert.True(index.Contains(id));
                    Assert.True(index.TryGetRetrievableArticle(id, out _));
                    Assert.True(index.TryGetIndexedRecord(id, out var record));
                    Assert.Equal(spool, record.SpoolDirectory);
                }
            })).ToArray();
            await Task.WhenAll(producers);
            Assert.Equal(200, index.PresenceCount);
            Assert.Equal(0, index.Counters.Failures);

            var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(() =>
            {
                for (var item = 0; item < 200; item++)
                {
                    Assert.True(index.Contains(ArticleIndexTestSupport.Id(item)));
                    Assert.True(index.TryGetIndexedRecord(ArticleIndexTestSupport.Id((reader * 40) + (item % 25)), out _));
                }
            })).ToArray();
            await Task.WhenAll(readers);
            await ArticleIndexTestSupport.StopAsync(index);
        }
        finally
        {
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(spool);
        }
    }

    [Fact]
    public async Task A_full_batch_commits_together_and_a_short_batch_flushes_on_the_interval()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var spool = ArticleIndexTestSupport.NewDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        try
        {
            var index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool, new ArticleIndexSettings
            {
                BatchSize = 32,
                FlushIntervalMilliseconds = 15,
                QueueCapacity = 8,
                SummaryIntervalMilliseconds = 60_000,
            });
            await WaitAsync(() => index.Execution is not null);

            var held = await ArticleIndexTestSupport.StartSpoolAsync(root + "-held", spool, new ArticleIndexSettings
            {
                BatchSize = 2,
                FlushIntervalMilliseconds = 60_000,
                QueueCapacity = 8,
                SummaryIntervalMilliseconds = 60_000,
                BeforeReadAsync = async cancellationToken =>
                {
                    if (Interlocked.Increment(ref reads) == 1)
                    {
                        entered.TrySetResult();
                        await release.Task.WaitAsync(cancellationToken);
                    }
                },
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var first = held.InsertStoredAsync(ArticleIndexTestSupport.Id(1), spool, 10, CancellationToken.None).AsTask();
            var second = held.InsertStoredAsync(ArticleIndexTestSupport.Id(2), spool, 11, CancellationToken.None).AsTask();
            await WaitAsync(() => held.Counters.QueueDepth == 2);
            release.TrySetResult();
            await Task.WhenAll(first, second);
            Assert.Equal(1, held.Counters.Batches);
            Assert.Equal(2, held.PresenceCount);
            await ArticleIndexTestSupport.StopAsync(held);

            var timed = index.InsertStoredAsync(ArticleIndexTestSupport.Id(9), spool, 12, CancellationToken.None);
            await timed.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            ArticleIndexTestSupport.PlacePayload(spool, ArticleIndexTestSupport.Id(9));
            Assert.True(index.Contains(ArticleIndexTestSupport.Id(9)));
            Assert.True(index.TryGetRetrievableArticle(ArticleIndexTestSupport.Id(9), out _));
            await ArticleIndexTestSupport.StopAsync(index);
        }
        finally
        {
            release.TrySetResult();
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(root + "-held");
            ArticleIndexTestSupport.Delete(spool);
        }
    }

    [Fact]
    public async Task A_full_queue_applies_backpressure_until_the_writer_drains()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var spool = ArticleIndexTestSupport.NewDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        try
        {
            var index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool, new ArticleIndexSettings
            {
                BatchSize = 1,
                FlushIntervalMilliseconds = 60_000,
                QueueCapacity = 1,
                SummaryIntervalMilliseconds = 60_000,
                BeforeReadAsync = async cancellationToken =>
                {
                    if (Interlocked.Increment(ref reads) == 1)
                    {
                        entered.TrySetResult();
                        await release.Task.WaitAsync(cancellationToken);
                    }
                },
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var first = index.InsertStoredAsync(ArticleIndexTestSupport.Id(1), spool, 1, CancellationToken.None).AsTask();
            await WaitAsync(() => index.Counters.QueueDepth == 1);
            var second = index.InsertStoredAsync(ArticleIndexTestSupport.Id(2), spool, 2, CancellationToken.None).AsTask();
            await WaitAsync(() => index.Counters.BackpressureWaits >= 1);
            Assert.False(second.IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            ArticleIndexTestSupport.PlacePayload(spool, ArticleIndexTestSupport.Id(1));
            ArticleIndexTestSupport.PlacePayload(spool, ArticleIndexTestSupport.Id(2));
            Assert.True(index.Contains(ArticleIndexTestSupport.Id(1)));
            Assert.True(index.Contains(ArticleIndexTestSupport.Id(2)));
            Assert.True(index.TryGetRetrievableArticle(ArticleIndexTestSupport.Id(1), out _));
            await ArticleIndexTestSupport.StopAsync(index);
        }
        finally
        {
            release.TrySetResult();
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(spool);
        }
    }

    [Fact]
    public async Task A_failed_commit_rolls_back_retries_busy_errors_and_does_not_publish()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var spool = ArticleIndexTestSupport.NewDirectory();
        var attempts = 0;
        try
        {
            var failing = await ArticleIndexTestSupport.StartSpoolAsync(root, spool, new ArticleIndexSettings
            {
                BatchSize = 1,
                FlushIntervalMilliseconds = 10,
                QueueCapacity = 8,
                CommitAttempts = 2,
                SummaryIntervalMilliseconds = 60_000,
                BeforeCommit = () => throw new InvalidOperationException("commit rejected"),
            });
            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                failing.InsertStoredAsync(ArticleIndexTestSupport.Id(1), spool, 5, CancellationToken.None).AsTask());
            Assert.Equal("commit rejected", rejected.Message);
            Assert.False(failing.Contains(ArticleIndexTestSupport.Id(1)));
            Assert.Equal(1, failing.Counters.Failures);
            await ArticleIndexTestSupport.StopAsync(failing);

            var busy = await ArticleIndexTestSupport.StartSpoolAsync(root + "-busy", spool, new ArticleIndexSettings
            {
                BatchSize = 1,
                FlushIntervalMilliseconds = 10,
                QueueCapacity = 8,
                CommitAttempts = 3,
                SummaryIntervalMilliseconds = 60_000,
                BeforeCommit = () =>
                {
                    if (Interlocked.Increment(ref attempts) < 3)
                    {
                        throw new SqliteException("busy", 5);
                    }
                },
            });
            var inserted = await busy.InsertStoredAsync(ArticleIndexTestSupport.Id(2), spool, 6, CancellationToken.None);
            Assert.True(inserted.Inserted);
            ArticleIndexTestSupport.PlacePayload(spool, ArticleIndexTestSupport.Id(2));
            Assert.True(busy.Contains(ArticleIndexTestSupport.Id(2)));
            Assert.True(busy.TryGetRetrievableArticle(ArticleIndexTestSupport.Id(2), out _));
            Assert.True(busy.Counters.Retries >= 1);
            Assert.Equal(0, busy.Counters.Failures);
            await ArticleIndexTestSupport.StopAsync(busy);

            var reopened = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
            Assert.False(reopened.Contains(ArticleIndexTestSupport.Id(1)));
            await ArticleIndexTestSupport.StopAsync(reopened);
        }
        finally
        {
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(root + "-busy");
            ArticleIndexTestSupport.Delete(spool);
        }
    }

    [Fact]
    public async Task Shutdown_commits_queued_commands_and_startup_reloads_only_those_hashes()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var spool = ArticleIndexTestSupport.NewDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool, new ArticleIndexSettings
            {
                BatchSize = 8,
                FlushIntervalMilliseconds = 60_000,
                QueueCapacity = 8,
                SummaryIntervalMilliseconds = 60_000,
                BeforeReadAsync = async cancellationToken =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                },
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = new List<Task>();
            for (var i = 0; i < 3; i++)
            {
                pending.Add(index.InsertStoredAsync(ArticleIndexTestSupport.Id(i), spool, 40 + i, CancellationToken.None).AsTask());
            }

            await WaitAsync(() => index.Counters.QueueDepth == 3);
            var stopped = index.StopAsync(CancellationToken.None);
            release.TrySetResult();
            await stopped.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(pending);

            var reloaded = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
            Assert.Equal(3, reloaded.Counters.StartupRows);
            Assert.Equal(3, reloaded.PresenceCount);
            for (var i = 0; i < 3; i++)
            {
                Assert.True(reloaded.Contains(ArticleIndexTestSupport.Id(i)));
                Assert.False(reloaded.TryGetRetrievableArticle(ArticleIndexTestSupport.Id(i), out _));
                Assert.True(reloaded.TryGetIndexedRecord(ArticleIndexTestSupport.Id(i), out _));
            }

            Assert.False(reloaded.Contains(ArticleIndexTestSupport.Id(99)));
            Assert.True(reloaded.Counters.MembershipMisses >= 1);
            await ArticleIndexTestSupport.StopAsync(reloaded);
        }
        finally
        {
            release.TrySetResult();
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(spool);
        }
    }

    [Fact]
    public async Task Reconciliation_inserts_a_spooled_hash_and_counts_a_stale_row_without_deleting_it()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var spool = ArticleIndexTestSupport.NewDirectory();
        try
        {
            var planted = ArticleIndexTestSupport.Id(70);
            var path = SpoolArticlePath.CombineArticle(spool, planted);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "stored");
            var catalog = new SegmentPoolCatalog();
            catalog.Publish([new SegmentPoolDevice("block:test", [spool])]);
            var index = await ArticleIndexTestSupport.StartAsync(root, catalog);
            var report = await index.Reconciliation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, report.MissingQueued);
            Assert.True(index.Contains(planted));
            Assert.True(index.TryGetRetrievableArticle(planted, out var retrieved));
            Assert.Equal(path, retrieved);

            var stale = ArticleIndexTestSupport.Id(71);
            await index.InsertStoredAsync(stale, spool, 80, CancellationToken.None);
            await ArticleIndexTestSupport.StopAsync(index);
            var again = await ArticleIndexTestSupport.StartAsync(root, catalog);
            var second = await again.Reconciliation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(second.StaleRows >= 1);
            Assert.True(again.Contains(stale));
            Assert.False(again.TryGetRetrievableArticle(stale, out _));
            Assert.True(again.TryGetIndexedRecord(stale, out var staleRecord));
            Assert.Equal(80, staleRecord.ReceivedAtUnixMilliseconds);
            await ArticleIndexTestSupport.StopAsync(again);
        }
        finally
        {
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(spool);
        }
    }

    [Fact]
    public async Task A_failed_batch_fails_commands_still_queued_and_does_not_publish()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var spool = ArticleIndexTestSupport.NewDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        try
        {
            var index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool, new ArticleIndexSettings
            {
                BatchSize = 1,
                FlushIntervalMilliseconds = 60_000,
                QueueCapacity = 8,
                SummaryIntervalMilliseconds = 60_000,
                BeforeReadAsync = async cancellationToken =>
                {
                    if (Interlocked.Increment(ref reads) == 1)
                    {
                        entered.TrySetResult();
                        await release.Task.WaitAsync(cancellationToken);
                    }
                },
                BeforeCommit = () => throw new InvalidOperationException("rejected"),
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var first = index.InsertStoredAsync(ArticleIndexTestSupport.Id(1), spool, 1, CancellationToken.None).AsTask();
            var second = index.InsertStoredAsync(ArticleIndexTestSupport.Id(2), spool, 2, CancellationToken.None).AsTask();
            await WaitAsync(() => index.Counters.QueueDepth == 2);
            release.TrySetResult();
            var firstError = await Assert.ThrowsAsync<InvalidOperationException>(() => first);
            var secondError = await Assert.ThrowsAsync<InvalidOperationException>(() => second);
            Assert.Equal("rejected", firstError.Message);
            Assert.Equal("rejected", secondError.Message);
            Assert.False(index.Contains(ArticleIndexTestSupport.Id(1)));
            Assert.False(index.Contains(ArticleIndexTestSupport.Id(2)));
            await Assert.ThrowsAnyAsync<Exception>(() => index.Execution!);
            await ArticleIndexTestSupport.StopAsync(index);
        }
        finally
        {
            release.TrySetResult();
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(spool);
        }
    }

    [Fact]
    public async Task Cancelling_the_wait_does_not_drop_a_command_already_queued()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var spool = ArticleIndexTestSupport.NewDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        try
        {
            var index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool, new ArticleIndexSettings
            {
                BatchSize = 1,
                FlushIntervalMilliseconds = 60_000,
                QueueCapacity = 8,
                SummaryIntervalMilliseconds = 60_000,
                BeforeReadAsync = async cancellationToken =>
                {
                    if (Interlocked.Increment(ref reads) == 1)
                    {
                        entered.TrySetResult();
                        await release.Task.WaitAsync(cancellationToken);
                    }
                },
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancel = new CancellationTokenSource();
            var insert = index.InsertStoredAsync(ArticleIndexTestSupport.Id(3), spool, 9, cancel.Token).AsTask();
            await WaitAsync(() => index.Counters.QueueDepth == 1);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => insert);
            release.TrySetResult();
            await WaitAsync(() => index.TryGetIndexedRecord(ArticleIndexTestSupport.Id(3), out _));
            Assert.True(index.Contains(ArticleIndexTestSupport.Id(3)));
            Assert.False(index.TryGetRetrievableArticle(ArticleIndexTestSupport.Id(3), out _));
            await ArticleIndexTestSupport.StopAsync(index);
        }
        finally
        {
            release.TrySetResult();
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(spool);
        }
    }

    [Fact]
    public async Task A_queued_newer_access_is_excluded_from_eviction_until_it_commits()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var spool = ArticleIndexTestSupport.NewDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        ArticleIndexService? index = null;
        try
        {
            index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool, new ArticleIndexSettings
            {
                BatchSize = 1,
                FlushIntervalMilliseconds = 60_000,
                QueueCapacity = 8,
                SummaryIntervalMilliseconds = 60_000,
                BeforeReadAsync = async cancellationToken =>
                {
                    if (Interlocked.Increment(ref reads) == 2)
                    {
                        entered.TrySetResult();
                        await release.Task.WaitAsync(cancellationToken);
                    }
                },
            });
            await index.InsertStoredAsync(ArticleIndexTestSupport.Id(4), spool, 10, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var touch = index.TouchAsync(ArticleIndexTestSupport.Id(4), spool, 500, CancellationToken.None).AsTask();
            await WaitAsync(() => index.Counters.QueueDepth == 1);
            var oldest = new ArticleIndexRecord[1];
            Assert.Equal(0, index.CopyOldest(spool, oldest));
            release.TrySetResult();
            var updated = await touch.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ArticleIndexTouchDisposition.Updated, updated.Disposition);
            Assert.Equal(1, index.CopyOldest(spool, oldest));
            Assert.Equal(500, oldest[0].LastAccessedAtUnixMilliseconds);
            Assert.Equal(10, oldest[0].ReceivedAtUnixMilliseconds);
        }
        finally
        {
            release.TrySetResult();
            if (index is not null)
            {
                await ArticleIndexTestSupport.StopAsync(index);
            }

            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(spool);
        }
    }

    [Fact]
    public async Task Recovery_uses_file_time_and_does_not_store_zero_when_that_time_is_unusable()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var spool = ArticleIndexTestSupport.NewDirectory();
        try
        {
            var dated = ArticleIndexTestSupport.Id(80);
            var undated = ArticleIndexTestSupport.Id(81);
            var datedPath = SpoolArticlePath.CombineArticle(spool, dated);
            var undatedPath = SpoolArticlePath.CombineArticle(spool, undated);
            Directory.CreateDirectory(Path.GetDirectoryName(datedPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(undatedPath)!);
            await File.WriteAllTextAsync(datedPath, "dated");
            await File.WriteAllTextAsync(undatedPath, "undated");
            await File.WriteAllTextAsync(Path.Combine(spool, "not-an-article.txt"), "skip");
            var fileTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(datedPath, fileTime);
            File.SetLastWriteTimeUtc(undatedPath, new DateTime(1960, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var catalog = new SegmentPoolCatalog();
            catalog.Publish([new SegmentPoolDevice("block:test", [spool])]);
            var index = await ArticleIndexTestSupport.StartAsync(root, catalog);
            var report = await index.Reconciliation;
            Assert.Equal(2, report.FilesSeen);
            Assert.Equal(2, report.MissingQueued);
            Assert.Equal(1, report.ReceiptTimeFromClock);
            Assert.True(index.TryGetIndexedRecord(dated, out var datedRecord));
            Assert.Equal(new DateTimeOffset(fileTime).ToUnixTimeMilliseconds(), datedRecord.ReceivedAtUnixMilliseconds);
            Assert.True(index.TryGetIndexedRecord(undated, out var undatedRecord));
            Assert.True(undatedRecord.ReceivedAtUnixMilliseconds >= before);
            Assert.NotEqual(0, undatedRecord.ReceivedAtUnixMilliseconds);

            var kept = await index.InsertStoredAsync(dated, spool, before + 10_000, CancellationToken.None);
            Assert.False(kept.Inserted);
            Assert.True(index.TryGetIndexedRecord(dated, out var still));
            Assert.Equal(datedRecord.ReceivedAtUnixMilliseconds, still.ReceivedAtUnixMilliseconds);
            await ArticleIndexTestSupport.StopAsync(index);
        }
        finally
        {
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(spool);
        }
    }

    [Fact]
    public async Task Passive_checkpoints_run_during_inserts_and_the_database_reopens()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var spool = ArticleIndexTestSupport.NewDirectory();
        try
        {
            var index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool, new ArticleIndexSettings
            {
                BatchSize = 1,
                FlushIntervalMilliseconds = 1,
                QueueCapacity = 64,
                SummaryIntervalMilliseconds = 60_000,
            });
            for (var i = 0; i < 40; i++)
            {
                await index.InsertStoredAsync(ArticleIndexTestSupport.Id(i), spool, 1000 + i, CancellationToken.None);
            }

            Assert.True(index.Counters.Checkpoints >= 1);
            await ArticleIndexTestSupport.StopAsync(index);
            var reopened = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
            Assert.Equal(40, reopened.Counters.StartupRows);
            Assert.True(reopened.Contains(ArticleIndexTestSupport.Id(0)));
            Assert.True(reopened.TryGetIndexedRecord(ArticleIndexTestSupport.Id(0), out _));
            Assert.False(reopened.TryGetRetrievableArticle(ArticleIndexTestSupport.Id(0), out _));
            await ArticleIndexTestSupport.StopAsync(reopened);
        }
        finally
        {
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(spool);
        }
    }

    [Fact]
    public async Task Membership_growth_keeps_every_hash_visible_to_concurrent_lookups()
    {
        Assert.Equal(80, ArticlePresenceIndex.GrowCapacity(64));
        Assert.Equal(1250, ArticlePresenceIndex.GrowCapacity(1000));
        var presence = new ArticlePresenceIndex();
        var failures = 0;
        var published = new int[20_000];
        var writing = Task.Run(() => Parallel.For(0, published.Length, i =>
        {
            if (!presence.Add(ArticleIndexTestSupport.Id(i)))
            {
                Interlocked.Increment(ref failures);
            }

            Volatile.Write(ref published[i], 1);
        }));
        var reading = Task.Run(() => Parallel.For(0, 4, _ =>
        {
            for (var i = 0; i < published.Length; i++)
            {
                if (Volatile.Read(ref published[i]) == 1 && !presence.Contains(ArticleIndexTestSupport.Id(i)))
                {
                    Interlocked.Increment(ref failures);
                }
            }
        }));
        await Task.WhenAll(writing, reading);
        Assert.Equal(0, failures);
        Assert.Equal(20_000, presence.Count);
        Assert.True(presence.MaxShardCapacity > 16);
    }

    [Fact]
    public void Membership_uses_all_32_bytes_and_grows_one_shard_without_a_global_lock_lookup()
    {
        var presence = new ArticlePresenceIndex();
        var first = ArticleId.FromSpan(Enumerable.Repeat((byte)1, 32).ToArray());
        var differentTail = new byte[32];
        differentTail.AsSpan().Fill(1);
        differentTail[31] = 2;
        var second = ArticleId.FromSpan(differentTail);
        Assert.True(presence.Add(first));
        Assert.False(presence.Add(first));
        Assert.True(presence.Contains(first));
        Assert.False(presence.Contains(second));
        Assert.True(presence.Add(second));

        var before = presence.MaxShardCapacity;
        for (var i = 0; i < 4_096; i++)
        {
            Assert.True(presence.Add(ArticleIndexTestSupport.Id(i + 100)));
        }

        Assert.True(presence.MaxShardCapacity >= before);
        Assert.True(presence.EstimatedBytes > presence.Count * 32);
        Parallel.For(0, 4_096, i => Assert.True(presence.Contains(ArticleIndexTestSupport.Id(i + 100))));
    }

    [Fact]
    public async Task Each_spool_gets_a_stable_database_and_disabling_one_does_not_delete_it()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var left = ArticleIndexTestSupport.NewDirectory();
        var right = ArticleIndexTestSupport.NewDirectory();
        try
        {
            var index = await ArticleIndexTestSupport.StartAsync(root, ArticleIndexTestSupport.Catalog(left, right));
            await index.InsertStoredAsync(ArticleIndexTestSupport.Id(1), left, 10, CancellationToken.None);
            await index.InsertStoredAsync(ArticleIndexTestSupport.Id(1), right, 20, CancellationToken.None);
            var leftRows = new ArticleIndexRecord[1];
            var rightRows = new ArticleIndexRecord[1];
            Assert.Equal(1, index.CopyOldest(left, leftRows));
            Assert.Equal(1, index.CopyOldest(right, rightRows));
            Assert.Equal(10, leftRows[0].ReceivedAtUnixMilliseconds);
            Assert.Equal(20, rightRows[0].ReceivedAtUnixMilliseconds);
            Assert.NotEqual(index.DatabasePathFor(left), index.DatabasePathFor(right));
            var rightDatabase = index.DatabasePathFor(right);
            await ArticleIndexTestSupport.StopAsync(index);

            var reopened = await ArticleIndexTestSupport.StartAsync(root, ArticleIndexTestSupport.Catalog(left, right));
            Assert.Equal(rightDatabase, reopened.DatabasePathFor(right));
            Assert.Equal(1, reopened.CopyOldest(left, leftRows));
            Assert.Equal(10, leftRows[0].ReceivedAtUnixMilliseconds);
            await ArticleIndexTestSupport.StopAsync(reopened);

            var leftOnly = await ArticleIndexTestSupport.StartAsync(root, ArticleIndexTestSupport.Catalog(left));
            Assert.True(File.Exists(rightDatabase));
            Assert.True(leftOnly.TryGetIndexedRecord(ArticleIndexTestSupport.Id(1), out var kept));
            Assert.Equal(10, kept.ReceivedAtUnixMilliseconds);
            Assert.True(leftOnly.Contains(ArticleIndexTestSupport.Id(1)));
            Assert.False(leftOnly.TryGetRetrievableArticle(ArticleIndexTestSupport.Id(1), out _));
            await ArticleIndexTestSupport.StopAsync(leftOnly);
        }
        finally
        {
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(left);
            ArticleIndexTestSupport.Delete(right);
        }
    }

    [Fact]
    public async Task Duplicate_spool_identities_fail_startup_and_are_not_reassigned()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var left = ArticleIndexTestSupport.NewDirectory();
        var right = ArticleIndexTestSupport.NewDirectory();
        try
        {
            var first = await ArticleIndexTestSupport.StartSpoolAsync(root, left);
            await ArticleIndexTestSupport.StopAsync(first);
            File.Copy(
                Path.Combine(left, ArticleIndexSettings.SpoolIdentityFileName),
                Path.Combine(right, ArticleIndexSettings.SpoolIdentityFileName));
            var moved = ArticleIndexTestSupport.Create(root, ArticleIndexTestSupport.Catalog(right));
            var movedError = await Assert.ThrowsAsync<InvalidOperationException>(() => moved.StartAsync(CancellationToken.None));
            Assert.Contains("cannot be reassigned", movedError.Message, StringComparison.Ordinal);
            Assert.False(moved.IsReady);

            var conflict = ArticleIndexTestSupport.Create(root, ArticleIndexTestSupport.Catalog(left, right));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => conflict.StartAsync(CancellationToken.None));
            Assert.Contains("already uses", error.Message, StringComparison.Ordinal);
            Assert.False(conflict.IsReady);
        }
        finally
        {
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(left);
            ArticleIndexTestSupport.Delete(right);
        }
    }

    [Fact]
    public async Task An_in_flight_reservation_blocks_a_second_ingestion_until_it_is_released()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var spool = ArticleIndexTestSupport.NewDirectory();
        try
        {
            var index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
            var id = ArticleIndexTestSupport.Id(9);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            index.OnReservationWaiting = () => entered.TrySetResult();
            Assert.Equal(ArticleIngestionHold.Acquired, await index.ReserveIngestionAsync(id, CancellationToken.None));
            var waiting = index.ReserveIngestionAsync(id, CancellationToken.None).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(waiting.IsCompleted);
            index.ReleaseIngestion(id);
            Assert.Equal(ArticleIngestionHold.Acquired, await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
            await index.InsertStoredAsync(id, spool, 40, CancellationToken.None);
            index.ReleaseIngestion(id);
            Assert.Equal(ArticleIngestionHold.AlreadyPresent, await index.ReserveIngestionAsync(id, CancellationToken.None));
            await ArticleIndexTestSupport.StopAsync(index);
        }
        finally
        {
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(spool);
        }
    }

    [Fact]
    public async Task A_failed_spool_writer_does_not_block_another_spool()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var left = ArticleIndexTestSupport.NewDirectory();
        var right = ArticleIndexTestSupport.NewDirectory();
        try
        {
            var index = await ArticleIndexTestSupport.StartAsync(
                root,
                ArticleIndexTestSupport.Catalog(left, right),
                new ArticleIndexSettings
                {
                    BatchSize = 1,
                    FlushIntervalMilliseconds = 10,
                    QueueCapacity = 8,
                    SummaryIntervalMilliseconds = 60_000,
                    BeforeCommit = () => throw new InvalidOperationException("left spool unavailable"),
                    BeforeCommitSpool = left,
                });
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                index.InsertStoredAsync(ArticleIndexTestSupport.Id(1), left, 5, CancellationToken.None).AsTask());
            var stored = await index.InsertStoredAsync(ArticleIndexTestSupport.Id(2), right, 6, CancellationToken.None);
            Assert.True(stored.Inserted);
            Assert.False(index.Contains(ArticleIndexTestSupport.Id(1)));
            ArticleIndexTestSupport.PlacePayload(right, ArticleIndexTestSupport.Id(2));
            Assert.True(index.Contains(ArticleIndexTestSupport.Id(2)));
            Assert.True(index.TryGetRetrievableArticle(ArticleIndexTestSupport.Id(2), out var retrieved));
            Assert.Equal(SpoolArticlePath.CombineArticle(right, ArticleIndexTestSupport.Id(2)), retrieved);
            await ArticleIndexTestSupport.StopAsync(index);
        }
        finally
        {
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(left);
            ArticleIndexTestSupport.Delete(right);
        }
    }

    [Fact]
    public async Task A_legacy_single_database_is_migrated_without_deleting_it()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var spool = ArticleIndexTestSupport.NewDirectory();
        var id = ArticleIndexTestSupport.Id(42);
        var legacy = Path.Combine(root, ArticleIndexSettings.DatabaseFileName);
        var hash = new byte[32];
        id.CopyTo(hash);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = legacy,
            Pooling = false,
        }.ConnectionString))
        {
            await connection.OpenAsync();
            foreach (var sql in new[]
            {
                """
                CREATE TABLE ArticleIndexMeta (
                    Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
                    SchemaVersion INTEGER NOT NULL,
                    ArticleCount INTEGER NOT NULL
                ) WITHOUT ROWID;
                """,
                "INSERT INTO ArticleIndexMeta (Id, SchemaVersion, ArticleCount) VALUES (1, 1, 1);",
                """
                CREATE TABLE ArticleIndex (
                    MessageIdHash BLOB NOT NULL PRIMARY KEY,
                    ReceivedAt INTEGER NOT NULL,
                    LastAccessedAt INTEGER NOT NULL,
                    SpoolDirectory TEXT NOT NULL,
                    CHECK (length(MessageIdHash) = 32)
                ) WITHOUT ROWID;
                """,
            })
            {
                var statement = connection.CreateCommand();
                statement.CommandText = sql;
                statement.ExecuteNonQuery();
            }

            var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO ArticleIndex (MessageIdHash, ReceivedAt, LastAccessedAt, SpoolDirectory)
                VALUES ($hash, 111, 222, $spool);
                """;
            insert.Parameters.Add("$hash", SqliteType.Blob).Value = hash;
            insert.Parameters.Add("$spool", SqliteType.Text).Value = spool;
            insert.ExecuteNonQuery();
        }

        try
        {
            var index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
            Assert.True(index.TryGetIndexedRecord(id, out var record));
            Assert.Equal(111, record.ReceivedAtUnixMilliseconds);
            Assert.Equal(222, record.LastAccessedAtUnixMilliseconds);
            Assert.Equal(spool, record.SpoolDirectory);
            Assert.False(File.Exists(legacy));
            Assert.True(File.Exists(legacy + ".migrated"));
            await ArticleIndexTestSupport.StopAsync(index);

            var again = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
            Assert.True(again.TryGetIndexedRecord(id, out var kept));
            Assert.Equal(111, kept.ReceivedAtUnixMilliseconds);
            Assert.Equal(222, kept.LastAccessedAtUnixMilliseconds);
            await ArticleIndexTestSupport.StopAsync(again);
        }
        finally
        {
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(spool);
        }
    }

    [Fact]
    public void ArticleId_rejects_a_digest_that_is_not_32_bytes()
    {
        Assert.Throws<ArgumentException>(() => ArticleId.FromSpan(new byte[31]));
    }

    [Fact]
    public void Sixteen_and_twenty_two_spools_stay_inside_the_shared_cache_budget()
    {
        var one = ArticleIndexCacheBudget.ForSpoolCount(1);
        Assert.Equal(ArticleIndexSettings.AggregateWriterCacheKibibytes, one.WriterCacheKibibytes);
        Assert.Equal(ArticleIndexSettings.ReaderCount, one.ReaderCount);
        Assert.Equal(ArticleIndexSettings.AggregateReaderCacheKibibytes / ArticleIndexSettings.ReaderCount, one.ReaderCacheKibibytes);

        foreach (var count in new[] { 16, 22 })
        {
            var budget = ArticleIndexCacheBudget.ForSpoolCount(count);
            Assert.Equal(ArticleIndexSettings.SharedSpoolReaderCount, budget.ReaderCount);
            Assert.InRange(
                (long)budget.WriterCacheKibibytes * count,
                1,
                ArticleIndexSettings.AggregateWriterCacheKibibytes);
            Assert.InRange(
                (long)budget.ReaderCacheKibibytes * budget.ReaderCount * count,
                1,
                ArticleIndexSettings.AggregateReaderCacheKibibytes);
        }
    }

    [Fact]
    public async Task A_missing_copy_does_not_hide_the_other_spool_or_replace_its_receipt_time()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var left = ArticleIndexTestSupport.NewDirectory();
        var right = ArticleIndexTestSupport.NewDirectory();
        ArticleIndexService? index = null;
        try
        {
            var shared = ArticleIndexTestSupport.Id(11);
            var onlyLeft = ArticleIndexTestSupport.Id(12);
            index = await ArticleIndexTestSupport.StartAsync(root, ArticleIndexTestSupport.Catalog(left, right));
            await index.InsertStoredAsync(shared, left, 10, CancellationToken.None);
            await index.InsertStoredAsync(shared, right, 20, CancellationToken.None);
            await index.InsertStoredAsync(onlyLeft, left, 30, CancellationToken.None);
            await ArticleIndexTestSupport.StopAsync(index);

            var leftPath = ArticleIndexTestSupport.PlacePayload(left, shared, "left-copy");
            var rightPath = ArticleIndexTestSupport.PlacePayload(right, shared, "right-copy");
            var onlyLeftPath = ArticleIndexTestSupport.PlacePayload(left, onlyLeft, "left-only");
            index = await ArticleIndexTestSupport.StartAsync(root, ArticleIndexTestSupport.Catalog(left, right));
            Assert.True(index.TryGetRetrievableArticle(shared, out var firstPath));
            Assert.Equal(leftPath, firstPath);
            Assert.True(index.TryGetIndexedRecord(shared, out var original));
            Assert.Equal(10, original.ReceivedAtUnixMilliseconds);
            await ArticleIndexTestSupport.StopAsync(index);

            File.Delete(leftPath);
            index = await ArticleIndexTestSupport.StartAsync(root, ArticleIndexTestSupport.Catalog(left, right));
            Assert.True(index.Contains(shared));
            Assert.True(index.TryGetRetrievableArticle(shared, out var recovered));
            Assert.Equal(rightPath, recovered);
            Assert.Equal("right-copy", await File.ReadAllTextAsync(recovered));
            var leftRows = new ArticleIndexRecord[1];
            var rightRows = new ArticleIndexRecord[1];
            Assert.Equal(1, index.CopyOldest(left, leftRows));
            Assert.Equal(10, leftRows[0].ReceivedAtUnixMilliseconds);
            Assert.Equal(1, index.CopyOldest(right, rightRows));
            Assert.Equal(20, rightRows[0].ReceivedAtUnixMilliseconds);
            Assert.Equal(shared, rightRows[0].ArticleId);
            await ArticleIndexTestSupport.StopAsync(index);

            File.Delete(rightPath);
            index = await ArticleIndexTestSupport.StartAsync(root, ArticleIndexTestSupport.Catalog(right, left));
            Assert.True(index.Contains(shared));
            Assert.False(index.TryGetRetrievableArticle(shared, out _));
            Assert.True(index.Contains(onlyLeft));
            Assert.True(index.TryGetRetrievableArticle(onlyLeft, out var onlyPath));
            Assert.Equal(onlyLeftPath, onlyPath);
            Assert.Equal(1, index.CopyOldest(right, rightRows));
            Assert.Equal(20, rightRows[0].ReceivedAtUnixMilliseconds);
            Assert.Equal(shared, rightRows[0].ArticleId);
            await ArticleIndexTestSupport.StopAsync(index);
            index = null;
        }
        finally
        {
            if (index is not null)
            {
                await ArticleIndexTestSupport.StopAsync(index);
            }

            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(left);
            ArticleIndexTestSupport.Delete(right);
        }
    }

    [Fact]
    public async Task Reconciliation_ignores_invalid_names_and_keeps_a_named_file_without_hashing_its_bytes()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var spool = ArticleIndexTestSupport.NewDirectory();
        try
        {
            var stored = ArticleIndexTestSupport.Id(13);
            var ignored = ArticleIndexTestSupport.Id(99);
            var path = ArticleIndexTestSupport.PlacePayload(spool, stored, "not-the-digest");
            var leaf = Path.GetDirectoryName(path)!;
            await File.WriteAllTextAsync(Path.Combine(leaf, "not-a-hash"), "ignored");
            await File.WriteAllTextAsync(Path.Combine(leaf, ignored.ToLowerHexString().ToUpperInvariant()), "uppercase");
            await File.WriteAllTextAsync(path + ".writing-0123456789abcdef0123456789abcdef", "temp");
            await File.WriteAllTextAsync(Path.Combine(leaf, stored.ToLowerHexString()[..63]), "short");
            var index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
            var report = await index.Reconciliation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, report.FilesSeen);
            Assert.Equal(1, report.MissingQueued);
            Assert.True(index.Contains(stored));
            Assert.True(index.TryGetRetrievableArticle(stored, out var retrieved));
            Assert.Equal(path, retrieved);
            Assert.Equal("not-the-digest", await File.ReadAllTextAsync(retrieved));
            await ArticleIndexTestSupport.StopAsync(index);
        }
        finally
        {
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(spool);
        }
    }

    [Fact]
    public async Task A_missing_spool_directory_fails_startup_and_leaves_the_database_row()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var spool = ArticleIndexTestSupport.NewDirectory();
        try
        {
            var index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool);
            var id = ArticleIndexTestSupport.Id(14);
            await index.InsertStoredAsync(id, spool, 70, CancellationToken.None);
            var database = index.DatabasePath;
            await ArticleIndexTestSupport.StopAsync(index);
            Directory.Delete(spool, recursive: true);

            var restarted = ArticleIndexTestSupport.Create(root, ArticleIndexTestSupport.Catalog(spool));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.StartAsync(CancellationToken.None));
            Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
            Assert.False(restarted.IsReady);

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT ReceivedAt FROM ArticleIndex";
            Assert.Equal(70L, (long)command.ExecuteScalar()!);
        }
        finally
        {
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(spool);
        }
    }

    [Fact]
    public async Task One_spool_queue_does_not_stop_another_spool_from_committing()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var left = ArticleIndexTestSupport.NewDirectory();
        var right = ArticleIndexTestSupport.NewDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var index = await ArticleIndexTestSupport.StartAsync(
                root,
                ArticleIndexTestSupport.Catalog(left, right),
                new ArticleIndexSettings
                {
                    BatchSize = 1,
                    FlushIntervalMilliseconds = 60_000,
                    QueueCapacity = 1,
                    SummaryIntervalMilliseconds = 60_000,
                    BeforeCommitSpool = left,
                    BeforeCommit = () =>
                    {
                        entered.TrySetResult();
                        release.Task.GetAwaiter().GetResult();
                    },
                });
            var blocked = index.InsertStoredAsync(ArticleIndexTestSupport.Id(1), left, 1, CancellationToken.None).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stored = await index.InsertStoredAsync(ArticleIndexTestSupport.Id(2), right, 2, CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(blocked.IsCompleted);
            Assert.True(stored.Inserted);
            release.TrySetResult();
            Assert.True((await blocked.WaitAsync(TimeSpan.FromSeconds(5))).Inserted);
            await ArticleIndexTestSupport.StopAsync(index);
        }
        finally
        {
            release.TrySetResult();
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(left);
            ArticleIndexTestSupport.Delete(right);
        }
    }

    [Fact]
    public async Task A_non_busy_sqlite_error_is_not_retried_and_a_cancelled_waiter_does_not_drop_the_reservation()
    {
        var root = ArticleIndexTestSupport.NewDirectory();
        var spool = ArticleIndexTestSupport.NewDirectory();
        var attempts = 0;
        try
        {
            var index = await ArticleIndexTestSupport.StartSpoolAsync(root, spool, new ArticleIndexSettings
            {
                BatchSize = 1,
                FlushIntervalMilliseconds = 10,
                QueueCapacity = 8,
                CommitAttempts = 4,
                SummaryIntervalMilliseconds = 60_000,
                BeforeCommit = () =>
                {
                    Interlocked.Increment(ref attempts);
                    throw new SqliteException("constraint", 1);
                },
            });
            await Assert.ThrowsAsync<SqliteException>(() =>
                index.InsertStoredAsync(ArticleIndexTestSupport.Id(1), spool, 1, CancellationToken.None).AsTask());
            Assert.Equal(1, attempts);
            Assert.Equal(0, index.Counters.Retries);
            Assert.Equal(1, index.Counters.Failures);
            Assert.False(index.Contains(ArticleIndexTestSupport.Id(1)));
            await ArticleIndexTestSupport.StopAsync(index);

            var reservations = await ArticleIndexTestSupport.StartSpoolAsync(root + "-reserve", spool);
            var id = ArticleIndexTestSupport.Id(2);
            Assert.Equal(ArticleIngestionHold.Acquired, await reservations.ReserveIngestionAsync(id, CancellationToken.None));
            using var cancelled = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reservations.OnReservationWaiting = () => entered.TrySetResult();
            var waiting = reservations.ReserveIngestionAsync(id, cancelled.Token).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            var thirdEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reservations.OnReservationWaiting = () => thirdEntered.TrySetResult();
            var third = reservations.ReserveIngestionAsync(id, CancellationToken.None).AsTask();
            await thirdEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(third.IsCompleted);
            reservations.ReleaseIngestion(id);
            Assert.Equal(ArticleIngestionHold.Acquired, await third.WaitAsync(TimeSpan.FromSeconds(5)));
            reservations.ReleaseIngestion(id);
            await ArticleIndexTestSupport.StopAsync(reservations);
        }
        finally
        {
            ArticleIndexTestSupport.Delete(root);
            ArticleIndexTestSupport.Delete(root + "-reserve");
            ArticleIndexTestSupport.Delete(spool);
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = TimeProvider.System.GetTimestamp();
        while (!condition())
        {
            if (TimeProvider.System.GetElapsedTime(deadline) > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("condition was not met");
            }

            await Task.Delay(1);
        }
    }
}
