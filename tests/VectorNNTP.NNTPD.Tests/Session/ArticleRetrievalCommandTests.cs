using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.NNTPD.Newsgroups;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Networking.Transport;
using VectorNNTP.NNTPD.RabbitMq.ArticleWork;
using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Session.CommandProcessor;
using VectorNNTP.NNTPD.Tests.TestDoubles;

namespace VectorNNTP.NNTPD.Tests.Session;

/// <summary>
/// ARTICLE/HEAD/BODY/STAT return RFC 3977 lookup-failure codes. No article store exists.
/// </summary>
public sealed class ArticleRetrievalCommandTests
{
    private static readonly NntpAuthorization Reader = new(
        isAuthenticated: true,
        authorizedReader: true,
        authorizedTransit: false,
        postingPermitted: false,
        streamingPermitted: false);

    public static TheoryData<string> RetrievalVerbs() =>
        new()
        {
            "ARTICLE",
            "HEAD",
            "BODY",
            "STAT",
        };

    [Fact]
    public async Task ArticleMessageId_InvokesRpc_ThenStillReturns430()
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new RecordingArticleWorkRpcClient();
        var session = duplex.CreateSession(articleWorkRpc: rpc);
        await DispatchLineAsync(duplex, session, "ARTICLE <12345@example.invalid>");
        Assert.Equal("430 No article with that message-id", await duplex.ReadClientLineAsync());
        var messageId = Assert.Single(rpc.Lookups);
        Assert.Equal("<12345@example.invalid>"u8.ToArray(), messageId.ToArray());
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task HeadBodyStat_MessageId_DoesNotInvokeRpc(string verb)
    {
        if (verb == "ARTICLE")
        {
            return;
        }

        await using var duplex = await ArticleDuplex.CreateAsync();
        var rpc = new RecordingArticleWorkRpcClient();
        var session = duplex.CreateSession(articleWorkRpc: rpc);
        await DispatchLineAsync(duplex, session, $"{verb} <12345@example.invalid>");
        Assert.Equal("430 No article with that message-id", await duplex.ReadClientLineAsync());
        Assert.Empty(rpc.Lookups);
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task MessageId_Returns430_WithoutSelectingGroup(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession();
        await DispatchLineAsync(duplex, session, $"{verb} <does-not-exist@example.com>");
        Assert.Equal("430 No article with that message-id", await duplex.ReadClientLineAsync());
        Assert.False(session.HasSelectedGroup);
        await AssertNextCommandStillWorksAsync(duplex, session);
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task Number_WithoutGroup_Returns412(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession();
        await DispatchLineAsync(duplex, session, $"{verb} 123456");
        Assert.Equal("412 No newsgroup selected", await duplex.ReadClientLineAsync());
        Assert.False(session.HasSelectedGroup);
        await AssertNextCommandStillWorksAsync(duplex, session);
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task Number_WithGroup_Returns423_AndDoesNotChangeSelection(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession(SampleSnapshot());
        await DispatchLineAsync(duplex, session, "GROUP misc.test");
        Assert.StartsWith("211 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.True(session.HasSelectedGroup);
        var selected = session.SelectedGroupName.ToArray();

        await DispatchLineAsync(duplex, session, $"{verb} 123456");
        Assert.Equal("423 No article with that number", await duplex.ReadClientLineAsync());
        Assert.True(session.HasSelectedGroup);
        Assert.True(session.SelectedGroupName.SequenceEqual(selected));
        await AssertNextCommandStillWorksAsync(duplex, session);
        Assert.True(session.SelectedGroupName.SequenceEqual(selected));
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task Current_WithoutGroup_Returns412(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession();
        await DispatchLineAsync(duplex, session, verb);
        Assert.Equal("412 No newsgroup selected", await duplex.ReadClientLineAsync());
        Assert.False(session.HasSelectedGroup);
        await AssertNextCommandStillWorksAsync(duplex, session);
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task Current_WithGroup_Returns420_BecauseNoCurrentArticleExists(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession(SampleSnapshot());
        await DispatchLineAsync(duplex, session, "GROUP misc.test");
        Assert.StartsWith("211 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        var selected = session.SelectedGroupName.ToArray();

        await DispatchLineAsync(duplex, session, verb);
        Assert.Equal("420 Current article number is invalid", await duplex.ReadClientLineAsync());
        Assert.True(session.SelectedGroupName.SequenceEqual(selected));
        await AssertNextCommandStillWorksAsync(duplex, session);
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task FailedNumberLookup_DoesNotEstablishCurrentArticle(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession(SampleSnapshot());
        await DispatchLineAsync(duplex, session, "GROUP misc.test");
        Assert.StartsWith("211 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        var selected = session.SelectedGroupName.ToArray();

        await DispatchLineAsync(duplex, session, $"{verb} 123456");
        Assert.Equal("423 No article with that number", await duplex.ReadClientLineAsync());

        await DispatchLineAsync(duplex, session, verb);
        Assert.Equal("420 Current article number is invalid", await duplex.ReadClientLineAsync());
        Assert.True(session.SelectedGroupName.SequenceEqual(selected));
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task MessageIdVsNumber_AreDistinctNotFoundCodes(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession(SampleSnapshot());

        await DispatchLineAsync(duplex, session, $"{verb} <123@example.com>");
        Assert.Equal("430 No article with that message-id", await duplex.ReadClientLineAsync());

        await DispatchLineAsync(duplex, session, "GROUP misc.test");
        Assert.StartsWith("211 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await DispatchLineAsync(duplex, session, $"{verb} 123");
        Assert.Equal("423 No article with that number", await duplex.ReadClientLineAsync());
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task MalformedAngleBracket_Is501_NotAnArticleNumber(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession(SampleSnapshot());
        await DispatchLineAsync(duplex, session, "GROUP misc.test");
        Assert.StartsWith("211 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await DispatchLineAsync(duplex, session, $"{verb} <123@example.com");
        Assert.Equal("501 Syntax error", await duplex.ReadClientLineAsync());
        Assert.True(session.SelectedGroupName.SequenceEqual("misc.test"u8));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("0001")]
    [InlineData("2147483647")]
    [InlineData("2147483648")]
    [InlineData("9999999999999999")]
    public async Task DigitTokenThrough16Digits_IsNumberForm_423WhenGroupSelected(string number)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession(SampleSnapshot());
        await DispatchLineAsync(duplex, session, "GROUP misc.test");
        Assert.StartsWith("211 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        var selected = session.SelectedGroupName.ToArray();

        await DispatchLineAsync(duplex, session, "ARTICLE " + number);
        Assert.Equal("423 No article with that number", await duplex.ReadClientLineAsync());
        Assert.True(session.SelectedGroupName.SequenceEqual(selected));
        await DispatchLineAsync(duplex, session, "ARTICLE");
        Assert.Equal("420 Current article number is invalid", await duplex.ReadClientLineAsync());
    }

    [Theory]
    [InlineData("10000000000000000")]
    [InlineData("12345678901234567")]
    [InlineData("12a")]
    [InlineData("-1")]
    public async Task OutsideRfcDigitSyntax_Returns501(string token)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession(SampleSnapshot());
        await DispatchLineAsync(duplex, session, "GROUP misc.test");
        Assert.StartsWith("211 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await DispatchLineAsync(duplex, session, "ARTICLE " + token);
        Assert.Equal("501 Syntax error", await duplex.ReadClientLineAsync());
        Assert.True(session.SelectedGroupName.SequenceEqual("misc.test"u8));
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task Error_IsSingleLine_WithoutMultilineTerminator(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession(SampleSnapshot());

        await DispatchLineAsync(duplex, session, $"{verb} <missing@example.com>");
        Assert.Equal("430 No article with that message-id", await duplex.ReadClientLineAsync());
        await DispatchLineAsync(duplex, session, "DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await DispatchLineAsync(duplex, session, $"{verb} 123");
        Assert.Equal("412 No newsgroup selected", await duplex.ReadClientLineAsync());
        await DispatchLineAsync(duplex, session, "DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await DispatchLineAsync(duplex, session, "GROUP misc.test");
        Assert.StartsWith("211 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await DispatchLineAsync(duplex, session, $"{verb} 123");
        Assert.Equal("423 No article with that number", await duplex.ReadClientLineAsync());
        await DispatchLineAsync(duplex, session, "DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await DispatchLineAsync(duplex, session, verb);
        Assert.Equal("420 Current article number is invalid", await duplex.ReadClientLineAsync());
        await DispatchLineAsync(duplex, session, "DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SequentialFailedNumberLookups_DoNotChangeGroupOrCurrentArticle()
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession(SampleSnapshot());
        await DispatchLineAsync(duplex, session, "GROUP misc.test");
        Assert.StartsWith("211 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        var selected = session.SelectedGroupName.ToArray();

        foreach (var verb in new[] { "ARTICLE", "HEAD", "BODY", "STAT" })
        {
            await DispatchLineAsync(duplex, session, $"{verb} 123456");
            Assert.Equal("423 No article with that number", await duplex.ReadClientLineAsync());
            Assert.True(session.SelectedGroupName.SequenceEqual(selected));
        }

        await DispatchLineAsync(duplex, session, "STAT");
        Assert.Equal("420 Current article number is invalid", await duplex.ReadClientLineAsync());
        Assert.True(session.SelectedGroupName.SequenceEqual(selected));
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task InvalidArgument_Returns501_NotALookupFailure(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession();
        await DispatchLineAsync(duplex, session, $"{verb} not-an-id");
        Assert.Equal("501 Syntax error", await duplex.ReadClientLineAsync());
        await AssertNextCommandStillWorksAsync(duplex, session);
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public async Task Unauthenticated_StillReturns480(string verb)
    {
        await using var duplex = await ArticleDuplex.CreateAsync();
        var session = duplex.CreateSession(authorize: false);
        await DispatchLineAsync(duplex, session, $"{verb} <a@b.c>");
        Assert.Equal("480 Authentication required", await duplex.ReadClientLineAsync());
    }

    [Theory]
    [MemberData(nameof(RetrievalVerbs))]
    public void Catalog_RequiresAuthenticationAndReader(string verb)
    {
        var parsed = NntpCommandTestParse.ParseCommand(verb);
        Assert.Equal(
            NntpCommandAccess.RequiresAuthentication | NntpCommandAccess.RequiresReader,
            DefaultNntpCommandCatalog.GetAccess(parsed.Verb, parsed.Qualifier));
    }

    private static NewsgroupSnapshot SampleSnapshot() =>
        NewsgroupSnapshot.Create(
        [
            new NewsgroupDefinition("misc.test", "General testing", 3002322, 3000234, NewsgroupPostingStatus.Allowed),
        ]);

    private static async Task DispatchLineAsync(ArticleDuplex duplex, NntpSession session, string command)
    {
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher();
        await NntpCommandTestParse.DispatchAsync(dispatcher, session, response, command);
    }

    private static async Task AssertNextCommandStillWorksAsync(ArticleDuplex duplex, NntpSession session)
    {
        await DispatchLineAsync(duplex, session, "DATE");
        Assert.StartsWith("111 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    private sealed class RecordingArticleWorkRpcClient : IArticleWorkRpcClient
    {
        public List<ReadOnlyMemory<byte>> Lookups { get; } = [];

        public Task<ArticleWorkRpcResult> LookupByMessageIdAsync(
            ReadOnlyMemory<byte> messageId,
            CancellationToken cancellationToken)
        {
            Lookups.Add(messageId.ToArray());
            return Task.FromResult(new ArticleWorkRpcResult(
                ArticleWorkOutcome.Success,
                Guid.NewGuid(),
                "<12345@example.invalid>",
                "Storage",
                "cache://backfiller01.usenet.ninja:119/30edc94157aa16fe644a45a1f1ffe160",
                Error: null,
                "backfiller.storage"));
        }
    }

    private sealed class ArticleDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public PipeWriter ServerOutput => _serverToClient.Writer;

        public static Task<ArticleDuplex> CreateAsync() => Task.FromResult(new ArticleDuplex());

        public NntpSession CreateSession(
            NewsgroupSnapshot? snapshot = null,
            bool authorize = true,
            IArticleWorkRpcClient? articleWorkRpc = null)
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
                ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)));
            var session = new NntpSession(
                connection,
                NullLogger<NntpSession>.Instance,
                newsgroupCatalogue: snapshot is null ? null : new StaticNewsgroupCatalogue(snapshot),
                articleWorkRpc: articleWorkRpc);
            if (authorize)
            {
                session.SetAuthorization(Reader);
            }

            return session;
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
            VectorNNTP.NNTPD.Networking.Certificates.ITlsCertificateContextProvider certificateProvider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
