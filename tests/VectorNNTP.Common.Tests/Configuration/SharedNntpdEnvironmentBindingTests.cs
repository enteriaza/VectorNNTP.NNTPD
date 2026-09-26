using Microsoft.Extensions.Configuration;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.Common.Tests.Configuration;

public sealed class SharedNntpdEnvironmentBindingTests
{
    [Fact]
    public void SharedEnvironmentVariableNames_AreCanonicalUppercaseVectorPrefix()
    {
        Assert.Equal("VECTOR__CLOUDFLAREAPIKEY", AcmeCloudflareOptions.CloudFlareApiKeyEnvironmentVariable);
        Assert.Equal("VECTOR__ACMECERTIFICATEPASSWORD", AcmeCloudflareOptions.AcmeCertificatePasswordEnvironmentVariable);
        Assert.Equal("VECTOR__CLOUDFLAREZONEID", AcmeCloudflareOptions.CloudFlareZoneIdEnvironmentVariable);
        Assert.Equal("VECTOR__BINDADDRESS", AcmeCloudflareOptions.BindAddressEnvironmentVariable);
        Assert.Equal("VECTOR__BINDPORT", AcmeCloudflareOptions.BindPortEnvironmentVariable);
        Assert.Equal("VECTOR__BINDPORTTLS", AcmeCloudflareOptions.BindPortTlsEnvironmentVariable);
        Assert.Equal(AcmeCloudflareOptions.SectionName, string.Empty);
        Assert.Equal("CloudFlareApiKey", AcmeCloudflareOptions.CloudFlareApiKeyConfigurationKey);
        Assert.Equal("AcmeCertificatePassword", AcmeCloudflareOptions.AcmeCertificatePasswordConfigurationKey);
        Assert.Equal("CloudFlareZoneId", AcmeCloudflareOptions.CloudFlareZoneIdConfigurationKey);

        Assert.True(VectorEnvironment.IsCanonicalName(AcmeCloudflareOptions.CloudFlareApiKeyEnvironmentVariable));
        Assert.True(VectorEnvironment.IsCanonicalName(AcmeCloudflareOptions.AcmeCertificatePasswordEnvironmentVariable));
        Assert.True(VectorEnvironment.IsCanonicalName(AcmeCloudflareOptions.CloudFlareZoneIdEnvironmentVariable));
        Assert.Equal(
            AcmeCloudflareOptions.CloudFlareApiKeyEnvironmentVariable,
            VectorEnvironment.Variable(AcmeCloudflareOptions.CloudFlareApiKeyConfigurationKey));
        Assert.Equal(
            AcmeCloudflareOptions.AcmeCertificatePasswordEnvironmentVariable,
            VectorEnvironment.Variable(AcmeCloudflareOptions.AcmeCertificatePasswordConfigurationKey));
        Assert.Equal(
            AcmeCloudflareOptions.CloudFlareZoneIdEnvironmentVariable,
            VectorEnvironment.Variable(AcmeCloudflareOptions.CloudFlareZoneIdConfigurationKey));

        Assert.DoesNotContain("nntpd__", AcmeCloudflareOptions.CloudFlareApiKeyEnvironmentVariable, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("backfiller__", AcmeCloudflareOptions.CloudFlareApiKeyEnvironmentVariable, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "nntpd__",
            AcmeCloudflareOptions.AcmeCertificatePasswordEnvironmentVariable,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SharedRootTree_BindsTheSameOptionNames()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CloudFlareApiKey"] = "AbC123xYz!",
                ["AcmeCertificatePassword"] = "Pfx-CaseSensitive-Secret!",
                ["CloudFlareZoneId"] = "5811a29d39a0732afb5f160c9b137c3d",
                ["BindPort"] = "1190",
                ["BindPortTls"] = "1190",
                ["AcmeEmail"] = "ops@example.org",
                ["BindAddress:0"] = "*",
            })
            .Build();

        var options = new AcmeCloudflareOptions();
        configuration.Bind(options);

        Assert.Equal("AbC123xYz!", options.CloudFlareApiKey);
        Assert.Equal("Pfx-CaseSensitive-Secret!", options.AcmeCertificatePassword);
        Assert.Equal("5811a29d39a0732afb5f160c9b137c3d", options.CloudFlareZoneId);
        Assert.Equal(1190, options.BindPort);
        Assert.Equal(1190, options.BindPortTls);
        Assert.Equal("*", options.BindAddress[0]);
    }

    [Fact]
    public void OverlaySharedFromRoot_PreservesExactSecretValues()
    {
        const string password = "AbC123xYz!";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AcmeCertificatePassword"] = password,
                ["CloudFlareApiKey"] = "Cf-Key-MixedCASE-99",
                ["CloudFlareZoneId"] = "ZoneIdMixedCase",
            })
            .Build();

        var options = new AcmeCloudflareOptions
        {
            AcmeCertificatePassword = "from-nntpd-section-must-not-win",
            CloudFlareApiKey = "from-nntpd-section-must-not-win",
        };
        AcmeCloudflareOptions.OverlaySharedFromRoot(options, configuration);

        Assert.Equal(password, options.AcmeCertificatePassword);
        Assert.Equal("Cf-Key-MixedCASE-99", options.CloudFlareApiKey);
        Assert.Equal("ZoneIdMixedCase", options.CloudFlareZoneId);
        Assert.Equal(password, configuration["AcmeCertificatePassword"]);
    }

    [Fact]
    public void ObsoleteApplicationPrefixes_DoNotBindSharedOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["nntpd:CloudFlareApiKey"] = "obsolete-nntpd-prefix",
                ["nntpd:AcmeCertificatePassword"] = "obsolete-nntpd-pfx",
                ["Nntpd:CloudFlareZoneId"] = "obsolete-nntpd-zone",
                ["backfiller:CloudFlareApiKey"] = "obsolete-backfiller-prefix",
                ["BackFiller:LetsEncrypt:CloudFlareApiToken"] = "obsolete-letsencrypt",
            })
            .Build();

        var options = new AcmeCloudflareOptions();
        configuration.Bind(options);
        AcmeCloudflareOptions.OverlaySharedFromRoot(options, configuration);

        Assert.True(string.IsNullOrEmpty(options.CloudFlareApiKey));
        Assert.True(string.IsNullOrEmpty(options.AcmeCertificatePassword));
        Assert.True(string.IsNullOrEmpty(options.CloudFlareZoneId));
    }

    [Fact]
    public void VectorPrefix_StripsToRootConfigurationKeys()
    {
        Assert.Equal("CLOUDFLAREAPIKEY", StripVectorPrefix("VECTOR__CLOUDFLAREAPIKEY"));
        Assert.Equal("ACMECERTIFICATEPASSWORD", StripVectorPrefix("VECTOR__ACMECERTIFICATEPASSWORD"));
        Assert.Equal("CLOUDFLAREZONEID", StripVectorPrefix("VECTOR__CLOUDFLAREZONEID"));
        Assert.Equal("RABBITMQ:USERNAME", StripVectorPrefix("VECTOR__RABBITMQ__USERNAME").Replace("__", ":", StringComparison.Ordinal));
        Assert.Equal(
            "VECTOR__RABBITMQ__USERNAME",
            VectorEnvironment.Variable("RabbitMQ", "Username"));
    }

    [Fact]
    public void ConfigurationDocumentation_DoesNotPrescribeObsoletePrefixes()
    {
        var docs = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "configuration.md"));
        Assert.True(File.Exists(docs), docs);
        var text = File.ReadAllText(docs);
        Assert.Contains("VECTOR__CLOUDFLAREAPIKEY", text, StringComparison.Ordinal);
        Assert.Contains("VECTOR__ACMECERTIFICATEPASSWORD", text, StringComparison.Ordinal);
        Assert.Contains("VECTOR__CLOUDFLAREZONEID", text, StringComparison.Ordinal);
        Assert.Contains("VECTOR__RABBITMQ__USERNAME", text, StringComparison.Ordinal);
        Assert.DoesNotContain("nntpd__cloudflareapikey", text, StringComparison.Ordinal);
        Assert.DoesNotContain("nntpd__AcmeCertificatePassword", text, StringComparison.Ordinal);
        Assert.DoesNotContain("nntpd__RabbitMQ__Username", text, StringComparison.Ordinal);
        Assert.DoesNotContain("backfiller__BackFiller__", text, StringComparison.Ordinal);
        Assert.DoesNotContain("VECTOR__XTRACEKEY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("VECTOR__XTRACEPREVIOUSKEY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("VECTOR__NEWSMASTERUSER", text, StringComparison.Ordinal);
        Assert.DoesNotContain("VECTOR__NEWSMASTERPASSWORD", text, StringComparison.Ordinal);
        Assert.DoesNotContain("VECTOR__NAME", text, StringComparison.Ordinal);
        Assert.DoesNotContain("VECTOR__SERVERID", text, StringComparison.Ordinal);
        Assert.Contains("NNTPD__XTRACEKEY", text, StringComparison.Ordinal);
        Assert.Contains("NNTPD__SERVERID", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedVectorEnvironment_DoesNotTreatApplicationSettingsAsCanonical()
    {
        Assert.False(VectorEnvironment.IsCanonicalName("NNTPD__SERVERID"));
        Assert.False(VectorEnvironment.IsCanonicalName("NNTPD__XTRACEKEY"));
        Assert.False(VectorEnvironment.IsCanonicalName("NNTPD__NEWSMASTERPASSWORD"));
        Assert.False(VectorEnvironment.IsCanonicalName("BACKFILLER__NAME"));
        Assert.False(VectorEnvironment.IsCanonicalName("BACKFILLER__SERVERID"));
        Assert.True(VectorEnvironment.IsCanonicalName(VectorEnvironment.Variable("CloudFlareApiKey")));
    }

    [Fact]
    public void CertificateIdentities_BackFillerOmitsNewsHostname()
    {
        var names = CertificateIdentities.ForFqdn("backfiller01.usenet.ninja", includeNewsHostname: false);
        Assert.Equal(["backfiller01.usenet.ninja"], names);
        Assert.DoesNotContain(CertificateIdentities.NewsHostname, names);
    }

    [Fact]
    public void CertificateIdentities_NntpdIncludesNewsHostname()
    {
        var names = CertificateIdentities.ForFqdn("nntpd01.usenet.ninja");
        Assert.Equal(["nntpd01.usenet.ninja", CertificateIdentities.NewsHostname], names);
    }

    [Fact]
    public void Validator_DoesNotEchoSecrets()
    {
        var options = new AcmeCloudflareOptions
        {
            BindAddress = ["*"],
            BindPort = 1190,
            BindPortTls = 1190,
            AcmeEmail = "ops@example.org",
            AcmeCertificatePassword = "super-secret-pfx",
            CloudFlareApiKey = "super-secret-cf",
            CloudFlareZoneId = "zone",
            Fqdn = "backfiller01.usenet.ninja",
            IncludeNewsHostnameInCertificate = false,
        };

        var result = new AcmeCloudflareOptionsValidator(new AcceptAllAssignee()).Validate(null, options);
        Assert.True(result.Succeeded);
        if (result.Failures is not null)
        {
            foreach (var failure in result.Failures)
            {
                Assert.DoesNotContain("super-secret-pfx", failure, StringComparison.Ordinal);
                Assert.DoesNotContain("super-secret-cf", failure, StringComparison.Ordinal);
            }
        }
    }

    private static string StripVectorPrefix(string environmentVariable)
    {
        Assert.True(VectorEnvironment.IsCanonicalName(environmentVariable));
        return environmentVariable[VectorEnvironment.Prefix.Length..];
    }

    private sealed class AcceptAllAssignee : ILocalIpAddressAssignee
    {
        public bool IsLocallyAssigned(System.Net.IPAddress address) => true;

        public IReadOnlyList<System.Net.IPAddress> GetAssignedUnicastAddresses() => [];
    }
}
