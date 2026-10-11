using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Segment copies are placed on the spool that accepted them, survive restart,
/// and a deleted segment file releases that spool's cap.
/// </summary>
public sealed class SegmentPoolEngineTests
{
    [Fact]
    public async Task Writes_move_to_the_next_spool_when_the_active_spool_is_full()
    {
        using var dir = PoolDir.Create(poolCount: 2);
        var first = CreateRecord("<pool-a@seg.test>");
        var second = CreateRecord("<pool-b@seg.test>");
        var length = SegmentRecordCodec.RecordLengthForArtSize(first.ArtSize);
        Assert.Equal(length, SegmentRecordCodec.RecordLengthForArtSize(second.ArtSize));
        var options = dir.OptionsFor(length);

        await using (var engine = Open(options))
        {
            Assert.Equal(2, engine.Segments.SegmentRoots.Count);
            await AcceptAsync(engine, first);
            await AcceptAsync(engine, second);
            Assert.Equal(length, FileBytes(dir.Pools[0]));
            Assert.Equal(length, FileBytes(dir.Pools[1]));

            var rejected = await engine.AcceptAsync(CreateRecord("<pool-c@seg.test>"), CancellationToken.None);
            Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, rejected.Outcome);
            Assert.Equal(SegmentPoolCapacity.HardLimitReason, rejected.Reason);
        }

        await using var restarted = Open(options);
        Assert.True(restarted.TryRead(first.ArtId, out var left));
        Assert.True(left.ArtData.Span.SequenceEqual(first.ArtData.Span));
        Assert.True(restarted.TryRead(second.ArtId, out var right));
        Assert.True(right.ArtData.Span.SequenceEqual(second.ArtData.Span));
        Assert.Equal(length, FileBytes(dir.Pools[0]));
        Assert.Equal(length, FileBytes(dir.Pools[1]));
    }

    [Fact]
    public async Task Reclaiming_a_full_spool_releases_its_capacity()
    {
        using var dir = PoolDir.Create(poolCount: 1);
        var first = CreateRecord("<pool-reclaim@seg.test>");
        var length = SegmentRecordCodec.RecordLengthForArtSize(first.ArtSize);
        var options = dir.OptionsFor(length);

        await using var engine = Open(options);
        await AcceptAsync(engine, first);
        var blocked = await engine.AcceptAsync(CreateRecord("<pool-blocked@seg.test>"), CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.RejectedCapacity, blocked.Outcome);
        Assert.Equal(SegmentPoolCapacity.HardLimitReason, blocked.Reason);

        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.True(engine.TryEvict(first.ArtId));
        Assert.True(engine.Index.TryGet(first.ArtId, out var row));
        engine.CompleteUnreferencedExtentAccounting();
        Assert.True(engine.Catalogue.TryGet(row.Location.SegmentId, out var info));
        var reclaimed = await engine.ReclaimFullyDeadClosedSegmentAsync(
            row.Location.SegmentId,
            info.Generation,
            CancellationToken.None);
        Assert.Equal(ArticleSegmentReclamationOutcome.Reclaimed, reclaimed.Outcome);
        Assert.Equal(0, FileBytes(dir.Pools[0]));

        var again = CreateRecord("<pool-after@seg.test>");
        await AcceptAsync(engine, again);
        Assert.Equal(SegmentRecordCodec.RecordLengthForArtSize(again.ArtSize), FileBytes(dir.Pools[0]));
        Assert.True(engine.TryRead(again.ArtId, out _));
    }

    private static FileArticleStorageEngine Open(ArticleStorageRuntimeOptions options) =>
        FileArticleStorageEngine.Open(options, logger: NullLogger.Instance);

    private static async Task AcceptAsync(FileArticleStorageEngine engine, ArticleRecord record)
    {
        engine.SuspendBackgroundPersist = true;
        var accepted = await engine.AcceptAsync(record, CancellationToken.None);
        Assert.Equal(ArticleAcceptOutcome.Accepted, accepted.Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
    }

    private static long FileBytes(string directory)
    {
        long sum = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "seg-*"))
        {
            sum += new FileInfo(file).Length;
        }

        return sum;
    }

    private static ArticleRecord CreateRecord(string messageId)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: pools\r\n\r\nbody\r\n");
        var created = ArticleRecordFactory.TryCreate(
            new NntpArticleParser("cache01.usenet.ninja"),
            Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class PoolDir : IDisposable
    {
        private PoolDir(string root, string control, string[] pools)
        {
            Root = root;
            Control = control;
            Pools = pools;
        }

        public string Root { get; }

        public string Control { get; }

        public string[] Pools { get; }

        public static PoolDir Create(int poolCount)
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-pools-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            Directory.CreateDirectory(control);
            var pools = new string[poolCount];
            for (var i = 0; i < poolCount; i++)
            {
                pools[i] = Path.Combine(root, "pool-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(pools[i]);
            }

            return new PoolDir(root, control, pools);
        }

        public ArticleStorageRuntimeOptions OptionsFor(long maxCapacity)
        {
            var runtimes = new SegmentPoolRuntime[Pools.Length];
            for (var i = 0; i < Pools.Length; i++)
            {
                runtimes[i] = new SegmentPoolRuntime(i, Pools[i], maxCapacity);
            }

            return new ArticleStorageRuntimeOptions(
                Control,
                Pools[0],
                ArticleStorageOptions.DefaultJournalSoftLimitBytes,
                ArticleStorageOptions.DefaultJournalHardLimitBytes,
                ArticleStorageOptions.DefaultSegmentTargetSizeBytes,
                SegmentPools: runtimes);
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
                // The engine disposes file handles before this runs. A leftover temp directory is not the assertion.
            }
        }
    }
}
