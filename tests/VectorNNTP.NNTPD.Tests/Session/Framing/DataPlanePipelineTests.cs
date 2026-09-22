using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using VectorNNTP.NNTPD.MultilineFramerBench.Corpus;
using VectorNNTP.NNTPD.MultilineFramerBench.DataPlane;
using Xunit;

namespace VectorNNTP.NNTPD.Tests.Session.Framing;

public sealed class DataPlanePipelineTests
{
    [Fact]
    public async Task Pipeline_Synthetic_CheckQuitAndPayloadCommands()
    {
        var aStored = Encoding.ASCII.GetBytes("CHECK ignored\r\nQUIT ignored\r\nTAKETHIS ignored\r\n");
        var cStored = Encoding.ASCII.GetBytes("hello\r\n");
        using var ms = new MemoryStream();
        WriteTxn(ms, "<a@test>", aStored);
        ms.Write("CHECK <b@test>\r\n"u8);
        WriteTxn(ms, "<c@test>", cStored);
        ms.Write("QUIT\r\n"u8);
        // Trailing garbage after QUIT must not be parsed as an article.
        ms.Write("TAKETHIS <after@quit>\r\nbody\r\n.\r\n"u8);

        var sink = new CountOnlySink();
        var responses = new OrderedResponseCollector();
        var result = await DataPlanePipeline.RunFromWireAsync(
            ms.ToArray(),
            sink,
            "CountOnly",
            new PipelineRunOptions { Mode = PipelineParserMode.ContinuousBulk, MinimumSegmentSize = 256 },
            responses,
            expectedArticles: 2);

        Assert.True(result.Ok);
        Assert.Equal(2, result.ArticlesParsed);
        Assert.Equal(1, result.Checks);
        Assert.Equal(1, result.Quits);
        Assert.Equal(2, responses.Count);
        Assert.Equal("<a@test>", responses.Snapshot()[0].MessageId);
        Assert.Equal("<c@test>", responses.Snapshot()[1].MessageId);
    }

    [Fact]
    public async Task Pipeline_ControlPlane_CheckAndQuit()
    {
        var aStored = Encoding.ASCII.GetBytes("CHECK ignored\r\nQUIT ignored\r\nTAKETHIS ignored\r\n");
        var cStored = Encoding.ASCII.GetBytes("hello\r\n");
        var entries = await BuildTempCorpusAsync(
        [
            ("<a@test>", aStored),
            ("<c@test>", cStored),
        ]);

        var sink = new CountOnlySink();
        var responses = new OrderedResponseCollector();
        var result = await DataPlanePipeline.RunAsync(
            entries,
            sink,
            "CountOnly",
            new PipelineRunOptions { Mode = PipelineParserMode.ContinuousBulk, MinimumSegmentSize = 4096 },
            responses);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, result.ArticlesParsed);
        Assert.True(responses.IsStrictlyOrdered());
    }

    [Fact]
    public async Task Pipeline_CommandsInsideArticle_ArePayload()
    {
        var stored = Encoding.ASCII.GetBytes("line\r\nCHECK x\r\nQUIT\r\nTAKETHIS y\r\n");
        var entries = await BuildTempCorpusAsync([("<in@body>", stored)]);
        var sink = new CountOnlySink();
        var responses = new OrderedResponseCollector();
        var result = await DataPlanePipeline.RunAsync(
            entries,
            sink,
            "CountOnly",
            new PipelineRunOptions { Mode = PipelineParserMode.ContinuousSimd, MinimumSegmentSize = 1024 },
            responses);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(0, result.Checks);
        Assert.Equal(0, result.Quits);
        Assert.Equal(1, result.ArticlesParsed);
    }

    [Fact]
    public async Task Pipeline_ResponseOrdering_Preserved()
    {
        var entries = await BuildTempCorpusAsync(
        [
            ("<1@t>", "a\r\n"u8.ToArray()),
            ("<2@t>", "b\r\n"u8.ToArray()),
            ("<3@t>", "c\r\n"u8.ToArray()),
        ]);
        var sink = new ControlledBackpressureSink(ControlledSinkSpeed.Fast);
        var responses = new OrderedResponseCollector();
        var result = await DataPlanePipeline.RunAsync(
            entries,
            sink,
            "Fast",
            new PipelineRunOptions { Mode = PipelineParserMode.ContinuousBulk },
            responses);
        Assert.True(result.Ok, result.Error);
        var snap = responses.Snapshot();
        Assert.Equal(3, snap.Count);
        Assert.Equal("<1@t>", snap[0].MessageId);
        Assert.Equal("<2@t>", snap[1].MessageId);
        Assert.Equal("<3@t>", snap[2].MessageId);
        Assert.Equal(1, snap[0].SequenceNumber);
        Assert.Equal(2, snap[1].SequenceNumber);
        Assert.Equal(3, snap[2].SequenceNumber);
    }

    [Fact]
    public async Task Pipeline_RoundTrip_StoredEqualsDestuffedCurrent()
    {
        var stored = Encoding.ASCII.GetBytes(".leading\r\n..two\r\nnormal\r\n");
        var entries = await BuildTempCorpusAsync([("<rt@test>", stored)]);
        var sink = new CountOnlySink();
        var result = await DataPlanePipeline.RunAsync(
            entries,
            sink,
            "CountOnly",
            new PipelineRunOptions { Mode = PipelineParserMode.CurrentMaterializing });
        Assert.True(result.Ok, result.Error);
        // Current payload is destuffed; should match stored length.
        Assert.Equal(stored.Length, result.PayloadBytes);
    }

    private static async Task<IReadOnlyList<ReplayArticleEntry>> BuildTempCorpusAsync(
        (string Mid, byte[] Stored)[] articles)
    {
        var root = Path.Combine(Path.GetTempPath(), "VectorNNTP.PipelineTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var list = new List<ReplayArticleEntry>();
        foreach (var (mid, stored) in articles)
        {
            var digest = MessageIdDigest.ComputeHexLower(mid);
            var dir = Path.Combine(root, digest[..2], digest.Substring(2, 2));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, digest);
            await File.WriteAllBytesAsync(path, stored);
            list.Add(new ReplayArticleEntry(mid, path, Path.GetRelativePath(root, path), stored.Length, digest));
        }

        return list;
    }

    private static void WriteTxn(Stream s, string mid, byte[] stored)
    {
        var txn = ArticleWireReconstructor.BuildTakeThisTransaction(mid, stored);
        s.Write(txn);
    }
}
