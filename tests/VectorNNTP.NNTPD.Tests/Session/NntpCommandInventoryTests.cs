using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.NNTPD.Networking.Certificates;
using VectorNNTP.NNTPD.Networking.Proxy;
using VectorNNTP.NNTPD.Networking.Transport;
using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Session.Commands;

namespace VectorNNTP.NNTPD.Tests.Session;

public sealed class NntpCommandInventoryTests
{
    [Fact]
    public void Catalog_RegistersCompleteInventory()
    {
        var registry = DefaultNntpCommandCatalog.Create();
        var keys = registry.GetRegisteredKeys();

        // Production inventory remains complete; BENCHIT is an internal extra (not in InventoryKeys).
        foreach (var key in DefaultNntpCommandCatalog.InventoryKeys)
        {
            Assert.Contains(key, keys);
        }

        Assert.Contains("BENCHIT", keys);
        Assert.DoesNotContain("BENCHIT", DefaultNntpCommandCatalog.InventoryKeys);
        Assert.Equal(DefaultNntpCommandCatalog.InventoryKeys.Count + 1, keys.Count);
    }

    [Theory]
    [InlineData("CAPABILITIES")]
    [InlineData("DATE")]
    [InlineData("HELP")]
    [InlineData("QUIT")]
    [InlineData("STARTTLS")]
    [InlineData("LIST")]
    [InlineData("GROUP")]
    [InlineData("ARTICLE")]
    [InlineData("NEWGROUPS")]
    [InlineData("NEWNEWS")]
    [InlineData("COMPRESS DEFLATE")]
    [InlineData("MODE READER")]
    [InlineData("MODE STREAM")]
    [InlineData("AUTHINFO USER")]
    [InlineData("AUTHINFO PASS")]
    [InlineData("AUTHINFO SASL")]
    [InlineData("IHAVE")]
    [InlineData("CHECK")]
    [InlineData("TAKETHIS")]
    public void Registry_ResolvesInventoryCommands(string commandLine)
    {
        var registry = DefaultNntpCommandCatalog.Create();
        Assert.True(NntpCommandParser.TryParse(commandLine, out var parsed));
        Assert.True(registry.TryResolve(parsed, out var descriptor, out _, out var status));
        Assert.Equal(NntpCommandResolveStatus.Found, status);
        Assert.NotNull(descriptor);
    }

    [Fact]
    public void Registry_ExactMatch_RejectsPrefixConfusion()
    {
        var registry = DefaultNntpCommandCatalog.Create();

        Assert.True(NntpCommandParser.TryParse("ARTICLEX", out var articleX));
        Assert.False(registry.TryResolve(articleX, out _, out _, out var unknown));
        Assert.Equal(NntpCommandResolveStatus.UnknownCommand, unknown);

        Assert.True(NntpCommandParser.TryParse("AUTHINFO USERXYZ", out var userXyz));
        Assert.False(registry.TryResolve(userXyz, out _, out _, out var unknownSub));
        Assert.Equal(NntpCommandResolveStatus.UnknownSubcommand, unknownSub);
    }

    [Fact]
    public void Registry_TryResolve_Found_DescriptorIsNonNull()
    {
        var registry = DefaultNntpCommandCatalog.Create();
        Assert.True(NntpCommandParser.TryParse("DATE", out var parsed));
        Assert.True(registry.TryResolve(parsed, out var descriptor, out var args, out var status));
        Assert.Equal(NntpCommandResolveStatus.Found, status);
        Assert.NotNull(descriptor);
        Assert.Equal("DATE", descriptor.RegistryKey);
        Assert.Empty(args);
    }

    [Fact]
    public async Task Placeholder_List_RemainsRecognizedButReturns500AfterAuthz()
    {
        await using var duplex = await InventoryDuplex.CreateAsync();
        var session = duplex.CreateSession();
        session.SetAuthorization(new NntpAuthorization(
            isAuthenticated: true,
            authorizedReader: true,
            authorizedTransit: false,
            postingPermitted: false,
            streamingPermitted: false));

        var dispatcher = new NntpCommandDispatcher(
            DefaultNntpCommandCatalog.Create());
        var response = new NntpResponseWriter(duplex.ServerOutput);

        Assert.True(NntpCommandParser.TryParse("LIST", out var parsed));
        await dispatcher.DispatchAsync(session, parsed, response, CancellationToken.None);
        Assert.Contains("500 Command not implemented", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Placeholder_List_WithoutAuth_StillReturns480()
    {
        await using var duplex = await InventoryDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var dispatcher = new NntpCommandDispatcher(
            DefaultNntpCommandCatalog.Create());
        var response = new NntpResponseWriter(duplex.ServerOutput);

        Assert.True(NntpCommandParser.TryParse("LIST", out var parsed));
        await dispatcher.DispatchAsync(session, parsed, response, CancellationToken.None);
        Assert.Contains("480 Authentication required", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompressDeflate_IsRegisteredPublic_Returns206()
    {
        await using var duplex = await InventoryDuplex.CreateAsync();
        var session = duplex.CreateSession();
        var dispatcher = new NntpCommandDispatcher(
            DefaultNntpCommandCatalog.Create());
        var response = new NntpResponseWriter(duplex.ServerOutput);

        Assert.True(NntpCommandParser.TryParse("COMPRESS DEFLATE", out var parsed));
        await dispatcher.DispatchAsync(session, parsed, response, CancellationToken.None);
        Assert.Contains("206 Compression active", await duplex.ReadClientLineAsync(), StringComparison.Ordinal);
        Assert.True(session.Connection.IsCompressed);
    }

    private sealed class InventoryDuplex : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new(NntpPipeOptions.Create());
        private readonly Pipe _serverToClient = new(NntpPipeOptions.Create());

        public PipeWriter ServerOutput => _serverToClient.Writer;

        public static Task<InventoryDuplex> CreateAsync() => Task.FromResult(new InventoryDuplex());

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

        public bool IsCompressed => Volatile.Read(ref _compressed) == 1;
        public CancellationToken ConnectionClosed => _cts.Token;
        public bool IsCompleted => _cts.IsCancellationRequested;
        public long OutboundIdleVersion => 0;

        public Task PauseReadsAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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

        private int _compressed;
    }
}
