using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.Configuration;

using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.BackFiller.Tests.Configuration;

/// <summary>
/// Canonical BackFiller FQDN is <c>backfiller{ServerId:00}.{DnsSuffix}</c>.
/// </summary>
public sealed class BackFillerCanonicalIdentityTests
{
    [Fact]
    public void Application_prefix_is_fixed_and_name_is_not_configuration()
    {
        Assert.Equal("backfiller", BackFillerOptions.ApplicationPrefix);
        Assert.Null(typeof(BackFillerOptions).GetProperty("Name"));
        Assert.Null(typeof(BackFillerOptions).GetField(
            "NameEnvironmentVariable",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
        Assert.Null(typeof(BackFillerRuntimeOptions).GetProperty("Name"));
    }

    [Fact]
    public void TransitServer_is_not_part_of_BackFiller_configuration()
    {
        Assert.Null(typeof(BackFillerOptions).GetProperty("TransitServer"));
        Assert.Null(typeof(BackFillerRuntimeOptions).GetProperty("TransitServer"));
        Assert.Null(typeof(BackFillerOptions).Assembly.GetType(
            "VectorNNTP.BackFiller.Configuration.BackFillerTransitServerOptions"));
        Assert.Null(typeof(BackFillerRuntimeOptions).Assembly.GetType(
            "VectorNNTP.BackFiller.Configuration.BackFillerTransitServerRuntimeOptions"));
    }

    [Theory]
    [InlineData(1, "backfiller01.usenet.ninja")]
    [InlineData(8, "backfiller08.usenet.ninja")]
    [InlineData(99, "backfiller99.usenet.ninja")]
    public void Canonical_fqdn_comes_from_shared_builder(int serverId, string expected)
    {
        var options = BackFillerTestOptions.CreateValid();
        options.ServerId = serverId;
        options.DnsSuffix = "usenet.ninja";

        Assert.Equal(expected, options.Fqdn);
        Assert.Equal(
            ApplicationFqdn.Build(BackFillerOptions.ApplicationPrefix, serverId, "usenet.ninja"),
            options.Fqdn);
        Assert.NotEqual(
            ApplicationFqdn.Build("nntpd", serverId, "usenet.ninja"),
            options.Fqdn);
    }

    [Fact]
    public void Leftover_name_configuration_does_not_change_fqdn()
    {
        var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
        pairs["BackFiller:Name"] = "cache";
        pairs["BackFiller:ServerId"] = "1";
        pairs["BackFiller:DnsSuffix"] = "usenet.ninja";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(pairs)
            .Build();
        var options = new BackFillerOptions();
        configuration.GetSection(BackFillerOptions.SectionName).Bind(options);

        Assert.Equal("backfiller01.usenet.ninja", options.Fqdn);
        Assert.NotEqual("cache01.usenet.ninja", options.Fqdn);
    }

    [Fact]
    public void Host_acme_cloudflare_and_storage_use_the_canonical_fqdn()
    {
        var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
        pairs["BackFiller:ServerId"] = "8";

        using var host = CreateHost(pairs);
        var identity = host.Services.GetRequiredService<IOptions<BackFillerOptions>>().Value;
        var acme = host.Services.GetRequiredService<IOptions<AcmeCloudflareOptions>>().Value;
        var runtime = host.Services.GetRequiredService<BackFillerRuntimeOptions>();
        const string expected = "backfiller08.usenet.ninja";

        Assert.Equal(expected, identity.Fqdn);
        Assert.Equal(expected, acme.Fqdn);
        Assert.Equal(expected, runtime.Fqdn);
        Assert.Equal(identity.Fqdn, acme.Fqdn);
        Assert.Equal(identity.Fqdn, runtime.Fqdn);
        Assert.False(acme.IncludeNewsHostnameInCertificate);
        Assert.Equal([expected], runtime.CertificateDomainNames);
        Assert.DoesNotContain(CertificateIdentities.NewsHostname, runtime.CertificateDomainNames);
        Assert.Equal(
            Path.Combine("certs", "journal", expected + ".json"),
            AcmePaths.TransactionJournalPath("certs", runtime.Fqdn));
        Assert.Equal(
            Path.Combine("certs", "live", expected),
            AcmePaths.LiveDir("certs", runtime.Fqdn));
    }

    private static IHost CreateHost(Dictionary<string, string?> pairs)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "VectorNNTP.BackFiller.Tests",
            EnvironmentName = Environments.Development,
        });
        builder.Configuration.AddInMemoryCollection(pairs);
        builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        builder.Services.AddSingleton<VectorNNTP.NNTPD.Cloudflare.ICloudflareDnsReconciler>(
            new NoOpCloudflareDnsReconciler());
        builder.Services.AddSingleton<IPhysicalMemoryProvider>(
            new FakePhysicalMemoryProvider(64L * 1024 * 1024 * 1024));
        builder.Services.AddSingleton<VectorNNTP.Common.Messaging.RabbitMq.IRabbitMqConnectionFactory>(
            new FakeBackFillerRabbitMqConnectionFactory());
        builder.Services.AddSingleton<VectorNNTP.BackFiller.Accounts.IProviderAccountSource>(
            new FakeProviderAccountSource());
        builder.AddBackFillerHosting();
        return builder.Build();
    }
}
