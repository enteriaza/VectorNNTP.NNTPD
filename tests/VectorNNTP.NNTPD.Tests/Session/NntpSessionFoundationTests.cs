using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.NNTPD.Networking.Certificates;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Networking.Transport;
using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Session.Commands;

namespace VectorNNTP.NNTPD.Tests.Session;

public sealed class NntpSessionFoundationTests
{
    [Fact]
    public void Authorization_DefaultsDenyTransitAndPosting()
    {
        var authz = NntpAuthorization.Unauthenticated;
        Assert.False(authz.IsAuthenticated);
        Assert.False(authz.AuthorizedReader);
        Assert.False(authz.AuthorizedTransit);
        Assert.False(authz.PostingPermitted);
        Assert.False(authz.StreamingPermitted);
    }

    [Fact]
    public void CommandParser_SplitsVerbAndArguments()
    {
        Assert.True(NntpCommandParser.TryParse("CAPABILITIES", out var caps));
        Assert.Equal("CAPABILITIES", caps.Verb);
        Assert.Empty(caps.Tokens);

        Assert.True(NntpCommandParser.TryParse("AUTHINFO USER fred", out var auth));
        Assert.Equal("AUTHINFO", auth.Verb);
        Assert.Equal(["USER", "fred"], auth.Tokens);

        Assert.False(NntpCommandParser.TryParse("   ", out _));
    }

    [Fact]
    public async Task Dispatcher_UnknownCommand_Returns500()
    {
        await using var duplex = await SessionTestDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher(
            DefaultNntpCommandCatalog.Create(),
            NullLogger<NntpCommandDispatcher>.Instance);

        Assert.True(NntpCommandParser.TryParse("NOSUCHCMD", out var parsed));
        await dispatcher.DispatchAsync(session, parsed, response, CancellationToken.None);
        Assert.Contains("500 Unknown command", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatcher_UnknownAuthinfoVariant_Returns501()
    {
        await using var duplex = await SessionTestDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher(
            DefaultNntpCommandCatalog.Create(),
            NullLogger<NntpCommandDispatcher>.Instance);

        Assert.True(NntpCommandParser.TryParse("AUTHINFO GENERIC x", out var parsed));
        await dispatcher.DispatchAsync(session, parsed, response, CancellationToken.None);
        Assert.Contains("501 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatcher_ReaderCommandWithoutAuth_Returns480()
    {
        await using var duplex = await SessionTestDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher(
            DefaultNntpCommandCatalog.Create(),
            NullLogger<NntpCommandDispatcher>.Instance);

        Assert.True(NntpCommandParser.TryParse("ARTICLE", out var parsed));
        await dispatcher.DispatchAsync(session, parsed, response, CancellationToken.None);
        Assert.Contains("480 Authentication required", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatcher_TransitCommandWithoutAuth_Returns480()
    {
        await using var duplex = await SessionTestDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher(
            DefaultNntpCommandCatalog.Create(),
            NullLogger<NntpCommandDispatcher>.Instance);

        Assert.True(NntpCommandParser.TryParse("CHECK <msg@example.com>", out var parsed));
        await dispatcher.DispatchAsync(session, parsed, response, CancellationToken.None);
        Assert.Contains("480 Authentication required", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatcher_TransitWhenAuthenticatedButNotAuthorized_Returns502()
    {
        await using var duplex = await SessionTestDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: true,
            authorizedTransit: false,
            postingPermitted: false,
            streamingPermitted: false));
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher(
            DefaultNntpCommandCatalog.Create(),
            NullLogger<NntpCommandDispatcher>.Instance);

        Assert.True(NntpCommandParser.TryParse("IHAVE <msg@example.com>", out var parsed));
        await dispatcher.DispatchAsync(session, parsed, response, CancellationToken.None);
        Assert.Contains("502 Permission denied", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatcher_ModeStreamWhenAuthenticatedWithoutStreaming_Returns502()
    {
        await using var duplex = await SessionTestDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: true,
            authorizedTransit: true,
            postingPermitted: false,
            streamingPermitted: false));
        var response = new NntpResponseWriter(duplex.ServerOutput);
        var dispatcher = new NntpCommandDispatcher(
            DefaultNntpCommandCatalog.Create(),
            NullLogger<NntpCommandDispatcher>.Instance);

        Assert.True(NntpCommandParser.TryParse("MODE STREAM", out var parsed));
        await dispatcher.DispatchAsync(session, parsed, response, CancellationToken.None);
        Assert.Contains("502 Streaming not permitted", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_GreetingAndQuit_PublicCommands()
    {
        await using var duplex = await SessionTestDuplex.CreateAsync();
        var sessionTask = duplex.CreateSession().RunAsync();

        Assert.StartsWith("201 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("DATE");
        var date = await duplex.ReadClientLineAsync();
        Assert.StartsWith("111 ", date, StringComparison.Ordinal);
        Assert.Equal(18, date.Length); // "111 " + 14 digit stamp

        await duplex.WriteClientLineAsync("MODE READER");
        Assert.StartsWith("201 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);

        await duplex.WriteClientLineAsync("CAPABILITIES");
        Assert.StartsWith("101 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        var caps = new List<string>();
        while (true)
        {
            var line = await duplex.ReadClientLineAsync();
            if (line == ".")
            {
                break;
            }

            caps.Add(line);
        }

        Assert.Contains("VERSION 2", caps);
        Assert.Contains("READER", caps);
        Assert.Contains("AUTHINFO USER", caps);
        Assert.DoesNotContain(caps, static c => c.StartsWith("COMPRESS", StringComparison.Ordinal));

        await duplex.WriteClientLineAsync("QUIT");
        Assert.StartsWith("205 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await sessionTask;
    }

    [Fact]
    public async Task ModeReader_PostingPermitted_Returns200()
    {
        await using var duplex = await SessionTestDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(NntpAuthorization.Unauthenticated.With(postingPermitted: true));
        var sessionTask = session.RunAsync();

        Assert.StartsWith("200 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("MODE READER");
        Assert.StartsWith("200 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await duplex.WriteClientLineAsync("QUIT");
        Assert.StartsWith("205 ", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        await sessionTask;
    }

    private sealed class SessionTestDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        private SessionTestDuplex()
        {
        }

        public PipeWriter ServerOutput => _serverToClient.Writer;
        public PipeReader ServerInput => _clientToServer.Reader;

        public static Task<SessionTestDuplex> CreateAsync() => Task.FromResult(new SessionTestDuplex());

        public NntpSession CreateSession()
        {
            var connection = new PipeNntpConnection(
                ServerInput,
                ServerOutput,
                ConnectionClientIdentity.Direct(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 119)));
            return new NntpSession(connection, NullLogger<NntpSession>.Instance);
        }

        public async Task WriteClientLineAsync(string line)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
            await _clientToServer.Writer.WriteAsync(bytes);
            await _clientToServer.Writer.FlushAsync();
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
        public bool IsCompressed => false;
        public CancellationToken ConnectionClosed => _cts.Token;

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
