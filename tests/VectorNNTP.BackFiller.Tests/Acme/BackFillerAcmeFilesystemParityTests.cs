using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.BackFiller.Tests.Acme;

public sealed class BackFillerAcmeFilesystemParityTests
{
    [Fact]
    public void BackFiller_certificate_identity_uses_common_fqdn_paths()
    {
        var options = BackFillerTestOptions.CreateValid();
        var fqdn = CertificateIdentities.NormalizeFqdn(options.Fqdn);
        Assert.Equal("backfiller01.usenet.ninja", fqdn);
        Assert.Equal(
            new[] { "backfiller01.usenet.ninja" },
            CertificateIdentities.ForFqdn(fqdn, includeNewsHostname: false));
        Assert.DoesNotContain(CertificateIdentities.NewsHostname, CertificateIdentities.ForFqdn(fqdn, includeNewsHostname: false));

        const string certs = "certs-root";
        Assert.Equal(
            Path.Combine(certs, "live", fqdn, "current"),
            AcmePaths.CurrentGenerationPointerPath(certs, fqdn));
        Assert.Equal(
            Path.Combine(certs, "live", fqdn, "gens"),
            AcmePaths.GenerationsDir(certs, fqdn));
        Assert.Equal(
            Path.Combine(certs, "live", fqdn, "dns01"),
            AcmePaths.Dns01RecoveryDir(certs, fqdn));
        Assert.Equal(
            Path.Combine(certs, "journal", fqdn + ".json"),
            AcmePaths.TransactionJournalPath(certs, fqdn));
        Assert.Equal(
            Path.Combine(certs, "live", fqdn, ".issuance.lock"),
            AcmePaths.IssuanceLockPath(certs, fqdn));
        Assert.Equal(
            Path.Combine(certs, "account", "private_key.der"),
            AcmePaths.AccountKeyPath(certs));
        Assert.DoesNotContain(
            Path.Combine("live", "gens"),
            AcmePaths.GenerationsDir(certs, fqdn),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Path.Combine("live", "current"),
            AcmePaths.CurrentGenerationPointerPath(certs, fqdn),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BackFiller_uses_shared_acme_account_environment_variable()
    {
        var pairs = BackFillerTestOptions.CreateValidConfigurationPairs();
        Assert.Equal("security@usenet.ninja", pairs[AcmeCloudflareOptions.AcmeAccountConfigurationKey]);
        Assert.False(pairs.ContainsKey("BackFiller:AcmeEmail"));
        Assert.Equal("VECTOR__ACMEACCOUNT", AcmeCloudflareOptions.AcmeAccountEnvironmentVariable);
        Assert.DoesNotContain("BACKFILLER", AcmeCloudflareOptions.AcmeAccountEnvironmentVariable, StringComparison.OrdinalIgnoreCase);
    }
}
