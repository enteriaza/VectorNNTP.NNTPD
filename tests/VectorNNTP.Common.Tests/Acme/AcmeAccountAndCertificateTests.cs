using System.Security.Cryptography;
using VectorNNTP.Common.Tests.TestDoubles;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.Common.Tests.Acme;

public sealed class AcmeAccountAndCertificateTests
{
    [Fact]
    public void Account_SaveThenLoad_ReusesKey()
    {
        using var dir = new TempStateDir();
        var store = new AccountStore(dir.Path);
        Assert.Null(store.Load());

        using var rsa = RSA.Create(2048);
        var key = rsa.ExportPkcs8PrivateKey();
        store.Save(new AcmeAccountState(
            "https://acme.example/acme/acct/1",
            AcmeCloudflareOptions.DefaultAcmeDirectoryUrl,
            key));

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.True(key.AsSpan().SequenceEqual(loaded.PrivateKeyDer));
        Assert.False(DerCrypto.LooksLikePem(loaded.PrivateKeyDer));
    }

    [Fact]
    public void Account_EnsureRegistered_ReusesExistingWithoutCallingRegister()
    {
        using var dir = new TempStateDir();
        var store = new AccountStore(dir.Path);
        using var rsa = RSA.Create(2048);
        var key = rsa.ExportPkcs8PrivateKey();
        store.Save(new AcmeAccountState("https://acme.example/acme/acct/1", "https://dir.example/directory", key));

        var registerCalls = 0;
        var result = store.EnsureRegistered(
            "https://dir.example/directory",
            () => throw new InvalidOperationException("should not generate"),
            _ =>
            {
                registerCalls++;
                return ("x", "");
            });

        Assert.Equal(0, registerCalls);
        Assert.Equal("https://acme.example/acme/acct/1", result.AccountUri);
    }

    [Fact]
    public void Account_EnsureRegistered_PersistsNewAccount()
    {
        using var dir = new TempStateDir();
        var store = new AccountStore(dir.Path);
        using var rsa = RSA.Create(2048);
        var key = rsa.ExportPkcs8PrivateKey();

        var result = store.EnsureRegistered(
            "https://dir.example/directory",
            () => key,
            _ => ("https://acme.example/acme/acct/9", "body"));

        Assert.Equal("https://acme.example/acme/acct/9", result.AccountUri);
        Assert.NotNull(store.Load());
        Assert.False(File.Exists(AcmePaths.AccountPendingKeyPath(dir.Path)));
    }

    [Fact]
    public void Certificate_SaveLoad_SurvivesRestart()
    {
        using var dir = new TempStateDir();
        var names = CertificateIdentities.ForFqdn("backfiller01.usenet.ninja", includeNewsHostname: false);
        var store = new CertificateStore(dir.Path, TestCertificateFactory.Password, names, TimeSpan.FromDays(30));
        var material = TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(60));

        store.Save(material);
        var reloaded = new CertificateStore(dir.Path, TestCertificateFactory.Password, names, TimeSpan.FromDays(30))
            .Load();
        Assert.NotNull(reloaded);
        Assert.Equal(names, reloaded.Domains);
        Assert.DoesNotContain(CertificateIdentities.NewsHostname, reloaded.Domains);
    }

    [Fact]
    public void Certificate_FailedReplacement_PreservesCurrent()
    {
        using var dir = new TempStateDir();
        var names = CertificateIdentities.ForFqdn("backfiller01.usenet.ninja", includeNewsHostname: false);
        var store = new CertificateStore(dir.Path, TestCertificateFactory.Password, names, TimeSpan.FromDays(30));
        var current = TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(60));
        store.Save(current);
        var before = File.ReadAllBytes(store.Paths().PfxPath);

        var invalid = current with { PfxBytes = [0x00, 0x01] };
        Assert.ThrowsAny<Exception>(() => store.Save(invalid));

        var after = new CertificateStore(dir.Path, TestCertificateFactory.Password, names, TimeSpan.FromDays(30))
            .Load();
        Assert.NotNull(after);
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(store.Paths().PfxPath)));
    }

}
