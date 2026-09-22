using System.Buffers;
using System.IO.Pipelines;
using VectorNNTP.NNTPD.MultilineFramerBench;
using VectorNNTP.NNTPD.MultilineFramerBench.Prototype;

namespace VectorNNTP.NNTPD.Tests.Session.Framing;

/// <summary>
/// Correctness tests for <see cref="SimdBulkMultilineFramer"/> — same contract as the bulk prototype.
/// </summary>
public sealed class SimdBulkMultilineFramerTests
{
    public static IEnumerable<object[]> AllFixtureNames()
    {
        foreach (var entry in InnArticleCorpus.LoadManifest().Files)
        {
            yield return [entry.RelativePath];
        }
    }

    public static IEnumerable<object[]> SegmentSizes()
    {
        foreach (var size in new[] { 1, 2, 4, 16, 4096 })
        {
            yield return [size];
        }
    }

    [Fact]
    public void EmptyArticle()
    {
        var (status, payload) = ReadRecording(ArticlePayloadFactory.EmptyArticle, 1);
        Assert.Equal(BulkMultilineReadStatus.Completed, status);
        Assert.Equal(0, payload.Length);
    }

    [Fact]
    public void FiveByteDelimiterIsDetected()
    {
        var (status, payload) = ReadRecording(ArticlePayloadFactory.DataThenDelimiter(), 64);
        Assert.Equal(BulkMultilineReadStatus.Completed, status);
        Assert.Equal("data\r\n"u8.ToArray(), payload.ToArray());
    }

    [Fact]
    public void HelloDotIsNotTerminator()
    {
        var wire = ArticlePayloadFactory.WithTerminator("Hello.\r\nSomething else.\r\n");
        var (status, payload) = ReadRecording(wire, 8);
        Assert.Equal(BulkMultilineReadStatus.Completed, status);
        Assert.Equal("Hello.\r\nSomething else.\r\n"u8.ToArray(), payload.ToArray());
    }

    [Fact]
    public void DotStuffedLineIsNotTerminator()
    {
        var wire = ArticlePayloadFactory.WithTerminator("..foo\r\nbar\r\n");
        var (status, payload) = ReadRecording(wire, 3);
        Assert.Equal(BulkMultilineReadStatus.Completed, status);
        Assert.Equal("..foo\r\nbar\r\n"u8.ToArray(), payload.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void DelimiterSplitAtEveryBoundary(int split)
    {
        var wire = ArticlePayloadFactory.WithTerminator("split-term\r\n");
        var expected = ArticlePayloadFactory.ExpectedPayload(wire);
        var prefix = wire.AsMemory()[..^5];
        var delim = wire.AsMemory()[^5..];
        ReadOnlySequence<byte> seq = split switch
        {
            0 => SegmentedSequenceFactory.CreateFromParts(prefix, delim),
            5 => new ReadOnlySequence<byte>(wire),
            _ => SegmentedSequenceFactory.CreateFromParts(prefix, delim[..split], delim[split..]),
        };

        var sink = new RecordingArticleSink();
        var result = SimdBulkMultilineFramer.ReadArticleFromSequence(seq, sink, out _);
        Assert.Equal(BulkMultilineReadStatus.Completed, result.Status);
        Assert.True(expected.AsSpan().SequenceEqual(sink.Payload.Span));
    }

    [Theory]
    [MemberData(nameof(SegmentSizes))]
    public void Wire7_IdenticalAcrossSegmentSizes(int segmentSize)
    {
        var wire = InnArticleCorpus.ReadAllBytes("wire-7");
        var expected = InnArticleCorpus.ExpectedWirePayload(wire);
        var sink = new RecordingArticleSink();
        var result = SimdBulkMultilineFramer.ReadArticleFromSequence(
            SegmentedSequenceFactory.Create(wire, segmentSize),
            sink,
            out _);
        Assert.Equal(BulkMultilineReadStatus.Completed, result.Status);
        Assert.True(expected.Span.SequenceEqual(sink.Payload.Span));
    }

    [Theory]
    [MemberData(nameof(AllFixtureNames))]
    public void EveryInnFixture_MatchesBulkSemantics(string relativePath)
    {
        var wire = InnArticleCorpus.ReadAllBytes(relativePath);
        var entry = InnArticleCorpus.LoadManifest().Files.Single(f => f.RelativePath == relativePath);

        var bulkSink = new RecordingArticleSink();
        var simdSink = new RecordingArticleSink();
        var seqBulk = SegmentedSequenceFactory.Create(wire, 7);
        var seqSimd = SegmentedSequenceFactory.Create(wire, 7);

        var bulk = BulkMultilineFramer.ReadArticleFromSequence(seqBulk, bulkSink, out var bulkConsumed);
        var simd = SimdBulkMultilineFramer.ReadArticleFromSequence(seqSimd, simdSink, out var simdConsumed);

        Assert.Equal(bulk.Status, simd.Status);
        Assert.Equal(bulk.PayloadBytes, simd.PayloadBytes);
        Assert.True(bulkSink.Payload.Span.SequenceEqual(simdSink.Payload.Span));
        Assert.Equal(GetOffset(seqBulk, bulkConsumed), GetOffset(seqSimd, simdConsumed));

        if (entry.FramingClassification == "complete_multiline_wire")
        {
            Assert.Equal(BulkMultilineReadStatus.Completed, simd.Status);
            Assert.True(InnArticleCorpus.ExpectedWirePayload(wire).Span.SequenceEqual(simdSink.Payload.Span));
        }
        else
        {
            Assert.Equal(BulkMultilineReadStatus.Incomplete, simd.Status);
            Assert.True(wire.AsSpan().SequenceEqual(simdSink.Payload.Span));
        }
    }

    [Fact]
    public void ManyDotsHostile_DoesNotFalseTerminate()
    {
        var wire = ArticlePayloadFactory.ManyDotsHostileArticle();
        var expected = ArticlePayloadFactory.ExpectedPayload(wire);
        Assert.True(expected.AsSpan().IndexOf("Hello.\r\n"u8) >= 0);

        var sink = new RecordingArticleSink();
        var result = SimdBulkMultilineFramer.ReadArticleFromSequence(
            SegmentedSequenceFactory.Create(wire, 17),
            sink,
            out _);
        Assert.Equal(BulkMultilineReadStatus.Completed, result.Status);
        Assert.True(expected.AsSpan().SequenceEqual(sink.Payload.Span));
    }

    [Fact]
    public void IndexOfFiveByteDelimiter_RejectsTrailingPeriodLines()
    {
        var content = "Hello.\r\nSomething else.\r\n"u8;
        Assert.Equal(-1, SimdBulkMultilineFramer.IndexOfFiveByteDelimiter(content));
        var withTerm = ArticlePayloadFactory.WithTerminator("Hello.\r\nSomething else.\r\n");
        Assert.Equal(withTerm.Length - 5, SimdBulkMultilineFramer.IndexOfFiveByteDelimiter(withTerm));
    }

    [Fact]
    public async Task DelimiterSplitAcrossReads()
    {
        var wire = ArticlePayloadFactory.WithTerminator("across-reads\r\n");
        var expected = ArticlePayloadFactory.ExpectedPayload(wire);
        var pipe = new Pipe(new PipeOptions(
            minimumSegmentSize: 8,
            pauseWriterThreshold: 1024 * 1024,
            resumeWriterThreshold: 1024 * 1024,
            useSynchronizationContext: false));

        var readTask = Task.Run(async () =>
        {
            var sink = new RecordingArticleSink();
            var result = await SimdBulkMultilineFramer.ReadArticleAsync(pipe.Reader, sink);
            return (result, sink.Payload.ToArray());
        });

        await pipe.Writer.WriteAsync(wire.AsMemory()[..^1]);
        await pipe.Writer.FlushAsync();
        await Task.Yield();
        await pipe.Writer.WriteAsync(wire.AsMemory()[^1..]);
        await pipe.Writer.FlushAsync();
        await pipe.Writer.CompleteAsync();

        var (result, payload) = await readTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BulkMultilineReadStatus.Completed, result.Status);
        Assert.True(expected.AsSpan().SequenceEqual(payload));
    }

    [Fact]
    public void PipelinedSecondArticleRemains()
    {
        var wire = ArticlePayloadFactory.PipelinedTwoArticles();
        var sink = new RecordingArticleSink();
        var seq = SegmentedSequenceFactory.Create(wire, 5);
        var result = SimdBulkMultilineFramer.ReadArticleFromSequence(seq, sink, out var consumed);
        Assert.Equal(BulkMultilineReadStatus.Completed, result.Status);
        Assert.Equal("article-one\r\n"u8.ToArray(), sink.Payload.ToArray());
        Assert.True(ArticlePayloadFactory.WithTerminator("article-two\r\n").AsSpan()
            .SequenceEqual(ToArray(seq.Slice(consumed))));
    }

    private static (BulkMultilineReadStatus Status, ReadOnlyMemory<byte> Payload) ReadRecording(
        byte[] wire,
        int segmentSize)
    {
        var sink = new RecordingArticleSink();
        var result = SimdBulkMultilineFramer.ReadArticleFromSequence(
            SegmentedSequenceFactory.Create(wire, segmentSize),
            sink,
            out _);
        return (result.Status, sink.Payload);
    }

    private static long GetOffset(ReadOnlySequence<byte> seq, SequencePosition pos) =>
        seq.Slice(seq.Start, pos).Length;

    private static byte[] ToArray(ReadOnlySequence<byte> sequence)
    {
        var buffer = new byte[sequence.Length];
        sequence.CopyTo(buffer);
        return buffer;
    }
}
