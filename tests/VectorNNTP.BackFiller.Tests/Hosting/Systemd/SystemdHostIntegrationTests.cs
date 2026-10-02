using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using VectorNNTP.BackFiller.Accounts;
using VectorNNTP.BackFiller.Acme;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.BackFiller.Hosting.Systemd;
using VectorNNTP.BackFiller.Logging;
using VectorNNTP.BackFiller.RabbitMq;
using VectorNNTP.NNTPD.Cloudflare;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;

using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.BackFiller.Tests.Hosting.Systemd;

public sealed class SystemdHostIntegrationTests
{
    [Fact]
    public async Task Host_EmitsReadyAfterSuccessfulStart_AndStoppingOnShutdown()
    {
        var notify = new FakeSystemdNotifyBridge();
        using var host = CreateHost(notify);

        Assert.Equal(0, notify.ReadyCount);
        await host.StartAsync();
        try
        {
            Assert.Equal(1, notify.ReadyCount);
            Assert.True(host.Services.GetRequiredService<BackFillerApplicationHealth>().IsHealthyForWatchdog);
            Assert.Contains(notify.Notifications, n => n.Contains("Running", StringComparison.Ordinal));
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.Equal(1, notify.StoppingCount);
        Assert.False(host.Services.GetRequiredService<BackFillerApplicationHealth>().IsHealthyForWatchdog);
    }

    [Fact]
    public async Task Host_StartupFailure_DoesNotEmitReady()
    {
        var notify = new FakeSystemdNotifyBridge();
        using var host = CreateHost(notify, failApplicationService: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Equal(0, notify.ReadyCount);
    }

    [Fact]
    public async Task Host_OutsideSystemd_DoesNotEmitNotifications()
    {
        var notify = new FakeSystemdNotifyBridge { IsEnabled = false };
        using var host = CreateHost(notify);

        await host.StartAsync();
        try
        {
            Assert.Empty(notify.Notifications);
            Assert.Equal(0, notify.ReadyCount);
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.Equal(0, notify.StoppingCount);
    }

    private static IHost CreateHost(FakeSystemdNotifyBridge notify, bool failApplicationService = false)
    {
        var builder = Host.CreateApplicationBuilder([]);
        var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
        var tlsPort = GetFreePort();
        pairs["BackFiller:BindPortTls"] = tlsPort.ToString();
        pairs["BackFiller:BindAddress:0"] = "*";
        pairs["BackFiller:Logging:File:LogDir"] = Directory.CreateTempSubdirectory("bf-systemd-logs-").FullName;
        builder.Configuration.AddInMemoryCollection(pairs);
        builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        builder.Services.AddSingleton<ICloudflareDnsReconciler>(new NoOpCloudflareDnsReconciler());
        builder.Services.AddSingleton<IPhysicalMemoryProvider>(new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
        builder.Services.AddSingleton<IRabbitMqConnectionFactory>(new FakeBackFillerRabbitMqConnectionFactory());
        builder.Services.AddSingleton<IProviderAccountSource>(new FakeProviderAccountSource());

        builder.ConfigureBackFillerLogging();
        builder.ConfigureBackFillerPlatformHosting();
        builder.AddBackFillerHosting();

        builder.Services.RemoveAll<ISystemdNotifyBridge>();
        builder.Services.AddSingleton<ISystemdNotifyBridge>(notify);
        builder.Services.RemoveAll<ISystemdRuntime>();
        builder.Services.AddSingleton<ISystemdRuntime>(RecordingSystemdRuntimeFactory.LinuxUnderSystemd());

        ReplaceAcmeApplicationService(builder.Services, failApplicationService);

        return builder.Build();
    }

    private static void ReplaceAcmeApplicationService(IServiceCollection services, bool fail)
    {
        for (var i = 0; i < services.Count; i++)
        {
            if (services[i].ServiceType == typeof(IApplicationService)
                && services[i].ImplementationType == typeof(AcmeCertificateApplicationService))
            {
                services[i] = fail
                    ? ServiceDescriptor.Singleton<IApplicationService, FailingAcmeApplicationService>()
                    : ServiceDescriptor.Singleton<IApplicationService, ImmediateAcmeReadyApplicationService>();
            }
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

internal sealed class FailingAcmeApplicationService : IApplicationService
{
    public string Name => "FailingAcme";

    public Task? Execution => null;

    public Task StartAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("ACME startup failed");

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
