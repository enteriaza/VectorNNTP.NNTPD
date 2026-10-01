using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Proven-location scans snapshot segment identity under the write gate, then read
/// each file on a private stream. Candidates stay subject to the existing adoption checks.
/// </summary>
public sealed class ProvenLocationScanTests
{
    [Fact]
    public async Task Scan_does_not_block_a_read_of_another_segment()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var first = CreateRecord("<proven-read-a@seg.test>");
        var second = CreateRecord("<proven-read-b@seg.test>");
        var firstLocation = await AppendAndCloseAsync(store, first);
        var secondLocation = await AppendAndCloseAsync(store, second);

        var found = await RunWhileScanHoldsNoWriteGateAsync(store, first, () =>
        {
            Assert.True(store.TryRead(secondLocation, out var read));
            Assert.True(read.Span.SequenceEqual(second.ArtData.Span));
            return Task.CompletedTask;
        });

        var match = Assert.Single(found);
        Assert.Equal(firstLocation, match);
    }

    [Fact]
    public async Task Scan_does_not_block_an_active_append()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var closed = CreateRecord("<proven-append-closed@seg.test>");
        var appended = CreateRecord("<proven-append-active@seg.test>");
        _ = await AppendAndCloseAsync(store, closed);

        var found = await RunWhileScanHoldsNoWriteGateAsync(store, closed, async () =>
        {
            var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
            var location = await appender.AppendAsync(appended.ArtData, CancellationToken.None);
            Assert.True(store.TryRead(location, out var read));
            Assert.True(read.Span.SequenceEqual(appended.ArtData.Span));
        });

        Assert.Single(found);
        var again = store.FindProvenLocations(
            appended.ArtId,
            appended.ArtHash,
            appended.ArtSize,
            appended.ArtData.Span);
        Assert.Single(again);
    }

    [Fact]
    public async Task Active_scan_does_not_read_bytes_appended_after_the_snapshot()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var existing = CreateRecord("<proven-prefix@seg.test>");
        var appended = CreateRecord("<proven-after-snapshot@seg.test>");
        var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
        var existingLocation = await appender.AppendAsync(existing.ArtData, CancellationToken.None);

        var found = await RunWhileScanHoldsNoWriteGateAsync(store, appended, async () =>
        {
            var during = await store.GetActiveAppenderAsync(CancellationToken.None);
            _ = await during.AppendAsync(appended.ArtData, CancellationToken.None);
        });

        Assert.Empty(found);
        var after = store.FindProvenLocations(
            appended.ArtId,
            appended.ArtHash,
            appended.ArtSize,
            appended.ArtData.Span);
        Assert.Single(after);
        var stillThere = store.FindProvenLocations(
            existing.ArtId,
            existing.ArtHash,
            existing.ArtSize,
            existing.ArtData.Span);
        Assert.Equal(existingLocation, Assert.Single(stillThere));
    }

    [Fact]
    public async Task Retirement_during_scan_is_not_a_servable_proof()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var record = CreateRecord("<proven-retire@seg.test>");
        var location = await AppendAndCloseAsync(store, record);
        Assert.True(store.TryGetSegmentInfo(location.SegmentId, out var info));

        List<StoredArticleLocation>? found = null;
        try
        {
            found = await RunWhileScanHoldsNoWriteGateAsync(store, record, () =>
            {
                Assert.True(store.Catalogue.TryRetire(info.SegmentId, info.Generation, DateTimeOffset.UtcNow));
                Assert.True(store.TryReclaimRetired(info.SegmentId, out var reason), reason);
                return Task.CompletedTask;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            found = null;
        }

        Assert.False(store.TryGetSegmentInfo(location.SegmentId, out _));
        foreach (var candidate in found ?? [])
        {
            Assert.False(store.TryReadProven(
                candidate,
                record.ArtId,
                record.ArtHash,
                record.ArtSize,
                out _));
        }
    }

    [Fact]
    public async Task Corrupt_record_does_not_yield_an_adoptable_location()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var first = CreateRecord("<proven-corrupt@seg.test>");
        var second = CreateRecord("<proven-after-corrupt@seg.test>");
        var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
        var firstLocation = await appender.AppendAsync(first.ArtData, CancellationToken.None);
        _ = await appender.AppendAsync(second.ArtData, CancellationToken.None);
        await store.CloseActiveAsync(CancellationToken.None);

        var path = Path.Combine(
            dir.Options.SegmentDir,
            SegmentFileNames.Format(firstLocation.SegmentId, SegmentFileKind.Closed));
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            stream.Position = firstLocation.Length - 1;
            var value = stream.ReadByte();
            stream.Position = firstLocation.Length - 1;
            stream.WriteByte((byte)(value ^ 0xFF));
        }

        var found = store.FindProvenLocations(
            second.ArtId,
            second.ArtHash,
            second.ArtSize,
            second.ArtData.Span);
        Assert.Empty(found);
    }

    [Fact]
    public async Task Exact_payload_is_still_found_at_its_location()
    {
        using var dir = TempSegmentDir.Create();
        using var store = FileSegmentStore.Open(dir.Options);
        var record = CreateRecord("<proven-exact@seg.test>");
        var location = await AppendAndCloseAsync(store, record);

        var found = store.FindProvenLocations(
            record.ArtId,
            record.ArtHash,
            record.ArtSize,
            record.ArtData.Span);

        Assert.Equal(location, Assert.Single(found));
    }

    private static async Task<List<StoredArticleLocation>> RunWhileScanHoldsNoWriteGateAsync(
        FileSegmentStore store,
        ArticleRecord sought,
        Func<Task> duringScan)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<StoredArticleLocation>? found = null;
        store.TestHookDuringProvenLocationScan = () =>
        {
            entered.TrySetResult();
            if (!release.Task.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Proven-location scan kept the segment write gate.");
            }
        };

        var scan = Task.Run(() =>
        {
            try
            {
                found = store.FindProvenLocations(
                    sought.ArtId,
                    sought.ArtHash,
                    sought.ArtSize,
                    sought.ArtData.Span);
            }
            finally
            {
                store.TestHookDuringProvenLocationScan = null;
            }
        });

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await duringScan();
        }
        finally
        {
            release.TrySetResult();
            await scan.WaitAsync(TimeSpan.FromSeconds(5));
        }

        return found ?? [];
    }

    private static async Task<StoredArticleLocation> AppendAndCloseAsync(FileSegmentStore store, ArticleRecord record)
    {
        var appender = await store.GetActiveAppenderAsync(CancellationToken.None);
        var location = await appender.AppendAsync(record.ArtData, CancellationToken.None);
        await store.CloseActiveAsync(CancellationToken.None);
        return location;
    }

    private static ArticleRecord CreateRecord(string messageId)
    {
        var parser = new NntpArticleParser("cache01.usenet.ninja");
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: proven-location\r\n");
        _ = builder.Append("\r\nline1\r\nline2\r\n");
        var created = ArticleRecordFactory.TryCreate(parser, Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class TempSegmentDir : IDisposable
    {
        private TempSegmentDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempSegmentDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-proven-scan-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(cache);
            return new TempSegmentDir(
                root,
                new ArticleStorageRuntimeOptions(
                    ControlDir: control,
                    SegmentDir: cache,
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
            catch (UnauthorizedAccessException)
            {
            }
        }

        private string Root { get; }
    }
}
