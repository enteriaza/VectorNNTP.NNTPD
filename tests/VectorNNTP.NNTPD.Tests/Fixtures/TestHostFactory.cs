using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Cloudflare;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.NNTPD.Tests.TestDoubles;

namespace VectorNNTP.NNTPD.Tests.Fixtures;

internal static class TestHostFactory
{
    public const string TestCloudFlareApiKey = "unit-test-cloudflare-api-key";

    public static readonly IPAddress TestIpv4 = IPAddress.Parse("198.18.0.10");
    public static readonly IPAddress TestIpv6 = IPAddress.Parse("2001:db8::10");

    public static NntpdOptions CreateOptions(
        TimeSpan? gracefulShutdownTimeout = null,
        TimeSpan? startupTimeout = null)
    {
        return CreateValidOptions(gracefulShutdownTimeout, startupTimeout);
    }

    public static NntpdOptions CreateValidOptions(
        TimeSpan? gracefulShutdownTimeout = null,
        TimeSpan? startupTimeout = null)
    {
        return new NntpdOptions
        {
            ApplicationName = "VectorNNTP.NNTPD.Tests",
            GracefulShutdownTimeout = gracefulShutdownTimeout ?? TimeSpan.FromSeconds(5),
            StartupTimeout = startupTimeout,
            StopHostOnUnexpectedServiceTermination = true,
            BindAddress = ["*"],
            BindPort = 119,
            BindPortTls = 0,
            CloudFlareApiKey = TestCloudFlareApiKey,
            CloudFlareZoneId = "5811a29d39a0732afb5f160c9b137c3d",
            DnsSuffix = "usenet.ninja",
            ServerId = 1,
        };
    }

    public static ApplicationServiceManager CreateServiceManager(
        IEnumerable<IApplicationService> services,
        NntpdOptions? options = null)
    {
        return new ApplicationServiceManager(
            services,
            Options.Create(options ?? CreateOptions()),
            NullLogger<ApplicationServiceManager>.Instance);
    }

    public static ApplicationLifecycle CreateLifecycle(
        IEnumerable<IApplicationService> services,
        NntpdOptions? options = null)
    {
        options ??= CreateOptions();
        var manager = CreateServiceManager(services, options);
        return new ApplicationLifecycle(
            manager,
            Options.Create(options),
            NullLogger<ApplicationLifecycle>.Instance);
    }

    /// <summary>
    /// Registers deterministic networking and Cloudflare test configuration before hosting services.
    /// </summary>
    public static void ConfigureNntpdTestHost(HostApplicationBuilder builder, ILocalIpAddressAssignee? assignee = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [$"{NntpdOptions.SectionName}:{NntpdOptions.CloudFlareApiKeyConfigurationKey}"] = TestCloudFlareApiKey,
                // Replace appsettings BindAddress entirely (in-memory must clear leftover indices).
                [$"{NntpdOptions.SectionName}:BindAddress:0"] = "*",
                [$"{NntpdOptions.SectionName}:BindAddress:1"] = null,
            });

        var localAssignee = assignee ?? new FakeLocalIpAddressAssignee(TestIpv4, TestIpv6);
        builder.Services.AddSingleton<ILocalIpAddressAssignee>(localAssignee);
        builder.Services.AddSingleton<ICloudflareDnsClient>(new FakeCloudflareDnsClient());

        // Ensure machine-specific appsettings bind entries cannot leak into host tests.
        builder.Services.PostConfigure<NntpdOptions>(static options =>
        {
            options.BindAddress = ["*"];
        });
    }
}
