using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Listener;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.Retention;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.Listener;

public sealed class CacheListenerServiceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Starts_binds_ipv4_and_repeated_dispose_is_safe()
    {
        await using var context = await ListenerContext.StartAsync();

        Assert.Equal(CacheListenerState.Running, context.Service.State);
        Assert.Contains(context.Service.LocalEndPoints, static endpoint => endpoint is IPEndPoint ip && ip.Address.Equals(IPAddress.Loopback));

        await context.Service.DisposeAsync();
        await context.Service.DisposeAsync();
        Assert.Equal(CacheListenerState.Stopped, context.Service.State);
    }

    [Fact]
    public async Task Startup_fails_when_the_certificate_is_missing()
    {
        var runtime = CreateRuntime(GetFreePort());
        var service = new CacheListenerService(
            runtime,
            new StaticCacheListenerCertificateSource(available: false),
            ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024),
            NullLogger<CacheListenerService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.Equal(CacheListenerState.Stopped, service.State);
    }

    [Fact]
    public async Task Tls_client_can_fetch_the_exact_retained_payload()
    {
        var payload = new byte[] { 0x00, 0x0A, 0x0D, 0xFF, (byte)'Z' };
        await using var context = await ListenerContext.StartAsync(payload);
        var md5 = ArticleIdentity.FromExactMessageId(ArticleWorkTestDeliveries.CanonicalMessageId).Md5Hex;

        await using var client = await ConnectAsync(context);
        await client.Stream.WriteAsync(ListenerProtocolEncoder.EncodeGetRequest(21, md5));
        var found = await ReadFrameAsync(client.Stream);
        Assert.Equal(ListenerOpcode.GetResponseFound, found.Header.Opcode);
        Assert.Equal(payload, found.Payload);
        await client.Stream.WriteAsync(ListenerProtocolEncoder.EncodeGetReceiptAck(21));
    }

    [Fact]
    public async Task Handshake_failure_does_not_fault_the_listener()
    {
        await using var context = await ListenerContext.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, context.Port);
        tcp.GetStream().WriteByte(0x16);
        tcp.Close();
        await ArticleWorkTestDeliveries.WaitUntilAsync(
            () => context.Service.State == CacheListenerState.Running && context.Service.ActiveConnections == 0,
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Max_connections_rejects_additional_clients()
    {
        var runtime = CreateRuntime(GetFreePort(), maxConnections: 1);
        await using var context = await ListenerContext.StartAsync(runtime: runtime);
        await using var held = await ConnectAsync(context);
        using var overflow = new TcpClient();
        await overflow.ConnectAsync(IPAddress.Loopback, context.Port);
        await ArticleWorkTestDeliveries.WaitUntilAsync(
            () => !overflow.Connected || overflow.Available >= 0,
            TimeSpan.FromSeconds(2));
        Assert.Equal(1, context.Service.ActiveConnections);
    }

    [Fact]
    public async Task Shutdown_during_handshake_is_safe()
    {
        await using var context = await ListenerContext.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, context.Port);
        await context.Service.DisposeAsync();
        Assert.Equal(CacheListenerState.Stopped, context.Service.State);
    }

    [Fact]
    public async Task Handshake_timeout_releases_the_connection_slot()
    {
        var options = BackFillerTestOptions.CreateValid();
        options.BindPort = GetFreePort();
        options.BindAddress = ["127.0.0.1"];
        options.Listener.TlsHandshakeTimeoutSeconds = 1;
        var runtime = BackFillerRuntimeOptionsFactory.Create(
            options,
            BackFillerTestOptions.CreateValidConnectionStrings());
        runtime = runtime with
        {
            Listener = runtime.Listener with { TlsHandshakeTimeout = TimeSpan.FromMilliseconds(80) },
        };

        await using var context = await ListenerContext.StartAsync(runtime: runtime);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, context.Port);
        await ArticleWorkTestDeliveries.WaitUntilAsync(
            () => context.Service.ActiveConnections == 0,
            TimeSpan.FromSeconds(2));
        Assert.Equal(CacheListenerState.Running, context.Service.State);
    }

    [Fact]
    public async Task Io_timeout_closes_an_idle_authenticated_connection()
    {
        var options = BackFillerTestOptions.CreateValid();
        options.BindPort = GetFreePort();
        options.BindAddress = ["127.0.0.1"];
        var runtime = BackFillerRuntimeOptionsFactory.Create(
            options,
            BackFillerTestOptions.CreateValidConnectionStrings());
        runtime = runtime with
        {
            Listener = runtime.Listener with { IoProgressTimeout = TimeSpan.FromMilliseconds(80) },
        };

        await using var context = await ListenerContext.StartAsync(runtime: runtime);
        await using var client = await ConnectAsync(context);
        await ArticleWorkTestDeliveries.WaitUntilAsync(
            () => context.Service.ActiveConnections == 0,
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Wildcard_bind_listens_on_ipv4_any()
    {
        var options = BackFillerTestOptions.CreateValid();
        options.BindPort = GetFreePort();
        options.BindAddress = ["*"];
        var runtime = BackFillerRuntimeOptionsFactory.Create(
            options,
            BackFillerTestOptions.CreateValidConnectionStrings());

        await using var service = new CacheListenerService(
            runtime,
            new StaticCacheListenerCertificateSource(),
            ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024),
            NullLogger<CacheListenerService>.Instance);
        await service.StartAsync(CancellationToken.None);
        Assert.Contains(
            service.LocalEndPoints,
            static endpoint => endpoint is IPEndPoint ip && ip.Address.Equals(IPAddress.IPv6Any));
        await service.DisposeAsync();
        Assert.Equal(CacheListenerState.Stopped, service.State);
    }

    [Fact]
    public async Task Stream_transport_enforces_io_progress_timeout()
    {
        await using var hanging = new HangingStream();
        var transport = new StreamCacheListenerTransport(hanging, TimeSpan.FromMilliseconds(40), leaveInnerStreamOpen: true);
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            var buffer = new byte[8];
            _ = await transport.ReadAsync(buffer, CancellationToken.None);
        });
    }

    [Fact]
    public async Task Tls12_client_can_complete_handshake_and_fetch()
    {
        await AssertHandshakeProtocolAsync(SslProtocols.Tls12);
    }

    [Fact]
    public async Task Tls13_client_can_complete_handshake_when_the_platform_supports_it()
    {
        try
        {
            await AssertHandshakeProtocolAsync(SslProtocols.Tls13);
        }
        catch (Exception ex) when (ex is AuthenticationException or PlatformNotSupportedException)
        {
            output.WriteLine($"SKIP: TLS 1.3 is not available for SslStream on this platform/runtime ({ex.GetType().Name}).");
            return;
        }
    }

    [Fact]
    public async Task Malformed_binary_frame_closes_the_authenticated_connection()
    {
        await using var context = await ListenerContext.StartAsync();
        await using var client = await ConnectAsync(context);
        var frame = ListenerProtocolEncoder.EncodeGetReceiptAck(4);
        frame[0] = 0x02;
        await client.Stream.WriteAsync(frame);
        await ArticleWorkTestDeliveries.WaitUntilAsync(
            () => context.Service.State == CacheListenerState.Running && context.Service.ActiveConnections == 0,
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Explicit_ipv6_loopback_binds_when_the_platform_supports_it()
    {
        if (!Socket.OSSupportsIPv6)
        {
            output.WriteLine("SKIP: Socket.OSSupportsIPv6 is false on this platform.");
            return;
        }

        var port = GetFreePort();
        var runtime = CreateRuntime(port) with
        {
            BindAddressTokens = ["::1"],
            CanonicalBindAddresses = [IPAddress.IPv6Loopback],
        };

        CacheListenerService? service = null;
        try
        {
            service = new CacheListenerService(
                runtime,
                new StaticCacheListenerCertificateSource(),
                ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024),
                NullLogger<CacheListenerService>.Instance);
            await service.StartAsync(CancellationToken.None);
            Assert.Contains(
                service.LocalEndPoints,
                static endpoint => endpoint is IPEndPoint ip && ip.Address.Equals(IPAddress.IPv6Loopback));
        }
        catch (SocketException ex) when (
            ex.SocketErrorCode is SocketError.AddressFamilyNotSupported
                or SocketError.ProtocolNotSupported
                or SocketError.AddressNotAvailable)
        {
            output.WriteLine($"SKIP: IPv6 loopback bind is not available ({ex.SocketErrorCode}).");
            return;
        }
        finally
        {
            if (service is not null)
            {
                await service.DisposeAsync();
            }
        }
    }

    [Fact]
    public void Production_listener_code_does_not_block_synchronously()
    {
        var directory = FindListenerSourceDirectory();
        foreach (var file in Directory.GetFiles(directory, "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Thread.Sleep", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".GetAwaiter().GetResult()", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".Wait();", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".Result;", text, StringComparison.Ordinal);
        }
    }

    private static async Task AssertHandshakeProtocolAsync(SslProtocols protocol)
    {
        var payload = "tls-protocol"u8.ToArray();
        await using var context = await ListenerContext.StartAsync(payload);
        var md5 = ArticleIdentity.FromExactMessageId(ArticleWorkTestDeliveries.CanonicalMessageId).Md5Hex;
        await using var client = await ConnectAsync(context, protocol);
        Assert.Equal(protocol, client.Stream.SslProtocol);
        await client.Stream.WriteAsync(ListenerProtocolEncoder.EncodeGetRequest(21, md5));
        var found = await ReadFrameAsync(client.Stream);
        Assert.Equal(ListenerOpcode.GetResponseFound, found.Header.Opcode);
        Assert.Equal(payload, found.Payload);
        await client.Stream.WriteAsync(ListenerProtocolEncoder.EncodeGetReceiptAck(21));
    }

    private static async Task<TlsClient> ConnectAsync(
        ListenerContext context,
        SslProtocols protocols = SslProtocols.Tls12 | SslProtocols.Tls13)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, context.Port);
        var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, static (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            EnabledSslProtocols = protocols,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        });
        return new TlsClient(tcp, ssl);
    }

    private static async Task<(ListenerFrameHeader Header, byte[] Payload)> ReadFrameAsync(Stream stream)
    {
        var headerBytes = new byte[ListenerProtocol.HeaderLengthBytes];
        await stream.ReadExactlyAsync(headerBytes);
        var header = ListenerFrameHeader.ReadFrom(headerBytes);
        var payload = new byte[header.PayloadLength];
        if (payload.Length > 0)
        {
            await stream.ReadExactlyAsync(payload);
        }

        return (header, payload);
    }

    private static BackFillerRuntimeOptions CreateRuntime(int port, int maxConnections = 8)
    {
        var options = BackFillerTestOptions.CreateValid();
        options.BindPort = port;
        options.BindAddress = ["127.0.0.1"];
        var runtime = BackFillerRuntimeOptionsFactory.Create(
            options,
            BackFillerTestOptions.CreateValidConnectionStrings());
        return runtime with
        {
            Listener = runtime.Listener with { MaxActiveConnections = maxConnections },
        };
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindListenerSourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "VectorNNTP.BackFiller", "Listener");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/VectorNNTP.BackFiller/Listener.");
    }

    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class TlsClient(TcpClient tcp, SslStream stream) : IAsyncDisposable
    {
        public SslStream Stream { get; } = stream;

        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync();
            tcp.Dispose();
        }
    }

    private sealed class ListenerContext : IAsyncDisposable
    {
        private ListenerContext(CacheListenerService service, int port)
        {
            Service = service;
            Port = port;
        }

        public CacheListenerService Service { get; }

        public int Port { get; }

        public static async Task<ListenerContext> StartAsync(
            byte[]? payload = null,
            BackFillerRuntimeOptions? runtime = null)
        {
            runtime ??= CreateRuntime(GetFreePort());
            var authority = ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024 * 1024);
            if (payload is not null)
            {
                authority.Retain(ArticleWorkTestDeliveries.CanonicalMessageId, payload);
            }

            var service = new CacheListenerService(
                runtime,
                new StaticCacheListenerCertificateSource(),
                authority,
                NullLogger<CacheListenerService>.Instance);
            await service.StartAsync(CancellationToken.None);
            return new ListenerContext(service, runtime.BindPort);
        }

        public async ValueTask DisposeAsync() => await Service.DisposeAsync();
    }
}
