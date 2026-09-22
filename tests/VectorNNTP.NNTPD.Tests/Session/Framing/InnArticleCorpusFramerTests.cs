using System.Buffers;
using System.IO.Pipelines;
using VectorNNTP.NNTPD.MultilineFramerBench.Prototype;
using VectorNNTP.NNTPD.Session.Framing;

namespace VectorNNTP.NNTPD.Tests.Session.Framing;

/// <summary>
/// Correctness coverage for the INN <c>tests/data/articles</c> corpus against the bulk framer prototype.
/// </summary>
public sealed class InnArticleCorpusFramerTests
{
    public static IEnumerable<object[]> AllFixtureNames()
    {
        foreach (var entry in InnArticleCorpus.LoadManifest().Files)
        {
            yield return [entry.RelativePath];
        }
    }

    public static IEnumerable<object[]> CompleteWireFixtureNames()
    {
        foreach (var entry in InnArticleCorpus.CompleteWireArticles())
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
    public void Manifest_MatchesPinnedCommit_AndFixtureCount()
    {
        var manifest = InnArticleCorpus.LoadManifest();
        Assert.Equal(InnArticleCorpus.UpstreamCommit, manifest.UpstreamCommit);
        Assert.Equal("InterNetNews/inn", manifest.Repository);
        Assert.Equal("tests/data/articles", manifest.SourcePath);
        Assert.Equal(25, manifest.FileCount);
        Assert.Equal(25, manifest.Files.Count);
        Assert.Equal(19554, manifest.TotalBytes);
    }

    [Theory]
    [MemberData(nameof(AllFixtureNames))]
    public void EveryFixture_Sha256MatchesManifest(string relativePath)
    {
        var entry = InnArticleCorpus.LoadManifest().Files.Single(f => f.RelativePath == relativePath);
        InnArticleCorpus.VerifySha256(entry);
    }

    [Theory]
    [MemberData(nameof(AllFixtureNames))]
    public void EveryFixture_BulkFramer_StatusMatchesClassification(string relativePath)
    {
        var entry = InnArticleCorpus.LoadManifest().Files.Single(f => f.RelativePath == relativePath);
        var wire = InnArticleCorpus.ReadAllBytes(relativePath);
        var sink = new RecordingArticleSink();
        var seq = SegmentedSequenceFactory.Create(wire, segmentSize: 7);
        var result = BulkMultilineFramer.ReadArticleFromSequence(seq, sink, out var consumed);

        if (entry.FramingClassification == "complete_multiline_wire")
        {
            Assert.Equal(BulkMultilineReadStatus.Completed, result.Status);
            var expected = InnArticleCorpus.ExpectedWirePayload(wire);
            Assert.True(expected.Span.SequenceEqual(sink.Payload.Span));
            Assert.Equal(expected.Length, result.PayloadBytes);
            Assert.True(seq.Slice(consumed).IsEmpty);
            Assert.Equal(wire.Length - 3, sink.Payload.Length);
        }
        else
        {
            Assert.Equal(BulkMultilineReadStatus.Incomplete, result.Status);
            // Without a terminator, the prototype forwards the entire examined buffer.
            Assert.True(wire.AsSpan().SequenceEqual(sink.Payload.Span));
        }
    }

    [Theory]
    [MemberData(nameof(CompleteWireFixtureNames))]
    public void CompleteWire_PreservesDotStuffing_AndExcludesTerminator(string relativePath)
    {
        var wire = InnArticleCorpus.ReadAllBytes(relativePath);
        Assert.True(InnArticleCorpus.ContainsFiveByteDelimiter(wire));
        var expected = InnArticleCorpus.ExpectedWirePayload(wire);
        Assert.Contains("..\r\n"u8, expected.Span);

        var sink = new RecordingArticleSink();
        var result = BulkMultilineFramer.ReadArticleFromSequence(
            SegmentedSequenceFactory.Create(wire, 3),
            sink,
            out _);
        Assert.Equal(BulkMultilineReadStatus.Completed, result.Status);
        Assert.True(expected.Span.SequenceEqual(sink.Payload.Span));
        Assert.True(sink.Payload.Span.StartsWith(".."u8));
    }

    [Theory]
    [MemberData(nameof(SegmentSizes))]
    public void Wire7_IdenticalPayload_AcrossSegmentSizes(int segmentSize)
    {
        var wire = InnArticleCorpus.ReadAllBytes("wire-7");
        var expected = InnArticleCorpus.ExpectedWirePayload(wire);
        var sink = new RecordingArticleSink();
        var result = BulkMultilineFramer.ReadArticleFromSequence(
            SegmentedSequenceFactory.Create(wire, segmentSize),
            sink,
            out _);
        Assert.Equal(BulkMultilineReadStatus.Completed, result.Status);
        Assert.True(expected.Span.SequenceEqual(sink.Payload.Span));
    }

    [Fact]
    public void Wire7_FiveByteDelimiter_SplitAtEveryBoundary()
    {
        var wire = InnArticleCorpus.ReadAllBytes("wire-7");
        var expected = InnArticleCorpus.ExpectedWirePayload(wire);
        Assert.True(wire.AsSpan().EndsWith("\r\n.\r\n"u8));

        for (var split = 0; split <= 5; split++)
        {
            var prefix = wire.AsMemory()[..^5];
            var delim = wire.AsMemory()[^5..];
            ReadOnlySequence<byte> seq;
            if (split == 0)
            {
                seq = SegmentedSequenceFactory.CreateFromParts(prefix, delim);
            }
            else if (split == 5)
            {
                seq = SegmentedSequenceFactory.CreateFromParts(prefix.ToArray().Concat(delim.ToArray()).ToArray());
            }
            else
            {
                seq = SegmentedSequenceFactory.CreateFromParts(
                    prefix,
                    delim[..split],
                    delim[split..]);
            }

            var sink = new RecordingArticleSink();
            var result = BulkMultilineFramer.ReadArticleFromSequence(seq, sink, out _);
            Assert.Equal(BulkMultilineReadStatus.Completed, result.Status);
            Assert.True(expected.Span.SequenceEqual(sink.Payload.Span));
        }
    }

    [Fact]
    public async Task Wire7_DelimiterSplitAcrossReads()
    {
        var wire = InnArticleCorpus.ReadAllBytes("wire-7");
        var expected = InnArticleCorpus.ExpectedWirePayload(wire);
        var pipe = new Pipe(new PipeOptions(
            minimumSegmentSize: 8,
            pauseWriterThreshold: 1024 * 1024,
            resumeWriterThreshold: 1024 * 1024,
            useSynchronizationContext: false));

        var readTask = Task.Run(async () =>
        {
            var sink = new RecordingArticleSink();
            var result = await BulkMultilineFramer.ReadArticleAsync(pipe.Reader, sink);
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
        Assert.True(expected.Span.SequenceEqual(payload));
    }

    [Fact]
    public void Wire7_HelloDotStyleTrailingPeriod_IsNotTerminator()
    {
        // Upstream wire-7 body includes lines that end with '.' before CRLF inside content;
        // terminator is only the final .\r\n line. Prove payload retains those periods.
        var wire = InnArticleCorpus.ReadAllBytes("wire-7");
        var expected = InnArticleCorpus.ExpectedWirePayload(wire);
        Assert.Contains(".\r\n"u8, expected.Span); // e.g. "...parser.\r\n" content, not the terminator

        var sink = new RecordingArticleSink();
        BulkMultilineFramer.ReadArticleFromSequence(new ReadOnlySequence<byte>(wire), sink, out _);
        Assert.True(expected.Span.SequenceEqual(sink.Payload.Span));
    }

    [Fact]
    public async Task Wire7_CurrentUnstuffs_BulkPreservesWire()
    {
        var wire = InnArticleCorpus.ReadAllBytes("wire-7");
        var expectedWire = InnArticleCorpus.ExpectedWirePayload(wire);

        var bulkSink = new RecordingArticleSink();
        var bulk = BulkMultilineFramer.ReadArticleFromSequence(new ReadOnlySequence<byte>(wire), bulkSink, out _);
        Assert.Equal(BulkMultilineReadStatus.Completed, bulk.Status);
        Assert.True(expectedWire.Span.SequenceEqual(bulkSink.Payload.Span));

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(wire);
        await pipe.Writer.CompleteAsync();
        var current = await NntpMultilineDataReader
            .ReadArticleAsync(pipe.Reader, maxArticleBytes: 1024 * 1024, CancellationToken.None);
        Assert.Equal(NntpMultilineReadStatus.Completed, current.Status);

        // Representation difference under measurement: current unstuffs; bulk does not.
        Assert.True(current.Payload.Span.SequenceEqual(UnstuffWirePayload(expectedWire.Span)));
        Assert.NotEqual(current.Payload.Length, expectedWire.Length);
    }

    [Fact]
    public void WireTruncated_IsIncomplete()
    {
        var wire = InnArticleCorpus.ReadAllBytes("wire-truncated");
        Assert.False(InnArticleCorpus.ContainsFiveByteDelimiter(wire));
        var sink = new RecordingArticleSink();
        var result = BulkMultilineFramer.ReadArticleFromSequence(new ReadOnlySequence<byte>(wire), sink, out _);
        Assert.Equal(BulkMultilineReadStatus.Incomplete, result.Status);
        Assert.True(wire.AsSpan().SequenceEqual(sink.Payload.Span));
    }

    [Fact]
    public void WireStrange_IsIncomplete_PreservesExactBytesIncludingNul()
    {
        var wire = InnArticleCorpus.ReadAllBytes("wire-strange");
        Assert.Contains((byte)0, wire);
        var sink = new RecordingArticleSink();
        var result = BulkMultilineFramer.ReadArticleFromSequence(
            SegmentedSequenceFactory.Create(wire, 1),
            sink,
            out _);
        Assert.Equal(BulkMultilineReadStatus.Incomplete, result.Status);
        Assert.True(wire.AsSpan().SequenceEqual(sink.Payload.Span));
    }

    [Theory]
    [InlineData("5")]
    [InlineData("6")]
    [InlineData("7")]
    public void LeadingDotSpoolFixtures_DoNotFalseComplete(string name)
    {
        var entry = InnArticleCorpus.LoadManifest().Files.Single(f => f.RelativePath == name);
        Assert.True(entry.ContainsLeadingDotLines);
        Assert.Equal("spool_or_non_wire_no_nntp_terminator", entry.FramingClassification);
        var wire = InnArticleCorpus.ReadAllBytes(name);
        var sink = new RecordingArticleSink();
        var result = BulkMultilineFramer.ReadArticleFromSequence(
            SegmentedSequenceFactory.Create(wire, 2),
            sink,
            out _);
        Assert.Equal(BulkMultilineReadStatus.Incomplete, result.Status);
        Assert.True(wire.AsSpan().SequenceEqual(sink.Payload.Span));
    }

    [Fact]
    public void Pipelined_TwoWire7_SecondRemains()
    {
        var one = InnArticleCorpus.ReadAllBytes("wire-7");
        var combined = new byte[one.Length * 2];
        one.CopyTo(combined.AsSpan());
        one.CopyTo(combined.AsSpan(one.Length));

        var sink = new RecordingArticleSink();
        var seq = SegmentedSequenceFactory.Create(combined, 5);
        var result = BulkMultilineFramer.ReadArticleFromSequence(seq, sink, out var consumed);
        Assert.Equal(BulkMultilineReadStatus.Completed, result.Status);
        Assert.True(InnArticleCorpus.ExpectedWirePayload(one).Span.SequenceEqual(sink.Payload.Span));
        Assert.True(one.AsSpan().SequenceEqual(ToArray(seq.Slice(consumed))));
    }

    private static byte[] UnstuffWirePayload(ReadOnlySpan<byte> wirePayload)
    {
        // Mirror production line-based unstuffing for comparison only.
        var output = new ArrayBufferWriter<byte>(wirePayload.Length);
        var offset = 0;
        while (offset < wirePayload.Length)
        {
            var nl = wirePayload[offset..].IndexOf("\r\n"u8);
            if (nl < 0)
            {
                throw new InvalidOperationException("Expected CRLF-framed wire payload.");
            }

            var line = wirePayload.Slice(offset, nl);
            if (line.Length > 0 && line[0] == (byte)'.')
            {
                line = line[1..];
            }

            var span = output.GetSpan(line.Length + 2);
            line.CopyTo(span);
            span[line.Length] = (byte)'\r';
            span[line.Length + 1] = (byte)'\n';
            output.Advance(line.Length + 2);
            offset += nl + 2;
        }

        return output.WrittenSpan.ToArray();
    }

    private static byte[] ToArray(ReadOnlySequence<byte> sequence)
    {
        var buffer = new byte[sequence.Length];
        sequence.CopyTo(buffer);
        return buffer;
    }
}
