using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.BackFiller.Tests.Configuration;

public sealed class SharedNntpdEnvironmentBindingTests
{
    [Fact]
    public void BackFiller_UsesTheSameVectorEnvironmentVariableNames()
    {
        Assert.Equal("VECTOR__CLOUDFLAREAPIKEY", AcmeCloudflareOptions.CloudFlareApiKeyEnvironmentVariable);
        Assert.Equal("VECTOR__ACMECERTIFICATEPASSWORD", AcmeCloudflareOptions.AcmeCertificatePasswordEnvironmentVariable);
        Assert.Equal("VECTOR__CLOUDFLAREZONEID", AcmeCloudflareOptions.CloudFlareZoneIdEnvironmentVariable);
        Assert.Equal("BACKFILLER__NAME", BackFillerOptions.NameEnvironmentVariable);
        Assert.Equal("BACKFILLER__SERVERID", BackFillerOptions.ServerIdEnvironmentVariable);
        Assert.False(VectorEnvironment.IsCanonicalName(BackFillerOptions.NameEnvironmentVariable));
        Assert.False(VectorEnvironment.IsCanonicalName(BackFillerOptions.ServerIdEnvironmentVariable));
        Assert.Equal("VECTOR__RABBITMQ__USERNAME", BackFillerOptions.RabbitMqUsernameEnvironmentVariable);
        Assert.Equal("VECTOR__CONNECTIONSTRINGS__GRABBERDB", BackFillerOptions.GrabberDbEnvironmentVariable);
        Assert.Equal(VectorEnvironment.Prefix, BackFillerOptions.EnvironmentVariablePrefix);
        Assert.True(VectorEnvironment.IsCanonicalName(AcmeCloudflareOptions.CloudFlareApiKeyEnvironmentVariable));
        Assert.DoesNotContain("nntpd__", AcmeCloudflareOptions.CloudFlareApiKeyEnvironmentVariable, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("backfiller__", AcmeCloudflareOptions.AcmeCertificatePasswordEnvironmentVariable, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BackFiller_BindsSharedRootSection_WithoutASecondPrefix()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(BackFillerTestOptions.CreateValidConfigurationPairs())
            .Build();

        var options = new AcmeCloudflareOptions();
        configuration.Bind(options);

        Assert.Equal(BackFillerTestOptions.SecretToken, options.CloudFlareApiKey);
        Assert.Equal(BackFillerTestOptions.SecretPfx, options.AcmeCertificatePassword);
        Assert.Equal("0123456789abcdef0123456789abcdef", options.CloudFlareZoneId);
        Assert.Equal(1190, options.BindPort);
        Assert.Equal(1190, options.BindPortTls);
    }

    [Fact]
    public void BackFillerHost_RegistersSharedAcmeCloudflareOptions_WithoutNewsSan()
    {
        var builder = Host.CreateApplicationBuilder([]);
        builder.Configuration.AddInMemoryCollection(BackFillerTestOptions.CreateValidConfigurationPairs());
        builder.Services.AddSingleton<ILocalIpAddressAssignee>(new FakeLocalIpAddressAssignee(assignAll: true));
        builder.Services.AddSingleton<VectorNNTP.NNTPD.Cloudflare.ICloudflareDnsReconciler>(
            new VectorNNTP.BackFiller.Tests.TestDoubles.NoOpCloudflareDnsReconciler());
        builder.ConfigureBackFillerPlatformHosting();
        builder.AddBackFillerHosting();

        using var host = builder.Build();
        var acme = host.Services.GetRequiredService<IOptions<AcmeCloudflareOptions>>().Value;
        Assert.False(acme.IncludeNewsHostnameInCertificate);
        Assert.Equal(1190, acme.BindPort);
        Assert.Equal(1190, acme.BindPortTls);
        Assert.Equal(BackFillerTestOptions.SecretToken, acme.CloudFlareApiKey);
        Assert.Equal(BackFillerTestOptions.SecretPfx, acme.AcmeCertificatePassword);
        Assert.Equal("backfiller01.usenet.ninja", acme.Fqdn);
        Assert.Equal(
            ["backfiller01.usenet.ninja"],
            CertificateIdentities.ForFqdn(acme.Fqdn, acme.IncludeNewsHostnameInCertificate));
    }
}
