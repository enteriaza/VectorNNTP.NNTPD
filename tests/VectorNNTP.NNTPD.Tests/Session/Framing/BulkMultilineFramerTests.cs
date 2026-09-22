using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using VectorNNTP.NNTPD.MultilineFramerBench;
using VectorNNTP.NNTPD.MultilineFramerBench.Prototype;

namespace VectorNNTP.NNTPD.Tests.Session.Framing;

/// <summary>
/// Correctness tests for the benchmark-only <see cref="BulkMultilineFramer"/> prototype.
/// Does not exercise production <c>NntpMultilineDataReader</c> contracts beyond isolation.
/// </summary>
public sealed class BulkMultilineFramerTests
{
    [Fact]
    public void EmptyArticle()
    {
        var wire = ArticlePayloadFactory.EmptyArticle;
        var (status, payload) = ReadRecording(wire, segmentSize: 1);
        Assert.Equal(BulkMultilineReadStatus.Completed, status);
        Assert.Equal(0, payload.Length);
    }

    [Fact]
    public void FiveByteDelimiterIsDetected()
    {
        var wire = ArticlePayloadFactory.DataThenDelimiter();
        var (status, payload) = ReadRecording(wire, segmentSize: 64);
        Assert.Equal(BulkMultilineReadStatus.Completed, status);
        Assert.Equal("data\r\n", Encoding.ASCII.GetString(payload.Span));
    }

    [Fact]
    public void HelloDotIsNotTerminator()
    {
        var wire = ArticlePayloadFactory.WithTerminator("Hello.\r\nSomething else.\r\n");
        var (status, payload) = ReadRecording(wire, segmentSize: 8);
        Assert.Equal(BulkMultilineReadStatus.Completed, status);
        Assert.Equal("Hello.\r\nSomething else.\r\n", Encoding.ASCII.GetString(payload.Span));
    }

    [Fact]
    public void DotStuffedLineIsNotTerminator()
    {
        var wire = ArticlePayloadFactory.WithTerminator("..foo\r\nbar\r\n");
        var (status, payload) = ReadRecording(wire, segmentSize: 3);
        Assert.Equal(BulkMultilineReadStatus.Completed, status);
        // Wire representation preserved — no unstuffing.
        Assert.Equal("..foo\r\nbar\r\n", Encoding.ASCII.GetString(payload.Span));
    }

    [Fact]
    public void TerminatorAtEndOfSegment()
    {
        var wire = ArticlePayloadFactory.WithTerminator("abc\r\n");
        // Body in first segment(s); force terminator to start at a segment boundary.
        var body = wire.AsMemory()[..^3];
        var term = wire.AsMemory()[^3..];
        var seq = SegmentedSequenceFactory.CreateFromParts(body, term);
        var sink = new RecordingArticleSink();
        var result = BulkMultilineFramer.ReadArticleFromSequence(seq, sink, out _);
        Assert.Equal(BulkMultilineReadStatus.Completed, result.Status);
        Assert.Equal("abc\r\n", Encoding.ASCII.GetString(sink.Payload.Span));
    }

    [Fact]
    public void TerminatorSplitAcrossSegments()
    {
        var wire = ArticlePayloadFactory.WithTerminator("split-term\r\n");
        var seq = MultilineFramerBenchmarks.CreateTerminatorSplitSequence(wire);
        Assert.True(CountSegments(seq) >= 2);

        var sink = new RecordingArticleSink();
        var result = BulkMultilineFramer.ReadArticleFromSequence(seq, sink, out _);
        Assert.Equal(BulkMultilineReadStatus.Completed, result.Status);
        Assert.Equal("split-term\r\n", Encoding.ASCII.GetString(sink.Payload.Span));
    }

    [Fact]
    public async Task TerminatorSplitAcrossReads()
    {
        var wire = ArticlePayloadFactory.WithTerminator("across-reads\r\n");
        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 1024 * 1024,
            resumeWriterThreshold: 1024 * 1024,
            minimumSegmentSize: 8,
            useSynchronizationContext: false));

        // Write everything except the final `\n` of the five-byte delimiter, then the last byte.
        var first = wire.AsMemory()[..^1];
        var last = wire.AsMemory()[^1..];

        var readTask = Task.Run(async () =>
        {
            var sink = new RecordingArticleSink();
            return await BulkMultilineFramer.ReadArticleAsync(pipe.Reader, sink);
        });

        await pipe.Writer.WriteAsync(first);
        await pipe.Writer.FlushAsync();
        await Task.Yield();
        await pipe.Writer.WriteAsync(last);
        await pipe.Writer.FlushAsync();
        await pipe.Writer.CompleteAsync();

        var result = await readTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BulkMultilineReadStatus.Completed, result.Status);
        Assert.Equal(
            ArticlePayloadFactory.ExpectedPayloadLength(wire),
            result.PayloadBytes);
    }

    [Fact]
    public void MultiSegmentArticle()
    {
        var wire = ArticlePayloadFactory.SmallArticle();
        var expected = ArticlePayloadFactory.ExpectedPayload(wire);
        var (status, payload) = ReadRecording(wire, segmentSize: 17);
        Assert.Equal(BulkMultilineReadStatus.Completed, status);
        Assert.True(expected.AsSpan().SequenceEqual(payload.Span));
    }

    [Fact]
    public void PipelinedSecondArticleRemainsUnread()
    {
        var wire = ArticlePayloadFactory.PipelinedTwoArticles();
        var seq = SegmentedSequenceFactory.Create(wire, segmentSize: 7);
        var sink = new RecordingArticleSink();
        var result = BulkMultilineFramer.ReadArticleFromSequence(seq, sink, out var consumed);
        Assert.Equal(BulkMultilineReadStatus.Completed, result.Status);
        Assert.Equal("article-one\r\n", Encoding.ASCII.GetString(sink.Payload.Span));

        var remaining = seq.Slice(consumed);
        Assert.Equal(
            Encoding.ASCII.GetString(ArticlePayloadFactory.WithTerminator("article-two\r\n")),
            Encoding.ASCII.GetString(ToArray(remaining)));
    }

    [Fact]
    public void LargeArticle()
    {
        var wire = ArticlePayloadFactory.Large768KiBArticle();
        var expectedLen = ArticlePayloadFactory.ExpectedPayloadLength(wire);
        var sink = new CountingDiscardSink();
        var seq = SegmentedSequenceFactory.Create(wire, segmentSize: 64);
        var result = BulkMultilineFramer.ReadArticleFromSequence(seq, sink, out _);
        Assert.Equal(BulkMultilineReadStatus.Completed, result.Status);
        Assert.Equal(expectedLen, result.PayloadBytes);
        Assert.Equal(expectedLen, sink.Bytes);
    }

    [Fact]
    public void MultipleCRLFs()
    {
        var wire = ArticlePayloadFactory.ManyCrlfArticle();
        var expected = ArticlePayloadFactory.ExpectedPayload(wire);
        var (status, payload) = ReadRecording(wire, segmentSize: 11);
        Assert.Equal(BulkMultilineReadStatus.Completed, status);
        Assert.True(expected.AsSpan().SequenceEqual(payload.Span));
    }

    [Fact]
    public void MultipleDotStuffedLines()
    {
        var wire = ArticlePayloadFactory.ManyDotStuffedLinesArticle();
        var expected = ArticlePayloadFactory.ExpectedPayload(wire);
        var (status, payload) = ReadRecording(wire, segmentSize: 9);
        Assert.Equal(BulkMultilineReadStatus.Completed, status);
        Assert.Contains("..stuffed-line-0\r\n"u8, payload.Span);
        Assert.True(expected.AsSpan().SequenceEqual(payload.Span));
    }

    [Fact]
    public void TrailingPeriodsDoNotTerminate()
    {
        var wire = ArticlePayloadFactory.TrailingPeriodLinesArticle();
        var expected = ArticlePayloadFactory.ExpectedPayload(wire);
        var (status, payload) = ReadRecording(wire, segmentSize: 13);
        Assert.Equal(BulkMultilineReadStatus.Completed, status);
        Assert.True(expected.AsSpan().SequenceEqual(payload.Span));
        Assert.EndsWith("period.\r\n", Encoding.ASCII.GetString(payload.Span));
    }

    [Fact]
    public void TerminatorNotDeliveredToSink()
    {
        var wire = ArticlePayloadFactory.WithTerminator("keep-me\r\n");
        var (status, payload) = ReadRecording(wire, segmentSize: 2);
        Assert.Equal(BulkMultilineReadStatus.Completed, status);
        Assert.False(payload.Span.EndsWith(".\r\n"u8));
        Assert.Equal("keep-me\r\n", Encoding.ASCII.GetString(payload.Span));
    }

    [Fact]
    public async Task PipeReader_ChunkedFeed_MatchesExpected()
    {
        var wire = ArticlePayloadFactory.MediumArticle();
        var expected = ArticlePayloadFactory.ExpectedPayloadLength(wire);
        var pipe = new Pipe(new PipeOptions(
            minimumSegmentSize: 32,
            pauseWriterThreshold: 1024 * 1024,
            resumeWriterThreshold: 1024 * 1024,
            useSynchronizationContext: false));

        var readTask = Task.Run(async () =>
        {
            var sink = new CountingDiscardSink();
            var result = await BulkMultilineFramer.ReadArticleAsync(pipe.Reader, sink);
            return (result, sink.Bytes);
        });

        await SegmentedPipeFeed.WriteChunkedAsync(pipe.Writer, wire, chunkSize: 32);
        await pipe.Writer.CompleteAsync();

        var (result, bytes) = await readTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(BulkMultilineReadStatus.Completed, result.Status);
        Assert.Equal(expected, result.PayloadBytes);
        Assert.Equal(expected, bytes);
    }

    private static (BulkMultilineReadStatus Status, ReadOnlyMemory<byte> Payload) ReadRecording(
        byte[] wire,
        int segmentSize)
    {
        var seq = SegmentedSequenceFactory.Create(wire, segmentSize);
        Assert.True(wire.Length <= segmentSize || CountSegments(seq) > 1);
        var sink = new RecordingArticleSink();
        var result = BulkMultilineFramer.ReadArticleFromSequence(seq, sink, out _);
        return (result.Status, sink.Payload);
    }

    private static int CountSegments(ReadOnlySequence<byte> sequence)
    {
        var count = 0;
        foreach (var _ in sequence)
        {
            count++;
        }

        return count;
    }

    private static byte[] ToArray(ReadOnlySequence<byte> sequence)
    {
        var buffer = new byte[sequence.Length];
        sequence.CopyTo(buffer);
        return buffer;
    }
}
