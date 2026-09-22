using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.NNTPD.Networking.Certificates;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Networking.Transport;
using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Session.Commands;

namespace VectorNNTP.NNTPD.Tests.Session;

/// <summary>HELP command — static syntax listing (RFC 3977 §7.2), matching pyNNTPD.</summary>
[Collection(nameof(NntpCommandLoggerCollection))]
public sealed class HelpCommandTests
{
    [Fact]
    public async Task Help_Returns100_AndStaticSyntaxLines()
    {
        await using var duplex = await HelpDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var dispatcher = new NntpCommandDispatcher(DefaultNntpCommandCatalog.Create());
        var response = new NntpResponseWriter(duplex.ServerOutput);

        Assert.True(NntpCommandParser.TryParse("HELP", out var parsed));
        var readTask = duplex.ReadMultilineResponseAsync();
        await dispatcher.DispatchAsync(session, parsed, response, CancellationToken.None);
        var (status, body) = await readTask;

        Assert.Equal("100 Help text follows", status);
        Assert.Equal(Help.SyntaxLines, body);
        Assert.DoesNotContain(body, l => l.Contains("BENCHIT", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("NEWGROUPS", body);
        Assert.DoesNotContain("NEWNEWS", body);
    }

    [Fact]
    public async Task Help_IsIdenticalAcrossSessionStates()
    {
        await using var duplex = await HelpDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: true,
            authorizedTransit: true,
            postingPermitted: true,
            streamingPermitted: true));
        session.SetMode(NntpSessionMode.Reader);

        var dispatcher = new NntpCommandDispatcher(DefaultNntpCommandCatalog.Create());
        var response = new NntpResponseWriter(duplex.ServerOutput);

        Assert.True(NntpCommandParser.TryParse("HELP", out var parsed));
        var readTask = duplex.ReadMultilineResponseAsync();
        await dispatcher.DispatchAsync(session, parsed, response, CancellationToken.None);
        var (_, body) = await readTask;

        Assert.Equal(Help.SyntaxLines, body);
    }

    [Fact]
    public async Task Help_TrailingArgs_Returns501()
    {
        await using var duplex = await HelpDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var dispatcher = new NntpCommandDispatcher(DefaultNntpCommandCatalog.Create());
        var response = new NntpResponseWriter(duplex.ServerOutput);

        Assert.True(NntpCommandParser.TryParse("HELP MORE", out var parsed));
        await dispatcher.DispatchAsync(session, parsed, response, CancellationToken.None);
        Assert.Equal("501 Syntax error", await duplex.ReadClientLineAsync());
    }

    [Fact]
    public async Task Help_SessionRemainsOpen_ForSubsequentCommand()
    {
        await using var duplex = await HelpDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var run = session.RunAsync();
        _ = await duplex.ReadClientLineAsync(); // greeting

        await duplex.WriteClientLineAsync("HELP");
        Assert.Equal("100 Help text follows", await duplex.ReadClientLineAsync());
        var body = await duplex.ReadMultilineBodyAsync();
        Assert.Equal(Help.SyntaxLines, body);

        await duplex.WriteClientLineAsync("DATE");
        var date = await duplex.ReadClientLineAsync();
        Assert.StartsWith("111 ", date, StringComparison.Ordinal);
        Assert.Equal(14, date.Length - 4);

        await duplex.WriteClientLineAsync("QUIT");
        _ = await duplex.ReadClientLineAsync();
        await run;
    }

    [Fact]
    public void SyntaxLines_AreDeterministicAndExcludeBenchIt()
    {
        Assert.Equal(Help.SyntaxLines, Help.SyntaxLines.ToArray());
        Assert.Contains("HELP", Help.SyntaxLines);
        Assert.Contains("COMPRESS DEFLATE", Help.SyntaxLines);
        Assert.DoesNotContain(Help.SyntaxLines, l => l.Contains("BENCHIT", StringComparison.OrdinalIgnoreCase));
        // Ordered as in pyNNTPD help_model (stable readable grouping).
        Assert.Equal("ARTICLE {message-id | article-number}", Help.SyntaxLines[0]);
        Assert.Equal("TAKETHIS [message-id]", Help.SyntaxLines[^1]);
    }

    private sealed class HelpDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public PipeWriter ServerOutput => _serverToClient.Writer;

        public static Task<HelpDuplex> CreateAsync() => Task.FromResult(new HelpDuplex());

        public NntpSession CreateSession()
        {
            var connection = new PipeNntpConnection(
                _clientToServer.Reader,
                _serverToClient.Writer,
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

        public async Task<List<string>> ReadMultilineBodyAsync()
        {
            var lines = new List<string>();
            while (true)
            {
                var line = await ReadClientLineAsync();
                if (line == ".")
                {
                    break;
                }

                lines.Add(line);
            }

            return lines;
        }

        public async Task<(string Status, List<string> Body)> ReadMultilineResponseAsync()
        {
            // Caller must start this before DispatchAsync so FlushAsync backpressure can drain.
            var status = await ReadClientLineAsync().ConfigureAwait(false);
            var body = await ReadMultilineBodyAsync().ConfigureAwait(false);
            return (status, body);
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
        private int _compressed;

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

        public bool IsCompressed => Volatile.Read(ref _compressed) == 1;
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

        public Task UpgradeToDeflateAsync(CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _compressed, 1);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
