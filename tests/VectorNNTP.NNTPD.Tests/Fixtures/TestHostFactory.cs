using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Cloudflare;
using VectorNNTP.NNTPD.Redis;
using VectorNNTP.NNTPD.RabbitMq;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.NNTPD.NntpDb;
using VectorNNTP.NNTPD.Tests.TestDoubles;

namespace VectorNNTP.NNTPD.Tests.Fixtures;

internal static class TestHostFactory
{
    internal static IApplicationService WrapDns(CloudflareDnsReconciliationService service) =>
        new CloudflareDnsReconciliationApplicationService(service);

    public const string TestCloudFlareApiKey = "unit-test-cloudflare-api-key";

    /// <summary>64-hex AES-256 test key for POST <c>X-Trace</c> (not a production secret).</summary>
    public const string TestXTraceKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>Offline placeholder for <c>ConnectionStrings:NntpDB</c> (not a live server).</summary>
    public const string TestNntpDbConnectionString = "Server=127.0.0.1;Database=nntpdb;User ID=test;Pooling=true;MinimumPoolSize=2;MaximumPoolSize=32;ConnectionIdleTimeout=300;";

    /// <summary>
    /// Deliberately malformed NntpDB string similar to the observed parse failure
    /// (unclosed quote; rejected by <c>MySqlConnectionStringBuilder</c>).
    /// </summary>
    public const string MalformedNntpDbConnectionString =
        "Server=127.0.0.1;Port=3306;Database=nntpdb;User ID=nntpd;Pooling=true;MinimumPoolSize=2;MaximumPoolSize=128;ConnectionIdleTimeout=300;Password=\"unclosed";

    /// <summary>Fake password used only in malformed-string secret-redaction tests.</summary>
    public const string FakeNntpDbPassword = "fake-nntpdb-password-not-real";

    /// <summary>Malformed NntpDB string that contains <see cref="FakeNntpDbPassword"/>.</summary>
    public const string MalformedNntpDbConnectionStringWithPassword =
        "Server=127.0.0.1;Database=nntpdb;User ID=nntpd;Password=\"" + FakeNntpDbPassword + ";";

    /// <summary>Shared parent for test file logs so hosts do not write under the repo <c>logs/</c>.</summary>
    public static readonly string SharedTestLogDir = Path.Combine(Path.GetTempPath(), "vectornntp-nntpd-testhost-logs");

    /// <summary>Unique <c>Nntpd:LogDir</c> so parallel hosts do not share a Serilog file lock.</summary>
    public static string NewTestLogDir()
    {
        var dir = Path.Combine(SharedTestLogDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

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
            BindPort = GetFreeTcpPort(),
            BindPortTls = 0,
            CloudFlareApiKey = TestCloudFlareApiKey,
            CloudFlareZoneId = "5811a29d39a0732afb5f160c9b137c3d",
            XTraceKey = TestXTraceKey,
            DnsSuffix = "usenet.ninja",
            ServerId = 1,
        };
    }

    /// <summary>Reserves an ephemeral TCP port on loopback for offline listener tests.</summary>
    public static int GetFreeTcpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
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
                [NntpdOptions.CloudFlareApiKeyConfigurationKey] = TestCloudFlareApiKey,
                [$"{NntpdOptions.SectionName}:{NntpdOptions.XTraceKeyConfigurationKey}"] = TestXTraceKey,
                [NntpdOptions.XTraceKeyConfigurationKey] = TestXTraceKey,
                // Replace appsettings BindAddress entirely (in-memory must clear leftover indices).
                ["BindAddress:0"] = "*",
                ["BindAddress:1"] = null,
                ["Redis:Host:0"] = "127.0.0.1",
                ["Redis:Port"] = "6379",
                ["RabbitMQ:Hosts:0"] = "127.0.0.1",
                [$"ConnectionStrings:{NntpDbOptions.ConnectionStringName}"] = TestNntpDbConnectionString,
                [$"{NntpdOptions.SectionName}:LogDir"] = NewTestLogDir(),
            });

        var localAssignee = assignee ?? new FakeLocalIpAddressAssignee(TestIpv4, TestIpv6);
        builder.Services.AddSingleton<ILocalIpAddressAssignee>(localAssignee);
        builder.Services.AddSingleton<ICloudflareDnsClient>(new FakeCloudflareDnsClient());
        builder.Services.AddSingleton<IRedisConnectionFactory, FakeRedisConnectionFactory>();
        builder.Services.AddSingleton<IRabbitMqConnectionFactory, FakeRabbitMqConnectionFactory>();
        builder.Services.AddSingleton<INntpDbConnectionFactory, FakeNntpDbConnectionFactory>();
        PromoteSharedKeysToRoot(builder.Configuration);
        IsolateTransit(builder.Services);

        // Ensure machine-specific appsettings bind entries cannot leak into host tests.
        // Force TLS off so ACME never contacts Let's Encrypt during offline host tests
        // (committed appsettings may set BindPortTls > 0 for local operator use).
        // Use an ephemeral plain port so NntpPlainListenerService can bind without conflicts.
        builder.Services.PostConfigure<NntpdOptions>(static options =>
        {
            options.BindAddress = ["*"];
            options.BindPort = GetFreeTcpPort();
            options.BindPortTls = 0;
            options.AcmeEmail = string.Empty;
        });
    }

    /// <summary>
    /// Copies leftover <c>Nntpd:</c> shared keys to the configuration root so older
    /// test fixtures keep working after the Common-owned root contract.
    /// </summary>
    public static void PromoteSharedKeysToRoot(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration is not IConfigurationBuilder builder)
        {
            return;
        }

        var extras = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string[] keys =
        [
            NntpdOptions.CloudFlareApiKeyConfigurationKey,
            NntpdOptions.CloudFlareZoneIdConfigurationKey,
            nameof(NntpdOptions.BindPort),
            nameof(NntpdOptions.BindPortTls),
            nameof(NntpdOptions.AcmeEmail),
            nameof(NntpdOptions.AcmeCertificatePassword),
            nameof(NntpdOptions.AcmeStateDir),
            nameof(NntpdOptions.AcmeDirectoryUrl),
            nameof(NntpdOptions.AcmeRenewalThresholdDays),
            nameof(NntpdOptions.DnsSuffix),
        ];

        foreach (var key in keys)
        {
            if (configuration[key] is null && configuration[$"{NntpdOptions.SectionName}:{key}"] is { } value)
            {
                extras[key] = value;
            }
        }

        for (var i = 0; i < 8; i++)
        {
            var destination = $"BindAddress:{i}";
            var source = configuration[$"{NntpdOptions.SectionName}:BindAddress:{i}"];
            if (configuration[destination] is null && source is not null)
            {
                extras[destination] = source;
            }
        }

        if (extras.Count > 0)
        {
            builder.AddInMemoryCollection(extras);
        }
    }

    /// <summary>
    /// Drops operator <c>Transit</c> peers inherited from <c>appsettings.json</c>.
    /// Host tests use deny-by-default Transit so local operator peers do not leak in.
    /// </summary>
    public static void IsolateTransit(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.PostConfigure<TransitPeersOptions>(static options => options.Clear());
    }
}
