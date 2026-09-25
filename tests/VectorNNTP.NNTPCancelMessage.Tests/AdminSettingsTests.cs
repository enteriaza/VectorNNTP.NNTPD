using VectorNNTP.NNTPCancelMessage;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPCancelMessage.Tests;

public sealed class AdminSettingsTests : IClassFixture<SharedPgpKeyFixture>
{
    private readonly SharedPgpKeyFixture _pgp;

    public AdminSettingsTests(SharedPgpKeyFixture pgp)
    {
        _pgp = pgp;
    }

    [Fact]
    public void MissingXTraceKey_FailsWithoutEchoingSecret()
    {
        Assert.True(AdminCliParser.TryParse(["<abc@example.com>"], out var args, out _));
        var admin = new NntpCancelMessageOptions { Host = "127.0.0.1", Port = 1199, UseTls = false };
        var nntpd = new NntpdOptions();
        Assert.False(AdminSettings.TryCreate(args, admin, nntpd, out _, out var error));
        Assert.Contains(NntpdOptions.XTraceKeyConfigurationKey, error, StringComparison.Ordinal);
        Assert.DoesNotContain("01234567", error, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelWithoutNewsmaster_FailsWithoutEchoingPassword()
    {
        Assert.True(AdminCliParser.TryParse(["--cancel", "<abc@example.com>"], out var args, out _));
        var admin = new NntpCancelMessageOptions { Host = "127.0.0.1", Port = 1199, UseTls = false };
        var nntpd = new NntpdOptions
        {
            XTraceKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        };
        Assert.False(AdminSettings.TryCreate(args, admin, nntpd, out _, out var error));
        Assert.Contains("--username/--password", error, StringComparison.Ordinal);
        Assert.DoesNotContain("unit-test", error, StringComparison.Ordinal);
    }

    [Fact]
    public void CliCredentials_OverrideConfiguration()
    {
        Assert.True(AdminCliParser.TryParse(
            [
                "--host", "nntpd01.usenet.ninja",
                "--port", "563",
                "--tls",
                "--username", "cli-user",
                "--password", "cli-secret",
                "<abc@example.com>",
            ],
            out var args,
            out _));
        var admin = new NntpCancelMessageOptions
        {
            Host = "127.0.0.1",
            Port = 1199,
            UseTls = false,
            Username = "config-user",
            Password = "config-secret",
        };
        var nntpd = new NntpdOptions
        {
            BindPort = 1199,
            BindPortTls = 5633,
            XTraceKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            NewsmasterUser = "nntpd-user",
            NewsmasterPassword = "nntpd-secret",
        };
        Assert.True(AdminSettings.TryCreate(args, admin, nntpd, out var settings, out _));
        Assert.Equal("nntpd01.usenet.ninja", settings.Host);
        Assert.Equal(563, settings.Port);
        Assert.True(settings.UseTls);
        Assert.Equal("cli-user", settings.NewsmasterUser);
        Assert.Equal("cli-secret", settings.NewsmasterPassword);
    }

    [Fact]
    public void ConfigurationFallback_UsesAppsettingsWhenCliOmitted()
    {
        Assert.True(AdminCliParser.TryParse(["<abc@example.com>"], out var args, out _));
        var admin = new NntpCancelMessageOptions
        {
            Host = "configured.example",
            Port = 1199,
            UseTls = false,
            Username = "config-user",
            Password = "config-secret",
        };
        var nntpd = new NntpdOptions
        {
            XTraceKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            NewsmasterUser = "nntpd-user",
            NewsmasterPassword = "nntpd-secret",
        };
        Assert.True(AdminSettings.TryCreate(args, admin, nntpd, out var settings, out _));
        Assert.Equal("configured.example", settings.Host);
        Assert.Equal("config-user", settings.NewsmasterUser);
        Assert.Equal("config-secret", settings.NewsmasterPassword);
    }

    [Fact]
    public void TlsWithoutPort_DoesNotFallBackToPlaintext()
    {
        Assert.True(AdminCliParser.TryParse(["--tls", "<abc@example.com>"], out var args, out _));
        var admin = new NntpCancelMessageOptions { Host = "127.0.0.1", Port = 0 };
        var nntpd = new NntpdOptions
        {
            BindPort = 1199,
            BindPortTls = 0,
            XTraceKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        };
        Assert.False(AdminSettings.TryCreate(args, admin, nntpd, out _, out var error));
        Assert.Contains("TLS", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Plaintext_UsesBindPort()
    {
        Assert.True(AdminCliParser.TryParse(["--plaintext", "<abc@example.com>"], out var args, out _));
        var admin = new NntpCancelMessageOptions { Host = "127.0.0.1" };
        var nntpd = new NntpdOptions
        {
            BindPort = 1199,
            BindPortTls = 5633,
            XTraceKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        };
        Assert.True(AdminSettings.TryCreate(args, admin, nntpd, out var settings, out _));
        Assert.False(settings.UseTls);
        Assert.Equal(1199, settings.Port);
    }

    [Fact]
    public void CancelWithoutPgp_FailsBeforeConnect()
    {
        Assert.True(AdminCliParser.TryParse(["--cancel", "<abc@example.com>"], out var args, out _));
        var admin = new NntpCancelMessageOptions
        {
            Host = "127.0.0.1",
            Port = 1199,
            UseTls = false,
            Username = "newsmaster",
            Password = "unit-test-newsmaster-password",
        };
        var nntpd = new NntpdOptions
        {
            XTraceKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        };
        Assert.False(AdminSettings.TryCreate(args, admin, nntpd, out _, out var error));
        Assert.Contains("CANCEL requires PGP signing", error, StringComparison.Ordinal);
        Assert.DoesNotContain("unit-test-newsmaster-password", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_DoesNotRequirePgp()
    {
        Assert.True(AdminCliParser.TryParse(["<abc@example.com>"], out var args, out _));
        var admin = new NntpCancelMessageOptions
        {
            Host = "127.0.0.1",
            Port = 1199,
            UseTls = false,
            Pgp = { Enabled = true, PrivateKeyPath = string.Empty },
        };
        var nntpd = new NntpdOptions
        {
            XTraceKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        };
        Assert.True(AdminSettings.TryCreate(args, admin, nntpd, out var settings, out _));
        Assert.Null(settings.PgpSigner);
    }

    [Fact]
    public void CancelWithPgp_LoadsSigningIdentity()
    {
        Assert.True(AdminCliParser.TryParse(["--cancel", "<abc@example.com>"], out var args, out _));
        var admin = new NntpCancelMessageOptions
        {
            Host = "127.0.0.1",
            Port = 1199,
            UseTls = false,
            Username = "newsmaster",
            Password = "unit-test-newsmaster-password",
            Pgp =
            {
                Enabled = true,
                PrivateKeyPath = _pgp.Path,
                PrivateKeyPassphrase = TestPgpKeys.Passphrase,
            },
        };
        var nntpd = new NntpdOptions
        {
            XTraceKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        };
        Assert.True(AdminSettings.TryCreate(args, admin, nntpd, out var settings, out _));
        Assert.NotNull(settings.PgpSigner);
        Assert.Equal(_pgp.Key.Fingerprint, settings.PgpSigner.Identity.Fingerprint);
        Assert.Equal(TestPgpKeys.Passphrase, settings.PgpPassphrase);
    }
}
