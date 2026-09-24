using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using VectorNNTP.NNTPD.Session.Commands;
using VectorNNTP.NNTPD.Session.Framing;
using Xunit;

namespace VectorNNTP.NNTPD.Tests.Session.Framing;

/// <summary>
/// Continuous STREAM scanner: command/article units, ownership, splits, and control-plane rules.
/// TAKETHIS payload is framed wire bytes (terminator omitted, no destuff).
/// </summary>
public sealed class NntpContinuousRxTests
{
    [Fact]
    public async Task OneTakeThis_CopiesFramedWireAndOmitsTerminator()
    {
        var wire = Encoding.ASCII.GetBytes("TAKETHIS <a@b>\r\n..foo\r\nbar\r\n.\r\n");
        var unit = await ReadOneAsync(wire, consumeTakeThisArticle: true);
        Assert.Equal(NntpContinuousRxKind.TakeThis, unit.Kind);
        Assert.Equal("<a@b>", unit.MessageId);
        Assert.Equal(NntpMultilineReadStatus.Completed, unit.Article.Status);
        Assert.Equal("..foo\r\nbar\r\n"u8.ToArray(), unit.Article.Payload.ToArray());
    }

    [Fact]
    public async Task ManyTakeThis_SamePipeBuffer_NoExtraTransportRead()
    {
        var wire = Encoding.ASCII.GetBytes(
            "TAKETHIS <a@t>\r\none\r\n.\r\nTAKETHIS <b@t>\r\ntwo\r\n.\r\nTAKETHIS <c@t>\r\nthree\r\n.\r\n");
        var units = await ReadAllAsync(wire, consumeTakeThisArticle: true);
        Assert.Equal(3, units.Count);
        Assert.All(units, u => Assert.Equal(NntpContinuousRxKind.TakeThis, u.Kind));
        Assert.Equal(["<a@t>", "<b@t>", "<c@t>"], units.Select(u => u.MessageId));
        Assert.Equal("one\r\n"u8.ToArray(), units[0].Article.Payload.ToArray());
        Assert.Equal("two\r\n"u8.ToArray(), units[1].Article.Payload.ToArray());
        Assert.Equal("three\r\n"u8.ToArray(), units[2].Article.Payload.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(64)]
    public async Task ArbitrarySegmentBoundaries_MatchFramedWireOracle(int segmentSize)
    {
        var article = FramingWireFactory.WithTerminator("Hello.\r\n..foo\r\n...\r\n");
        var header = "TAKETHIS <seg@t>\r\n"u8.ToArray();
        var wire = header.Concat(article).ToArray();
        var expected = article[..^3];
        var unit = await ReadOneSegmentedAsync(wire, segmentSize, consumeTakeThisArticle: true);
        Assert.Equal(NntpContinuousRxKind.TakeThis, unit.Kind);
        Assert.Equal(NntpMultilineReadStatus.Completed, unit.Article.Status);
        Assert.Equal(expected, unit.Article.Payload.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CommandCrlfSplit_StillRecognizesTakeThis(int holdBack)
    {
        var wire = Encoding.ASCII.GetBytes("TAKETHIS <split@t>\r\nbody\r\n.\r\n");
        var unit = await ReadSplitAsync(wire, holdBack, consumeTakeThisArticle: true);
        Assert.Equal(NntpContinuousRxKind.TakeThis, unit.Kind);
        Assert.Equal("<split@t>", unit.MessageId);
        Assert.Equal("body\r\n"u8.ToArray(), unit.Article.Payload.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task ArticleTerminatorSplit_Completes(int holdBack)
    {
        var wire = Encoding.ASCII.GetBytes("TAKETHIS <term@t>\r\nkeep\r\n.\r\n");
        var unit = await ReadSplitAsync(wire, holdBack, consumeTakeThisArticle: true);
        Assert.Equal(NntpContinuousRxKind.TakeThis, unit.Kind);
        Assert.Equal(NntpMultilineReadStatus.Completed, unit.Article.Status);
        Assert.Equal("keep\r\n"u8.ToArray(), unit.Article.Payload.ToArray());
    }

    [Fact]
    public async Task CommandLikeTextInsideArticle_IsPayload()
    {
        var stored = Encoding.ASCII.GetBytes("QUIT\r\nCHECK x\r\nTAKETHIS y\r\n");
        var wire = FramingWireFactory.BuildTakeThisTransaction("<in@body>", stored);
        var units = await ReadAllAsync(wire, consumeTakeThisArticle: true);
        Assert.Single(units);
        Assert.Equal(NntpContinuousRxKind.TakeThis, units[0].Kind);
        Assert.Equal(stored, units[0].Article.Payload.ToArray());
    }

    [Fact]
    public async Task CheckAndQuit_AreControlCommands()
    {
        var wire = Encoding.ASCII.GetBytes(
            "CHECK <x@y>\r\nTAKETHIS <a@b>\r\nhi\r\n.\r\nQUIT\r\nTAKETHIS <after@q>\r\nz\r\n.\r\n");
        var units = await ReadAllAsync(wire, consumeTakeThisArticle: true, stopAfterQuit: true);
        Assert.Equal(3, units.Count);
        Assert.Equal(NntpContinuousRxKind.Command, units[0].Kind);
        Assert.Equal("CHECK <x@y>", units[0].CommandLine);
        Assert.Equal(NntpContinuousRxKind.TakeThis, units[1].Kind);
        Assert.Equal("<a@b>", units[1].MessageId);
        Assert.Equal(NntpContinuousRxKind.Command, units[2].Kind);
        Assert.Equal("QUIT", units[2].CommandLine);
    }

    [Fact]
    public async Task IncompleteCommand_NeedMoreThenCompletes()
    {
        var parser = new NntpContinuousRxParser();
        var partial = new ReadOnlySequence<byte>("TAKETH"u8.ToArray());
        Assert.Equal(NntpContinuousRxKind.NeedMore, parser.TryConsume(ref partial, true, 1024).Kind);

        var unit = await ReadOneAsync("TAKETHIS <late@t>\r\nok\r\n.\r\n"u8.ToArray(), consumeTakeThisArticle: true);
        Assert.Equal(NntpContinuousRxKind.TakeThis, unit.Kind);
        Assert.Equal("<late@t>", unit.MessageId);
    }

    [Fact]
    public async Task IncompleteArticle_Eof_IsIncomplete()
    {
        var wire = Encoding.ASCII.GetBytes("TAKETHIS <e@e>\r\nno-terminator");
        var unit = await ReadOneAsync(wire, consumeTakeThisArticle: true);
        Assert.Equal(NntpContinuousRxKind.TakeThis, unit.Kind);
        Assert.Equal(NntpMultilineReadStatus.Incomplete, unit.Article.Status);
        Assert.True(unit.Article.Payload.IsEmpty);
    }

    [Fact]
    public async Task EmptyArticle_IsCompletedEmptyPayload()
    {
        var wire = Encoding.ASCII.GetBytes("TAKETHIS <e@e>\r\n.\r\n");
        var unit = await ReadOneAsync(wire, consumeTakeThisArticle: true);
        Assert.Equal(NntpMultilineReadStatus.Completed, unit.Article.Status);
        Assert.Equal(0, unit.Article.Payload.Length);
    }

    [Fact]
    public async Task Eof_EmptyPipe_IsNeedMore()
    {
        var unit = await ReadOneAsync([], consumeTakeThisArticle: true);
        Assert.Equal(NntpContinuousRxKind.NeedMore, unit.Kind);
    }

    [Fact]
    public async Task Cancellation_Throws()
    {
        var pipe = new Pipe();
        var parser = new NntpContinuousRxParser();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NntpContinuousRxReader
                .ReadUnitAsync(pipe.Reader, parser, true, 1024, cts.Token)
                .AsTask());
    }

    [Fact]
    public async Task LargeArticle_MatchesOracle()
    {
        var article = FramingWireFactory.Large768KiBArticle();
        var header = "TAKETHIS <large@t>\r\n"u8.ToArray();
        var wire = header.Concat(article).ToArray();
        var expected = article[..^3];
        var unit = await ReadOneAsync(wire, consumeTakeThisArticle: true, maxArticleBytes: 4 * 1024 * 1024);
        Assert.Equal(NntpMultilineReadStatus.Completed, unit.Article.Status);
        Assert.Equal(expected.Length, unit.Article.Payload.Length);
        Assert.True(expected.AsSpan().SequenceEqual(unit.Article.Payload.Span));
    }

    [Fact]
    public async Task BinaryNulArticle_PreservedInFramedPayload()
    {
        var body = new byte[] { (byte)'a', 0, (byte)'b', (byte)'\r', (byte)'\n' };
        var ms = new MemoryStream();
        ms.Write("TAKETHIS <nul@t>\r\n"u8);
        ms.Write(body);
        ms.Write(".\r\n"u8);
        var unit = await ReadOneAsync(ms.ToArray(), consumeTakeThisArticle: true);
        Assert.Equal(NntpMultilineReadStatus.Completed, unit.Article.Status);
        Assert.Equal(body, unit.Article.Payload.ToArray());
        Assert.Contains((byte)0, unit.Article.Payload.ToArray());
    }

    [Fact]
    public async Task TooLarge_ConsumesTerminator_EmptyPayload()
    {
        var wire = Encoding.ASCII.GetBytes("TAKETHIS <big@t>\r\n0123456789\r\n.\r\nQUIT\r\n");
        var units = await ReadAllAsync(wire, consumeTakeThisArticle: true, maxArticleBytes: 4);
        Assert.Equal(2, units.Count);
        Assert.Equal(NntpMultilineReadStatus.TooLarge, units[0].Article.Status);
        Assert.True(units[0].Article.Payload.IsEmpty);
        Assert.Equal("QUIT", units[1].CommandLine);
    }

    [Fact]
    public async Task WithoutConsumeArticle_TakeThisIsCommandLineOnly()
    {
        var wire = Encoding.ASCII.GetBytes("TAKETHIS <x@y>\r\nQUIT\r\n");
        var units = await ReadAllAsync(wire, consumeTakeThisArticle: false);
        Assert.Equal(2, units.Count);
        Assert.Equal(NntpContinuousRxKind.Command, units[0].Kind);
        Assert.Equal("TAKETHIS <x@y>", units[0].CommandLine);
        Assert.Equal("QUIT", units[1].CommandLine);
    }

    [Fact]
    public async Task OwnedPayload_SurvivesSubsequentAdvance()
    {
        var wire = Encoding.ASCII.GetBytes("TAKETHIS <own@t>\r\nkeep-me\r\n.\r\nTAKETHIS <n@t>\r\nx\r\n.\r\n");
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(wire);
        await pipe.Writer.CompleteAsync();
        var parser = new NntpContinuousRxParser();
        var first = await NntpContinuousRxReader.ReadUnitAsync(pipe.Reader, parser, true, 64 * 1024, CancellationToken.None);
        var second = await NntpContinuousRxReader.ReadUnitAsync(pipe.Reader, parser, true, 64 * 1024, CancellationToken.None);
        Assert.Equal("keep-me\r\n"u8.ToArray(), first.Article.Payload.ToArray());
        Assert.Equal("x\r\n"u8.ToArray(), second.Article.Payload.ToArray());
    }

    [Fact]
    public async Task StreamScanner_PreservesStuffedWire_WhileMultilineReaderDestuffs()
    {
        var article = FramingWireFactory.WithTerminator("Hello.\r\n..foo\r\nbar\r\n");
        var header = "TAKETHIS <agree@t>\r\n"u8.ToArray();
        var wire = header.Concat(article).ToArray();
        var stream = await ReadOneAsync(wire, consumeTakeThisArticle: true);
        var reader = await FramingPipe.ReadOneAsync(article);
        Assert.Equal(NntpMultilineReadStatus.Completed, stream.Article.Status);
        Assert.Equal(NntpMultilineReadStatus.Completed, reader.Status);
        Assert.Equal("Hello.\r\n..foo\r\nbar\r\n"u8.ToArray(), stream.Article.Payload.ToArray());
        Assert.Equal("Hello.\r\n.foo\r\nbar\r\n"u8.ToArray(), reader.Payload.ToArray());
        Assert.NotEqual(reader.Payload.ToArray(), stream.Article.Payload.ToArray());
    }

    [Fact]
    public async Task ManyCrlfLines_PreserveExactPayload()
    {
        var body = new StringBuilder();
        for (var i = 0; i < 200; i++)
        {
            body.Append("line-").Append(i).Append("\r\n");
        }

        var stored = Encoding.ASCII.GetBytes(body.ToString());
        var wire = FramingWireFactory.BuildTakeThisTransaction("<many@t>", stored);
        var unit = await ReadOneAsync(wire, consumeTakeThisArticle: true);
        Assert.Equal(stored, unit.Article.Payload.ToArray());
    }

    [Fact]
    public async Task StuffedDotLines_ArePayloadNotTerminator()
    {
        var wire = Encoding.ASCII.GetBytes("TAKETHIS <dots@t>\r\n..one\r\n..two\r\n...three\r\n.\r\n");
        var unit = await ReadOneAsync(wire, consumeTakeThisArticle: true);
        Assert.Equal("..one\r\n..two\r\n...three\r\n"u8.ToArray(), unit.Article.Payload.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task FiveByteTerminator_SplitAcrossEveryByteBoundary(int holdBack)
    {
        var wire = Encoding.ASCII.GetBytes("TAKETHIS <five@t>\r\nbody-line\r\n.\r\n");
        var unit = await ReadSplitAsync(wire, holdBack, consumeTakeThisArticle: true);
        Assert.Equal(NntpMultilineReadStatus.Completed, unit.Article.Status);
        Assert.Equal("body-line\r\n"u8.ToArray(), unit.Article.Payload.ToArray());
    }

    [Fact]
    public async Task BinaryCrLfInsideLine_DoesNotPrematurelyTerminate()
    {
        // RFC 3977 forbids isolated CR/LF in a line; STREAM still treats only \r\n.\r\n as the end.
        var body = new byte[] { (byte)'a', (byte)'\r', (byte)'b', (byte)'\n', (byte)'c', (byte)'\r', (byte)'\n' };
        var ms = new MemoryStream();
        ms.Write("TAKETHIS <bin@t>\r\n"u8);
        ms.Write(body);
        ms.Write(".\r\n"u8);
        var unit = await ReadOneAsync(ms.ToArray(), consumeTakeThisArticle: true);
        Assert.Equal(body, unit.Article.Payload.ToArray());
    }

    [Fact]
    public void TryConsume_MultipleUnitsFromOneSequence()
    {
        var wire = Encoding.ASCII.GetBytes(
            "TAKETHIS <1@t>\r\na\r\n.\r\nTAKETHIS <2@t>\r\nb\r\n.\r\n");
        var buffer = new ReadOnlySequence<byte>(wire);
        var parser = new NntpContinuousRxParser();
        var first = Capture(parser, parser.TryConsume(ref buffer, true, 1024));
        var second = Capture(parser, parser.TryConsume(ref buffer, true, 1024));
        Assert.Equal(NntpContinuousRxKind.TakeThis, first.Kind);
        Assert.Equal(NntpContinuousRxKind.TakeThis, second.Kind);
        Assert.Equal("<1@t>", first.MessageId);
        Assert.Equal("<2@t>", second.MessageId);
        Assert.True(buffer.IsEmpty);
    }

    private static CapturedRxUnit Capture(NntpContinuousRxParser parser, NntpContinuousRxUnit unit)
    {
        var line = parser.CurrentCommandLine.Span;
        return new CapturedRxUnit(
            unit,
            Encoding.ASCII.GetString(line),
            Encoding.ASCII.GetString(unit.Command.ArgumentSpan(line)));
    }

    private static async Task<CapturedRxUnit> ReadOneAsync(
        byte[] wire,
        bool consumeTakeThisArticle,
        int maxArticleBytes = 8 * 1024 * 1024)
    {
        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 16 * 1024 * 1024,
            resumeWriterThreshold: 8 * 1024 * 1024,
            useSynchronizationContext: false));
        if (wire.Length > 0)
        {
            await pipe.Writer.WriteAsync(wire);
        }

        await pipe.Writer.CompleteAsync();
        var parser = new NntpContinuousRxParser();
        var unit = await NntpContinuousRxReader.ReadUnitAsync(
            pipe.Reader,
            parser,
            consumeTakeThisArticle,
            maxArticleBytes,
            CancellationToken.None);
        return Capture(parser, unit);
    }

    private static async Task<CapturedRxUnit> ReadOneSegmentedAsync(
        byte[] wire,
        int segmentSize,
        bool consumeTakeThisArticle)
    {
        var pipe = new Pipe(new PipeOptions(
            minimumSegmentSize: Math.Max(1, segmentSize),
            pauseWriterThreshold: 16 * 1024 * 1024,
            resumeWriterThreshold: 8 * 1024 * 1024,
            useSynchronizationContext: false));
        var parser = new NntpContinuousRxParser();
        var read = NntpContinuousRxReader
            .ReadUnitAsync(pipe.Reader, parser, consumeTakeThisArticle, 8 * 1024 * 1024, CancellationToken.None)
            .AsTask();
        await FramingPipe.WriteChunkedAsync(pipe.Writer, wire, segmentSize, CancellationToken.None);
        await pipe.Writer.CompleteAsync();
        return Capture(parser, await read.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static async Task<CapturedRxUnit> ReadSplitAsync(
        byte[] wire,
        int holdBack,
        bool consumeTakeThisArticle)
    {
        var pipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
        var parser = new NntpContinuousRxParser();
        var read = NntpContinuousRxReader
            .ReadUnitAsync(pipe.Reader, parser, consumeTakeThisArticle, 8 * 1024 * 1024, CancellationToken.None)
            .AsTask();
        await pipe.Writer.WriteAsync(wire.AsMemory()[..^holdBack]);
        await pipe.Writer.FlushAsync();
        await pipe.Writer.WriteAsync(wire.AsMemory()[^holdBack..]);
        await pipe.Writer.FlushAsync();
        await pipe.Writer.CompleteAsync();
        return Capture(parser, await read.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task<List<CapturedRxUnit>> ReadAllAsync(
        byte[] wire,
        bool consumeTakeThisArticle,
        bool stopAfterQuit = false,
        int maxArticleBytes = 8 * 1024 * 1024)
    {
        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 16 * 1024 * 1024,
            resumeWriterThreshold: 8 * 1024 * 1024,
            useSynchronizationContext: false));
        await pipe.Writer.WriteAsync(wire);
        await pipe.Writer.CompleteAsync();
        var parser = new NntpContinuousRxParser();
        var units = new List<CapturedRxUnit>();
        while (true)
        {
            var unit = await NntpContinuousRxReader.ReadUnitAsync(
                pipe.Reader,
                parser,
                consumeTakeThisArticle,
                maxArticleBytes,
                CancellationToken.None);
            if (unit.Kind == NntpContinuousRxKind.NeedMore)
            {
                break;
            }

            var captured = Capture(parser, unit);
            units.Add(captured);
            if (stopAfterQuit
                && captured.Kind == NntpContinuousRxKind.Command
                && captured.Unit.Command.Verb == NntpVerb.Quit)
            {
                break;
            }
        }

        return units;
    }

    private readonly struct CapturedRxUnit
    {
        public CapturedRxUnit(NntpContinuousRxUnit unit, string commandLine, string argument)
        {
            Unit = unit;
            CommandLine = commandLine;
            Argument = argument;
        }

        public NntpContinuousRxUnit Unit { get; }

        public string CommandLine { get; }

        public string Argument { get; }

        public string MessageId => Argument;

        public NntpContinuousRxKind Kind => Unit.Kind;

        public NntpMultilineReadResult Article => Unit.Article;
    }
}
