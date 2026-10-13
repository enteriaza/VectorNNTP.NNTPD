using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.SpoolPersistence;
using VectorNNTP.StorageServer.Storage.SpoolShards;

namespace VectorNNTP.StorageServer.Tests.Storage;

public sealed class SpoolArticlePublisherTests
{
    [Fact]
    public void Publish_uses_the_canonical_article_id_and_hides_the_temp_file()
    {
        var files = new MemorySpoolArticleFiles();
        var publisher = new SpoolArticlePublisher(files);
        var id = ArticleId.FromMessageId("<persist@example.test>"u8);
        var body = "Path: cache01.usenet.ninja!peer\r\n\r\nbody\r\n"u8.ToArray();
        var root = OperatingSystem.IsWindows() ? @"C:\spool" : "/spool";

        var result = publisher.Publish(root, id, body, CancellationToken.None);

        Assert.Equal(SpoolPublishKind.Stored, result.Kind);
        Assert.Equal(SpoolArticlePath.CombineUnderRoot(root, SpoolArticlePath.RelativePath(id)), result.FinalPath);
        Assert.True(files.Exists(result.FinalPath));
        Assert.True(files.ContentsEqual(result.FinalPath, body));
        Assert.DoesNotContain(files.Paths, static path => path.Contains(".writing-", StringComparison.Ordinal));
        Assert.Equal(0, files.VisibleFinalDuringWrite);
    }

    [Fact]
    public void Duplicate_matching_bytes_are_kept_and_conflicting_bytes_are_not_replaced()
    {
        var files = new MemorySpoolArticleFiles();
        var publisher = new SpoolArticlePublisher(files);
        var id = ArticleId.FromMessageId("<dup@example.test>"u8);
        var root = OperatingSystem.IsWindows() ? @"C:\spool" : "/spool";
        var first = "alpha"u8.ToArray();
        var stored = publisher.Publish(root, id, first, CancellationToken.None);
        var duplicate = publisher.Publish(root, id, first, CancellationToken.None);
        var conflict = publisher.Publish(root, id, "beta"u8, CancellationToken.None);

        Assert.Equal(SpoolPublishKind.Duplicate, duplicate.Kind);
        Assert.Equal(SpoolPublishKind.Conflict, conflict.Kind);
        Assert.True(files.ContentsEqual(stored.FinalPath, first));
    }

    [Fact]
    public void Disk_full_and_access_denied_remove_the_temp_file()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\spool" : "/spool";
        var id = ArticleId.FromMessageId("<full@example.test>"u8);
        var full = new MemorySpoolArticleFiles { DiskFull = true };
        var denied = new MemorySpoolArticleFiles { AccessDenied = true };

        Assert.Equal(SpoolPublishKind.DiskFull, new SpoolArticlePublisher(full).Publish(root, id, "x"u8, CancellationToken.None).Kind);
        Assert.Equal(SpoolPublishKind.AccessDenied, new SpoolArticlePublisher(denied).Publish(root, id, "x"u8, CancellationToken.None).Kind);
        Assert.Empty(full.Paths);
        Assert.Empty(denied.Paths);
    }

    [Fact]
    public void Cancellation_removes_the_temp_file_and_a_later_publish_can_retry()
    {
        var files = new MemorySpoolArticleFiles { CancelDuringWrite = true };
        var publisher = new SpoolArticlePublisher(files);
        var id = ArticleId.FromMessageId("<cancel@example.test>"u8);
        var root = OperatingSystem.IsWindows() ? @"C:\spool" : "/spool";
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => publisher.Publish(root, id, "body"u8, cts.Token));
        Assert.Empty(files.Paths);

        files.CancelDuringWrite = false;
        var orphan = SpoolArticlePath.CombineUnderRoot(root, SpoolArticlePath.RelativePath(id)) + ".writing-stale";
        files.PlantTemp(SpoolArticlePath.CombineUnderRoot(root, SpoolArticlePath.RelativePath(id)));
        var stored = publisher.Publish(root, id, "body"u8, CancellationToken.None);
        Assert.Equal(SpoolPublishKind.Stored, stored.Kind);
        Assert.True(files.ContentsEqual(stored.FinalPath, "body"u8));
        Assert.True(files.Exists(orphan));
    }

    [Fact]
    public void Real_filesystem_publish_round_trips_and_rejects_a_conflict()
    {
        var root = Path.Combine(Path.GetTempPath(), "vectornntp-spool-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var files = new FileSpoolArticleFiles();
            var publisher = new SpoolArticlePublisher(files);
            var id = ArticleId.FromMessageId("<disk@example.test>"u8);
            var body = "Path: cache01.usenet.ninja!peer.example\r\n\r\nline\r\n"u8.ToArray();
            var stored = publisher.Publish(root, id, body, CancellationToken.None);
            var duplicate = publisher.Publish(root, id, body, CancellationToken.None);
            var conflict = publisher.Publish(root, id, "other"u8, CancellationToken.None);
            var onDisk = File.ReadAllBytes(stored.FinalPath);

            Assert.Equal(SpoolPublishKind.Stored, stored.Kind);
            Assert.Equal(SpoolPublishKind.Duplicate, duplicate.Kind);
            Assert.Equal(SpoolPublishKind.Conflict, conflict.Kind);
            Assert.Equal(body, onDisk);
            Assert.StartsWith(root, stored.FinalPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(stored.FinalPath)!, "*.writing-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Real_filesystem_recreates_a_leaf_removed_after_the_first_publish()
    {
        var root = Path.Combine(Path.GetTempPath(), "vectornntp-spool-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var files = new FileSpoolArticleFiles();
            var publisher = new SpoolArticlePublisher(files);
            var id = ArticleId.FromMessageId("<leaf@example.test>"u8);
            var body = "Path: cache01.usenet.ninja!peer.example\r\n\r\nline\r\n"u8.ToArray();
            var stored = publisher.Publish(root, id, body, CancellationToken.None);
            Directory.Delete(Path.GetDirectoryName(stored.FinalPath)!, recursive: true);

            var again = publisher.Publish(root, id, body, CancellationToken.None);
            Assert.Equal(SpoolPublishKind.Stored, again.Kind);
            Assert.Equal(body, File.ReadAllBytes(again.FinalPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Concurrent_writers_create_one_missing_leaf_without_losing_articles()
    {
        var root = Path.Combine(Path.GetTempPath(), "vectornntp-spool-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var files = new FileSpoolArticleFiles();
            var leaf = Path.Combine(root, "a", "b", "c");
            var body = "same-leaf"u8.ToArray();
            var tasks = new Task[8];
            for (var i = 0; i < tasks.Length; i++)
            {
                var name = Path.Combine(leaf, "article-" + i.ToString("00") + ".bin");
                tasks[i] = Task.Run(() =>
                {
                    files.CreateDirectory(leaf);
                    files.WriteAll(name, body, CancellationToken.None);
                });
            }

            await Task.WhenAll(tasks);
            Assert.Equal(8, Directory.GetFiles(leaf).Length);
            Directory.Delete(leaf, recursive: true);
            var retry = new Task[8];
            for (var i = 0; i < retry.Length; i++)
            {
                var name = Path.Combine(leaf, "retry-" + i.ToString("00") + ".bin");
                retry[i] = Task.Run(() =>
                {
                    files.CreateDirectory(leaf);
                    files.WriteAll(name, body, CancellationToken.None);
                });
            }

            await Task.WhenAll(retry);
            Assert.Equal(8, Directory.GetFiles(leaf).Length);
            foreach (var file in Directory.GetFiles(leaf))
            {
                Assert.Equal(body, File.ReadAllBytes(file));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Overlapping_same_id_writes_do_not_delete_each_others_temp_file()
    {
        var files = new MemorySpoolArticleFiles();
        var publisher = new SpoolArticlePublisher(files);
        var id = ArticleId.FromMessageId("<race@example.test>"u8);
        var root = OperatingSystem.IsWindows() ? @"C:\spool" : "/spool";
        var body = "same-body"u8.ToArray();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        files.OnWriteEntered = () =>
        {
            var count = Interlocked.Increment(ref entered);
            if (count == 1)
            {
                firstEntered.TrySetResult();
            }

            if (count == 2)
            {
                bothEntered.TrySetResult();
            }

            release.Task.GetAwaiter().GetResult();
        };

        var first = Task.Run(() => publisher.Publish(root, id, body, CancellationToken.None));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var firstTemp = files.Snapshot().Single(static path => path.Contains(".writing-", StringComparison.Ordinal));
        var second = Task.Run(() => publisher.Publish(root, id, body, CancellationToken.None));
        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(firstTemp, files.Snapshot());
        release.TrySetResult();

        var results = await Task.WhenAll(first, second);
        Assert.Equal(1, results.Count(static result => result.Kind == SpoolPublishKind.Stored));
        Assert.Equal(1, results.Count(static result => result.Kind == SpoolPublishKind.Duplicate));
        Assert.True(files.ContentsEqual(results.Single(static result => result.Kind == SpoolPublishKind.Stored).FinalPath, body));
        Assert.DoesNotContain(files.Snapshot(), static path => path.Contains(".writing-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Overlapping_conflicting_writes_keep_the_first_published_bytes()
    {
        var files = new MemorySpoolArticleFiles();
        var publisher = new SpoolArticlePublisher(files);
        var id = ArticleId.FromMessageId("<conflict-race@example.test>"u8);
        var root = OperatingSystem.IsWindows() ? @"C:\spool" : "/spool";
        var alpha = "alpha-body"u8.ToArray();
        var beta = "beta-body"u8.ToArray();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        files.OnWriteEntered = () =>
        {
            var count = Interlocked.Increment(ref entered);
            if (count == 1)
            {
                firstEntered.TrySetResult();
            }

            if (count == 2)
            {
                bothEntered.TrySetResult();
            }

            release.Task.GetAwaiter().GetResult();
        };

        var first = Task.Run(() => publisher.Publish(root, id, alpha, CancellationToken.None));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = Task.Run(() => publisher.Publish(root, id, beta, CancellationToken.None));
        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        release.TrySetResult();
        var results = await Task.WhenAll(first, second);
        var stored = results.Single(static result => result.Kind == SpoolPublishKind.Stored);
        Assert.Single(results, static result => result.Kind == SpoolPublishKind.Conflict);
        var storedBody = stored.FinalPath.Length > 0 && files.ContentsEqual(stored.FinalPath, alpha) ? alpha : beta;
        Assert.True(files.ContentsEqual(stored.FinalPath, storedBody));
        Assert.False(files.ContentsEqual(stored.FinalPath, storedBody == alpha ? beta : alpha));
    }
}

file sealed class MemorySpoolArticleFiles : ISpoolArticleFiles
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public bool DiskFull { get; set; }

    public bool AccessDenied { get; set; }

    public bool CancelDuringWrite { get; set; }

    public int VisibleFinalDuringWrite { get; private set; }

    public Action? OnWriteEntered { get; set; }

    public IReadOnlyCollection<string> Paths => Snapshot();

    public string[] Snapshot()
    {
        lock (_gate)
        {
            return _files.Keys.ToArray();
        }
    }

    public bool Exists(string path)
    {
        lock (_gate)
        {
            return _files.ContainsKey(path);
        }
    }

    public long Length(string path)
    {
        lock (_gate)
        {
            return _files[path].Length;
        }
    }

    public bool ContentsEqual(string path, ReadOnlySpan<byte> content)
    {
        lock (_gate)
        {
            return _files.TryGetValue(path, out var stored) && stored.AsSpan().SequenceEqual(content);
        }
    }

    public void CreateDirectory(string path)
    {
    }

    public void Delete(string path)
    {
        lock (_gate)
        {
            _files.Remove(path);
        }
    }

    public void WriteAll(string path, ReadOnlySpan<byte> content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CancelDuringWrite)
        {
            lock (_gate)
            {
                _files[path] = content.ToArray();
            }

            throw new OperationCanceledException(cancellationToken);
        }

        if (DiskFull)
        {
            lock (_gate)
            {
                _files[path] = content.ToArray();
            }

            throw new IOException("disk full") { HResult = unchecked((int)0x80070070) };
        }

        if (AccessDenied)
        {
            lock (_gate)
            {
                _files[path] = content.ToArray();
            }

            throw new UnauthorizedAccessException("denied");
        }

        lock (_gate)
        {
            var final = path.Contains(".writing-", StringComparison.Ordinal)
                ? path[..path.IndexOf(".writing-", StringComparison.Ordinal)]
                : path;
            if (_files.ContainsKey(final))
            {
                VisibleFinalDuringWrite++;
            }

            _files[path] = content.ToArray();
        }

        OnWriteEntered?.Invoke();
    }

    public void MoveNew(string source, string destination)
    {
        lock (_gate)
        {
            if (_files.ContainsKey(destination) || !_files.Remove(source, out var bytes))
            {
                throw new IOException("exists");
            }

            _files[destination] = bytes;
        }
    }

    public void PlantTemp(string finalPath)
    {
        lock (_gate)
        {
            _files[finalPath + ".writing-stale"] = [1, 2, 3];
        }
    }
}
