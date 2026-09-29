using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.Networking.Certificates;
using VectorNNTP.StorageServer.Configuration;
using VectorNNTP.StorageServer.Listener;
using VectorNNTP.StorageServer.Tests.Fixtures;
using VectorNNTP.StorageServer.Tests.TestDoubles;

namespace VectorNNTP.StorageServer.Tests.Listener;

public sealed class StorageVatpListenerServiceTests
{
    [Fact]
    public async Task Listener_refuses_start_without_acme_readiness()
    {
        var runtime = CreateRuntime(StorageServerTestOptions.GetFreeTcpPort());
        var readiness = new AcmeCertificateReadiness();
        await using var service = new StorageVatpListenerService(
            runtime,
            TestListenerCertificates.CreatePublishedProvider(),
            NullStorageArticleOpenBoundary.Instance,
            readiness,
            NullLogger<StorageVatpListenerService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.Contains("ACME certificate is not ready", ex.Message, StringComparison.Ordinal);
        Assert.Equal(StorageVatpListenerState.Stopped, service.State);
        Assert.Empty(service.LocalEndPoints);
    }

    [Fact]
    public async Task Listener_refuses_start_without_certificate()
    {
        var runtime = CreateRuntime(StorageServerTestOptions.GetFreeTcpPort());
        var readiness = new AcmeCertificateReadiness();
        readiness.MarkReady();
        await using var service = new StorageVatpListenerService(
            runtime,
            TestListenerCertificates.CreateUnavailableProvider(),
            NullStorageArticleOpenBoundary.Instance,
            readiness,
            NullLogger<StorageVatpListenerService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.Contains("no TLS certificate", ex.Message, StringComparison.Ordinal);
        Assert.Equal(StorageVatpListenerState.Stopped, service.State);
    }

    [Fact]
    public async Task Listener_starts_when_ready_and_shuts_down_cleanly()
    {
        var port = StorageServerTestOptions.GetFreeTcpPort();
        var runtime = CreateRuntime(port);
        var readiness = new AcmeCertificateReadiness();
        readiness.MarkReady();
        await using var service = new StorageVatpListenerService(
            runtime,
            TestListenerCertificates.CreatePublishedProvider(),
            NullStorageArticleOpenBoundary.Instance,
            readiness,
            NullLogger<StorageVatpListenerService>.Instance);

        await service.StartAsync(CancellationToken.None);
        Assert.Equal(StorageVatpListenerState.Running, service.State);
        Assert.All(service.LocalEndPoints, endpoint =>
        {
            var ip = Assert.IsType<IPEndPoint>(endpoint);
            Assert.Equal(port, ip.Port);
        });

        await service.StopAsync(CancellationToken.None);
        Assert.Equal(StorageVatpListenerState.Stopped, service.State);
        await service.DisposeAsync();
        Assert.Equal(StorageVatpListenerState.Stopped, service.State);
    }

    [Fact]
    public async Task Existing_connection_keeps_certificate_lease_after_rotation()
    {
        var port = StorageServerTestOptions.GetFreeTcpPort();
        var runtime = CreateRuntime(port);
        var certificates = TestListenerCertificates.CreatePublishedProvider("cache-original.test");
        var readiness = new AcmeCertificateReadiness();
        readiness.MarkReady();
        await using var service = new StorageVatpListenerService(
            runtime,
            certificates,
            NullStorageArticleOpenBoundary.Instance,
            readiness,
            NullLogger<StorageVatpListenerService>.Instance);

        await service.StartAsync(CancellationToken.None);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            },
            CancellationToken.None);

        Assert.Contains("cache-original.test", ssl.RemoteCertificate!.Subject, StringComparison.OrdinalIgnoreCase);

        using var renewed = TestListenerCertificates.CreateSelfSigned("cache-renewed.test");
        certificates.PublishFromPfx(renewed.Export(X509ContentType.Pfx, "test"), "test");

        Assert.Contains("cache-original.test", ssl.RemoteCertificate.Subject, StringComparison.OrdinalIgnoreCase);

        using var second = new TcpClient();
        await second.ConnectAsync(IPAddress.Loopback, port);
        await using var ssl2 = new SslStream(second.GetStream(), leaveInnerStreamOpen: false);
        await ssl2.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            },
            CancellationToken.None);
        Assert.Contains("cache-renewed.test", ssl2.RemoteCertificate!.Subject, StringComparison.OrdinalIgnoreCase);

        await service.StopAsync(CancellationToken.None);
    }

    private static StorageServerRuntimeOptions CreateRuntime(int tlsPort)
    {
        var identity = StorageServerTestOptions.CreateValid();
        identity.BindAddress = ["127.0.0.1"];
        identity.BindPortTls = tlsPort;
        var acme = StorageServerTestOptions.CreateValidAcme(identity);
        acme.BindAddress = ["127.0.0.1"];
        acme.BindPortTls = tlsPort;
        return StorageServerRuntimeOptionsFactory.Create(identity, acme);
    }
}
