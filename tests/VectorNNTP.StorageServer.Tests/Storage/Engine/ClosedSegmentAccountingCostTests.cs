using System.Diagnostics;
using System.Text;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Storage.Engine;
using VectorNNTP.StorageServer.Storage.Engine.Durable;
using VectorNNTP.StorageServer.Storage.Engine.FileSegments;
using Xunit.Abstractions;

// ArticleStorageRuntimeOptions / ArticleStorageOptions: Configuration.

namespace VectorNNTP.StorageServer.Tests.Storage.Engine;

/// <summary>
/// Phase 34: deterministic closed-segment accounting cost and generation-race checks.
/// Large GiB scaling lives under <c>.artifacts/benchmarks/phase34-closed-accounting</c>.
/// </summary>
public sealed class ClosedSegmentAccountingCostTests
{
    private readonly ITestOutputHelper _output;

    public ClosedSegmentAccountingCostTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Accounting_allocates_approximately_one_record_buffer_per_proven_extent()
    {
        using var dir = TempStorageDir.Create();
        const int count = 64;
        const int body = 2048;
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await engine.AcceptAsync(Create($"<p34-alloc-{i}@seg.test>", body), CancellationToken.None)).Outcome);
        }

        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);
        Assert.False(engine.IsUnreferencedExtentAccountingComplete);

        var started = Stopwatch.StartNew();
        engine.CompleteUnreferencedExtentAccounting();
        started.Stop();

        Assert.True(engine.IsUnreferencedExtentAccountingComplete);
        Assert.True(engine.Catalogue.TryGet(engine.Catalogue.Snapshot().First(s => s.State == SegmentState.Closed).SegmentId, out var info));
        _ = info;
        var recordLength = SegmentRecordCodec.RecordLengthForArtSize(
            Create("<p34-size@seg.test>", body).ArtSize);
        _output.WriteLine(
            $"articles={count} recordLength={recordLength} wallMs={started.Elapsed.TotalMilliseconds:0.0}");

        // Allocation ratio is measured by the Phase 34/35 Release harness (process-exclusive).
        // GC.GetTotalAllocatedBytes is process-wide and is not reliable under parallel xUnit.
        var second = Stopwatch.StartNew();
        engine.CompleteUnreferencedExtentAccounting();
        second.Stop();
        Assert.True(second.Elapsed < started.Elapsed + TimeSpan.FromMilliseconds(25));
    }

    [Fact]
    public async Task Structural_generation_change_during_commit_converges_on_retry()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        var first = Create("<p34-gen-a@seg.test>", 512);
        var second = Create("<p34-gen-b@seg.test>", 512);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(first, CancellationToken.None)).Outcome);
        Assert.Equal(ArticleAcceptOutcome.Accepted, (await engine.AcceptAsync(second, CancellationToken.None)).Outcome);
        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);

        var flipped = 0;
        engine.TestHookDuringClosedAccountingCommit = () =>
        {
            if (Interlocked.Exchange(ref flipped, 1) != 0)
            {
                return;
            }

            Assert.True(engine.TryInvalidate(first.ArtId));
        };

        engine.CompleteUnreferencedExtentAccounting();
        engine.TestHookDuringClosedAccountingCommit = null;
        if (!engine.IsUnreferencedExtentAccountingComplete)
        {
            engine.CompleteUnreferencedExtentAccounting();
        }

        Assert.True(engine.IsUnreferencedExtentAccountingComplete);
        Assert.True(engine.Index.TryGet(first.ArtId, out var row));
        Assert.Equal(ArticleStorageState.Invalid, row.State);
        Assert.True(engine.TryRead(second.ArtId, out _));
        Assert.False(engine.TryRead(first.ArtId, out _));
    }

    [Fact]
    public async Task Second_accounting_pass_is_cheap_once_segments_are_marked_complete()
    {
        using var dir = TempStorageDir.Create();
        await using var engine = FileArticleStorageEngine.Open(dir.Options);
        for (var i = 0; i < 32; i++)
        {
            Assert.Equal(
                ArticleAcceptOutcome.Accepted,
                (await engine.AcceptAsync(Create($"<p34-second-{i}@seg.test>", 1024), CancellationToken.None)).Outcome);
        }

        await engine.DrainPendingAsync(CancellationToken.None);
        await engine.Segments.CloseActiveAsync(CancellationToken.None);

        var first = Stopwatch.StartNew();
        engine.CompleteUnreferencedExtentAccounting();
        first.Stop();
        var second = Stopwatch.StartNew();
        engine.CompleteUnreferencedExtentAccounting();
        second.Stop();
        _output.WriteLine($"firstMs={first.Elapsed.TotalMilliseconds:0.0} secondMs={second.Elapsed.TotalMilliseconds:0.0}");
        Assert.True(engine.IsUnreferencedExtentAccountingComplete);
        Assert.True(second.Elapsed < first.Elapsed + TimeSpan.FromMilliseconds(5));
    }

    private static ArticleRecord Create(string messageId, int bodyLength)
    {
        var builder = new StringBuilder();
        _ = builder.Append("Path: peer.example\r\n");
        _ = builder.Append("Date: Fri, 23 Aug 2024 07:30:10 +0000\r\n");
        _ = builder.Append("Message-ID: ").Append(messageId).Append("\r\n");
        _ = builder.Append("Newsgroups: alt.test\r\n");
        _ = builder.Append("From: user@example.test\r\n");
        _ = builder.Append("Subject: phase34\r\n\r\n");
        var remaining = bodyLength;
        while (remaining > 0)
        {
            var take = Math.Min(64, remaining);
            _ = builder.Append(new string('x', take)).Append("\r\n");
            remaining -= take;
        }

        var created = ArticleRecordFactory.TryCreate(
            new NntpArticleParser("cache01.usenet.ninja"),
            Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.True(created.IsAccepted, created.ParseFailure.ToString());
        return created.Record;
    }

    private sealed class TempStorageDir : IDisposable
    {
        private TempStorageDir(string root, ArticleStorageRuntimeOptions options)
        {
            Root = root;
            Options = options;
        }

        public ArticleStorageRuntimeOptions Options { get; }

        public static TempStorageDir Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "vectornntp-phase34-" + Guid.NewGuid().ToString("N"));
            var control = Path.Combine(root, "control");
            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(control);
            Directory.CreateDirectory(cache);
            return new TempStorageDir(
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
        }

        private string Root { get; }
    }
}
