using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.NNTPD.Networking.Certificates;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Networking.Transport;
using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Session.Commands;

namespace VectorNNTP.NNTPD.Tests.Session;

/// <summary>
/// Full-surface byte parser contracts: classification, syntax status, and reject-before-handler.
/// </summary>
public sealed class NntpCommandParserTests
{
    [Theory]
    [InlineData("ARTICLE", NntpVerb.Article, NntpVerb.None)]
    [InlineData("article", NntpVerb.Article, NntpVerb.None)]
    [InlineData("ArTiClE", NntpVerb.Article, NntpVerb.None)]
    [InlineData("BODY", NntpVerb.Body, NntpVerb.None)]
    [InlineData("CAPABILITIES", NntpVerb.Capabilities, NntpVerb.None)]
    [InlineData("CHECK <a@b.c>", NntpVerb.Check, NntpVerb.None)]
    [InlineData("CHECK <x>", NntpVerb.Check, NntpVerb.None)]
    [InlineData("CHECK <>", NntpVerb.Check, NntpVerb.None)]
    [InlineData("CHECK <@b>", NntpVerb.Check, NntpVerb.None)]
    [InlineData("check <a@b.c>", NntpVerb.Check, NntpVerb.None)]
    [InlineData("cHeCk <a@b.c>", NntpVerb.Check, NntpVerb.None)]
    [InlineData("COMPRESS DEFLATE", NntpVerb.Compress, NntpVerb.None)]
    [InlineData("DATE", NntpVerb.Date, NntpVerb.None)]
    [InlineData("GROUP", NntpVerb.Group, NntpVerb.None)]
    [InlineData("HDR", NntpVerb.Hdr, NntpVerb.None)]
    [InlineData("HEAD", NntpVerb.Head, NntpVerb.None)]
    [InlineData("HELP", NntpVerb.Help, NntpVerb.None)]
    [InlineData("IHAVE <a@b.c>", NntpVerb.Ihave, NntpVerb.None)]
    [InlineData("LAST", NntpVerb.Last, NntpVerb.None)]
    [InlineData("LIST", NntpVerb.List, NntpVerb.None)]
    [InlineData("LIST ACTIVE", NntpVerb.List, NntpVerb.Active)]
    [InlineData("LIST HEADERS", NntpVerb.List, NntpVerb.Headers)]
    [InlineData("LIST MOTD", NntpVerb.List, NntpVerb.Motd)]
    [InlineData("LIST NEWSGROUPS", NntpVerb.List, NntpVerb.Newsgroups)]
    [InlineData("LIST OVERVIEW.FMT", NntpVerb.List, NntpVerb.OverviewFmt)]
    [InlineData("LISTGROUP", NntpVerb.ListGroup, NntpVerb.None)]
    [InlineData("MODE READER", NntpVerb.Mode, NntpVerb.Reader)]
    [InlineData("MODE STREAM", NntpVerb.Mode, NntpVerb.Stream)]
    [InlineData("mode reader", NntpVerb.Mode, NntpVerb.Reader)]
    [InlineData("mode stream", NntpVerb.Mode, NntpVerb.Stream)]
    [InlineData("NEWGROUPS", NntpVerb.Newgroups, NntpVerb.None)]
    [InlineData("NEWNEWS", NntpVerb.Newnews, NntpVerb.None)]
    [InlineData("NEXT", NntpVerb.Next, NntpVerb.None)]
    [InlineData("OVER", NntpVerb.Over, NntpVerb.None)]
    [InlineData("POST", NntpVerb.Post, NntpVerb.None)]
    [InlineData("QUIT", NntpVerb.Quit, NntpVerb.None)]
    [InlineData("STARTTLS", NntpVerb.StartTls, NntpVerb.None)]
    [InlineData("STAT", NntpVerb.Stat, NntpVerb.None)]
    [InlineData("TAKETHIS <a@b.c>", NntpVerb.TakeThis, NntpVerb.None)]
    [InlineData("takethis <a@b.c>", NntpVerb.TakeThis, NntpVerb.None)]
    [InlineData("AUTHINFO USER alice", NntpVerb.AuthInfo, NntpVerb.User)]
    [InlineData("AUTHINFO PASS secret", NntpVerb.AuthInfo, NntpVerb.Pass)]
    [InlineData("AUTHINFO SASL", NntpVerb.AuthInfo, NntpVerb.Sasl)]
    [InlineData("authinfo user alice", NntpVerb.AuthInfo, NntpVerb.User)]
    [InlineData("BENCHIT", NntpVerb.BenchIt, NntpVerb.None)]
    public void Parse_SupportedCommands_AreValid(string text, NntpVerb verb, NntpVerb qualifier)
    {
        var (command, _) = NntpCommandTestParse.Parse(text);
        Assert.True(command.IsValid);
        Assert.Equal(NntpParseStatus.Ok, command.Status);
        Assert.Equal(verb, command.Verb);
        Assert.Equal(qualifier, command.Qualifier);
    }

    /// <summary>RFC 3977 §3.1: tokens are separated by one or more SP or TAB.</summary>
    [Theory]
    [InlineData("CHECK\t<a@b.c>")]
    [InlineData("MODE\tSTREAM")]
    [InlineData("AUTHINFO\tUSER\talice")]
    [InlineData("QUIT\t")]
    [InlineData("  DATE  ")]
    [InlineData("\tHELP")]
    public void Parse_SpAndTabWhitespace_IsAccepted(string text)
    {
        Assert.True(NntpCommandTestParse.ParseCommand(text).IsValid);
    }

    [Theory]
    [InlineData("", NntpParseStatus.Empty)]
    [InlineData(" ", NntpParseStatus.Empty)]
    [InlineData("\t", NntpParseStatus.Empty)]
    [InlineData("   ", NntpParseStatus.Empty)]
    [InlineData("NOSUCHCMD", NntpParseStatus.UnknownVerb)]
    [InlineData("ARTICLEX", NntpParseStatus.UnknownVerb)]
    [InlineData("MODE", NntpParseStatus.UnknownQualifier)]
    [InlineData("MODE UNKNOWN", NntpParseStatus.UnknownQualifier)]
    [InlineData("AUTHINFO", NntpParseStatus.UnknownQualifier)]
    [InlineData("AUTHINFO GENERIC x", NntpParseStatus.UnknownQualifier)]
    [InlineData("AUTHINFO USERXYZ", NntpParseStatus.UnknownQualifier)]
    [InlineData("LIST ACTIVE.TIMES", NntpParseStatus.UnknownQualifier)]
    [InlineData("LIST COUNTS", NntpParseStatus.UnknownQualifier)]
    [InlineData("CHECK", NntpParseStatus.MissingArgument)]
    [InlineData("CHECK ", NntpParseStatus.MissingArgument)]
    [InlineData("TAKETHIS", NntpParseStatus.MissingArgument)]
    [InlineData("IHAVE", NntpParseStatus.MissingArgument)]
    [InlineData("AUTHINFO USER", NntpParseStatus.MissingArgument)]
    [InlineData("AUTHINFO PASS", NntpParseStatus.MissingArgument)]
    [InlineData("COMPRESS", NntpParseStatus.MissingArgument)]
    [InlineData("CHECK not-an-id", NntpParseStatus.InvalidArgument)]
    [InlineData("CHECK x>", NntpParseStatus.InvalidArgument)]
    [InlineData("CHECK <x", NntpParseStatus.InvalidArgument)]
    [InlineData("CHECK >x<", NntpParseStatus.InvalidArgument)]
    [InlineData("TAKETHIS not-an-id", NntpParseStatus.InvalidArgument)]
    [InlineData("IHAVE not-an-id", NntpParseStatus.InvalidArgument)]
    [InlineData("COMPRESS deflate", NntpParseStatus.InvalidArgument)]
    [InlineData("CHECK <a@b.c> extra", NntpParseStatus.ExtraArgument)]
    [InlineData("TAKETHIS <a@b.c> extra", NntpParseStatus.ExtraArgument)]
    [InlineData("IHAVE <a@b.c> extra", NntpParseStatus.ExtraArgument)]
    [InlineData("QUIT extra", NntpParseStatus.ExtraArgument)]
    [InlineData("HELP MORE", NntpParseStatus.ExtraArgument)]
    [InlineData("COMPRESS DEFLATE EXTRA", NntpParseStatus.ExtraArgument)]
    public void Parse_InvalidCommands_HaveExpectedStatus(string text, NntpParseStatus status)
    {
        var command = NntpCommandTestParse.ParseCommand(text);
        Assert.False(command.IsValid);
        Assert.Equal(status, command.Status);
    }

    /// <summary>
    /// IHAVE without a message-id is parser MissingArgument (501), not the old
    /// stub-handler 500. The parser owns required-parameter syntax.
    /// </summary>
    [Fact]
    public void Ihave_WithoutMessageId_IsMissingArgument_NotOk()
    {
        var command = NntpCommandTestParse.ParseCommand("IHAVE");
        Assert.Equal(NntpVerb.Ihave, command.Verb);
        Assert.Equal(NntpParseStatus.MissingArgument, command.Status);
        Assert.False(command.IsValid);
    }

    [Fact]
    public void Parse_DoesNotAllocate_OnRepresentativeLines()
    {
        string[] lines =
        [
            "CHECK <nntp-feed-20260924.012500.7f3a9c2b@peer.usenet.example>",
            "TAKETHIS <nntp-feed-20260924.012500.7f3a9c2b@peer.usenet.example>",
            "MODE STREAM",
            "MODE READER",
            "AUTHINFO USER foo",
            "AUTHINFO PASS bar",
            "QUIT",
            "CHECK not-an-id",
            "",
        ];

        foreach (var text in lines)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            for (var i = 0; i < 8; i++)
            {
                _ = NntpCommandParser.Parse(bytes);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            var sink = 0;
            for (var i = 0; i < 256; i++)
            {
                var command = NntpCommandParser.Parse(bytes);
                sink += (int)command.Verb + (int)command.Status + command.TokenCount;
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.NotEqual(int.MinValue, sink);
        }
    }

    [Theory]
    [InlineData("NOSUCHCMD", "500 Unknown command")]
    [InlineData("MODE UNKNOWN", "501 Unknown command variant")]
    [InlineData("LIST COUNTS", "501 Unknown command variant")]
    [InlineData("CHECK", "501 Syntax error")]
    [InlineData("CHECK not-an-id", "501 Syntax error")]
    [InlineData("CHECK <a@b.c> extra", "501 Syntax error")]
    [InlineData("IHAVE", "501 Syntax error")]
    [InlineData("IHAVE not-an-id", "501 Syntax error")]
    [InlineData("AUTHINFO USER", "501 AUTHINFO USER requires a username")]
    [InlineData("AUTHINFO PASS", "501 AUTHINFO PASS requires a password")]
    [InlineData("AUTHINFO GENERIC x", "501 Unknown command variant")]
    [InlineData("COMPRESS", "501 COMPRESS requires a single algorithm argument")]
    [InlineData("COMPRESS deflate", "501 Syntactically incorrect compression algorithm")]
    [InlineData("QUIT extra", "501 Syntax error")]
    [InlineData("HELP MORE", "501 Syntax error")]
    public async Task InvalidCommand_IsRejectedWithProtocolError_AndDoesNotReachHandler(string commandLine, string response)
    {
        await using var duplex = await ParserDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: true,
            authorizedTransit: true,
            postingPermitted: true,
            streamingPermitted: true));
        var dispatcher = new NntpCommandDispatcher();
        var writer = new NntpResponseWriter(duplex.ServerOutput);

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, writer, commandLine);
        Assert.Equal(response, await duplex.ReadClientLineAsync());
    }

    [Fact]
    public async Task InvalidCheck_DoesNotInvokeHandler_EvenWhenTransitAuthorized()
    {
        await using var duplex = await ParserDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: false,
            authorizedTransit: true,
            postingPermitted: false,
            streamingPermitted: true));
        var dispatcher = new NntpCommandDispatcher();
        var writer = new NntpResponseWriter(duplex.ServerOutput);

        await NntpCommandTestParse.DispatchAsync(dispatcher, session, writer, "CHECK not-an-id");
        var line = await duplex.ReadClientLineAsync();
        Assert.Equal("501 Syntax error", line);
        Assert.DoesNotContain("238", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NOSUCHCMD")]
    [InlineData("MODE")]
    [InlineData("AUTHINFO GENERIC x")]
    [InlineData("CHECK")]
    [InlineData("CHECK not-an-id")]
    [InlineData("IHAVE")]
    [InlineData("TAKETHIS not-an-id")]
    [InlineData("QUIT extra")]
    public async Task InvalidCommand_CannotConstructHandlerContext(string commandLine)
    {
        await using var duplex = await ParserDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var invalid = NntpCommandTestParse.ParseCommand(commandLine);
        Assert.False(invalid.IsValid);
        var writer = new NntpResponseWriter(duplex.ServerOutput);
        Assert.Throws<ArgumentException>(() =>
            new NntpCommandContext(session, invalid, ReadOnlyMemory<byte>.Empty, writer));
    }

    [Fact]
    public async Task Ihave_BasicEnvelope_ReachesHandler_NotAnIdInteriorIsNotRejectedByParser()
    {
        await using var duplex = await ParserDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: false,
            authorizedTransit: true,
            postingPermitted: false,
            streamingPermitted: true));
        var dispatcher = new NntpCommandDispatcher();
        var writer = new NntpResponseWriter(duplex.ServerOutput);

        var parsed = NntpCommandTestParse.ParseCommand("IHAVE <x>");
        Assert.True(parsed.IsValid);
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, writer, "IHAVE <x>");
        Assert.Contains("500 Command not implemented", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void MessageId_BasicWellFormed_UsesOnlyEnvelopeRules()
    {
        Assert.True(NntpMessageId.IsBasicWellFormed("<x>"u8));
        Assert.True(NntpMessageId.IsBasicWellFormed("<x>"));
        Assert.True(NntpMessageId.IsBasicWellFormed("<>"u8));
        Assert.True(NntpMessageId.IsBasicWellFormed("<@>"u8));

        var exact250 = new byte[250];
        exact250[0] = (byte)'<';
        exact250[^1] = (byte)'>';
        exact250.AsSpan(1, 248).Fill((byte)'x');
        Assert.True(NntpMessageId.IsBasicWellFormed(exact250));
        Assert.True(NntpCommandParser.Parse(BuildCommand("CHECK ", exact250)).IsValid);

        var tooLong = new byte[251];
        tooLong[0] = (byte)'<';
        tooLong[^1] = (byte)'>';
        tooLong.AsSpan(1, 249).Fill((byte)'x');
        Assert.False(NntpMessageId.IsBasicWellFormed(tooLong));
        Assert.Equal(NntpParseStatus.InvalidArgument, NntpCommandParser.Parse(BuildCommand("CHECK ", tooLong)).Status);

        Assert.False(NntpMessageId.IsBasicWellFormed(ReadOnlySpan<byte>.Empty));
        Assert.False(NntpMessageId.IsBasicWellFormed(""));
        Assert.False(NntpMessageId.IsBasicWellFormed("x>"u8));
        Assert.False(NntpMessageId.IsBasicWellFormed("<x"u8));
        Assert.False(NntpMessageId.IsBasicWellFormed("x"u8));
        Assert.False(NntpMessageId.IsBasicWellFormed(">x<"u8));
        Assert.False(NntpMessageId.IsBasicWellFormed("not-an-id"u8));
    }

    [Fact]
    public void MessageId_WellFormed_RemainsStricterThanParserEnvelope()
    {
        Assert.True(NntpMessageId.IsWellFormed("<a@b.c>"));
        Assert.True(NntpMessageId.IsWellFormed("<a@b.c>"u8));
        Assert.False(NntpMessageId.IsWellFormed("<x>"));
        Assert.False(NntpMessageId.IsWellFormed("<x>"u8));
        Assert.False(NntpMessageId.IsWellFormed("not-an-id"));
        Assert.False(NntpMessageId.IsWellFormed("not-an-id"u8));
        Assert.False(NntpMessageId.IsWellFormed(""));
        Assert.False(NntpMessageId.IsWellFormed(ReadOnlySpan<byte>.Empty));
    }

    private static byte[] BuildCommand(string prefix, ReadOnlySpan<byte> argument)
    {
        var prefixBytes = Encoding.ASCII.GetBytes(prefix);
        var line = new byte[prefixBytes.Length + argument.Length];
        prefixBytes.CopyTo(line);
        argument.CopyTo(line.AsSpan(prefixBytes.Length));
        return line;
    }

    private sealed class ParserDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public PipeWriter ServerOutput => _serverToClient.Writer;

        public static Task<ParserDuplex> CreateAsync() => Task.FromResult(new ParserDuplex());

        public NntpSession CreateSession()
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)));
            return new NntpSession(connection, NullLogger<NntpSession>.Instance);
        }

        public async Task<string> ReadClientLineAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var line = await NntpCommandLineReader.ReadLineAsync(_serverToClient.Reader, cts.Token);
            Assert.NotNull(line);
            return line!;
        }

        public async ValueTask DisposeAsync()
        {
            await _clientToServer.Writer.CompleteAsync();
            await _clientToServer.Reader.CompleteAsync();
            await _serverToClient.Writer.CompleteAsync();
            await _serverToClient.Reader.CompleteAsync();
        }
    }

    private sealed class PipeNntpConnection : INntpConnection
    {
        private readonly CancellationTokenSource _cts = new();

        public PipeNntpConnection(PipeReader input, PipeWriter output, ConnectionClientIdentity identity)
        {
            Input = input;
            Output = output;
            ClientIdentity = identity;
        }

        public PipeReader Input { get; }

        public PipeWriter Output { get; }

        public System.Net.EndPoint? RemoteEndPoint => ClientIdentity.TcpPeer;

        public System.Net.EndPoint? LocalEndPoint => null;

        public ConnectionClientIdentity ClientIdentity { get; }

        public bool IsTls => false;

        public bool TryGetNegotiatedTlsParameters(out string tlsVersion, out string cipher)
        {
            tlsVersion = string.Empty;
            cipher = string.Empty;
            return false;
        }

        public bool IsCompressed => false;

        public CancellationToken ConnectionClosed => _cts.Token;

        public bool IsCompleted => _cts.IsCancellationRequested;

        public long OutboundIdleVersion => 0;

        public Task PauseReadsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WaitForOutboundDeliveryAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WaitForOutboundDeliveryAndPauseReadsAsync(
            long outboundIdleVersionBeforeFlush,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CompleteAsync(Exception? exception = null)
        {
            _cts.Cancel();
            return Task.CompletedTask;
        }

        public Task UpgradeToTlsAsync(
            ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
