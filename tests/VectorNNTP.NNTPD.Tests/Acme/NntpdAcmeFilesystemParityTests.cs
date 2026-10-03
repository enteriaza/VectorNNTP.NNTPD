using VectorNNTP.Common.Acme;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Tests.Fixtures;
using VectorNNTP.Common.Configuration;

namespace VectorNNTP.NNTPD.Tests.Acme;

public sealed class NntpdAcmeFilesystemParityTests
{
    [Fact]
    public void Nntpd_certificate_identity_uses_common_fqdn_paths()
    {
        var options = TestHostFactory.CreateValidOptions();
        options.ServerId = 1;
        options.DnsSuffix = "usenet.ninja";
        var fqdn = CertificateIdentities.NormalizeFqdn(options.Fqdn);
        Assert.Equal("nntpd01.usenet.ninja", fqdn);
        Assert.Equal(
            new[] { "nntpd01.usenet.ninja", CertificateIdentities.NewsHostname },
            CertificateIdentities.ForFqdn(fqdn));

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
    public void Nntpd_uses_shared_acme_account_environment_variable()
    {
        Assert.Equal("VECTOR__ACMEACCOUNT", AcmeCloudflareOptions.AcmeAccountEnvironmentVariable);
        Assert.Equal(
            AcmeCloudflareOptions.AcmeAccountEnvironmentVariable,
            NntpdOptions.AcmeAccountEnvironmentVariable);
        Assert.DoesNotContain("NNTPD", NntpdOptions.AcmeAccountEnvironmentVariable, StringComparison.OrdinalIgnoreCase);
    }
}
