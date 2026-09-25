using System.Net;
using Microsoft.Extensions.Configuration;
using VectorNNTP.NNTPCancelMessage;
using VectorNNTP.NNTPCancelMessage.PgpVerify;
using VectorNNTP.NNTPD.Session.Commands.Posting;

namespace VectorNNTP.NNTPCancelMessage.Tests;

public sealed class AdminEndToEndTests : IClassFixture<SharedPgpKeyFixture>
{
    private const string TestKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Password = "unit-test-newsmaster-password";
    private readonly SharedPgpKeyFixture _pgp;

    public AdminEndToEndTests(SharedPgpKeyFixture pgp)
    {
        _pgp = pgp;
    }

    [Fact]
    public async Task AdminApp_ConnectsAuthenticatesHeadsDecryptsAndPostsCancel()
    {
        var protector = new AesGcmPostingTraceProtector(Convert.FromHexString(TestKey));
        var original = new PostingTracePayload(
            IPAddress.Parse("198.18.0.66"),
            54321,
            new DateTimeOffset(2026, 9, 25, 14, 0, 0, TimeSpan.Zero),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            "poster");
        var token = protector.Protect(original);
        var head =
            "221 0 <abc@example.com>\r\n" +
            "From: poster@example.com\r\n" +
            "Newsgroups: misc.test,alt.test\r\n" +
            "Subject: spam\r\n" +
            "Message-ID: <abc@example.com>\r\n" +
            "X-Trace: " + token + "\r\n" +
            ".\r\n";

        await using var server = await ScriptedNntpServer.StartAsync(
            headStatus: head,
            expectedUsername: "newsmaster",
            expectedPassword: Password,
            requireAuthentication: true);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await AdminApp.RunAsync(
            [
                "--host", "127.0.0.1",
                "--port", server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--plaintext",
                "--username", "newsmaster",
                "--password", Password,
                "--cancel",
                "<abc@example.com>",
            ],
            stdout,
            stderr,
            BuildConfiguration(server.Port),
            transport: null);

        Assert.Equal(AdminExitCode.Success, code);
        Assert.True(server.Authenticated);
        Assert.Equal(1, server.HeadCount);
        Assert.Equal(1, server.PostCount);
        Assert.True(server.Commands.Count >= 4);
        Assert.Equal("AUTHINFO USER newsmaster", server.Commands[0]);
        Assert.Equal("AUTHINFO PASS <redacted>", server.Commands[1]);
        Assert.Equal("HEAD <abc@example.com>", server.Commands[2]);
        Assert.Equal("POST", server.Commands[3]);
        Assert.Contains("Control: cancel <abc@example.com>\r\n", server.LastArticle, StringComparison.Ordinal);
        Assert.Contains("Newsgroups: misc.test,alt.test\r\n", server.LastArticle, StringComparison.Ordinal);
        Assert.DoesNotContain("Message-ID: <abc@example.com>", server.LastArticle, StringComparison.Ordinal);
        Assert.DoesNotContain("cmsg", server.LastArticle, StringComparison.Ordinal);
        Assert.Contains(
            "This is an administrative cancellation of <abc@example.com>.\r\n",
            server.LastArticle,
            StringComparison.Ordinal);
        Assert.DoesNotContain("198.18.0.66", server.LastArticle, StringComparison.Ordinal);
        Assert.DoesNotContain(token, server.LastArticle, StringComparison.Ordinal);
        Assert.Contains("X-PGP-Sig:", server.LastArticle, StringComparison.Ordinal);
        Assert.True(PgpVerifyVerifier.TryVerify(
            server.LastArticle,
            _pgp.Key.PublicArmored,
            PgpVerifyCanonicalizer.CancelSignedHeaderNames));
        Assert.DoesNotContain(TestPgpKeys.Passphrase, server.LastArticle, StringComparison.Ordinal);

        var text = stdout.ToString();
        Assert.Contains("198.18.0.66", text, StringComparison.Ordinal);
        Assert.Contains("Authenticated user: poster", text, StringComparison.Ordinal);
        Assert.Contains("Authenticated as: newsmaster", text, StringComparison.Ordinal);
        Assert.Contains("CANCEL submission identity", text, StringComparison.Ordinal);
        Assert.Contains("PGPVERIFY", text, StringComparison.Ordinal);
        Assert.Contains(_pgp.Key.Fingerprint, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(TestPgpKeys.Passphrase, text, StringComparison.Ordinal);
        Assert.DoesNotContain(TestKey, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedAuthinfo_DoesNotIssueHeadOrPost()
    {
        await using var server = await ScriptedNntpServer.StartAsync(
            expectedUsername: "newsmaster",
            expectedPassword: Password,
            requireAuthentication: true);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await AdminApp.RunAsync(
            [
                "--host", "127.0.0.1",
                "--port", server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--plaintext",
                "--username", "newsmaster",
                "--password", "wrong-secret",
                "--cancel",
                "<abc@example.com>",
            ],
            stdout,
            stderr,
            BuildConfiguration(server.Port));

        Assert.Equal(AdminExitCode.AuthenticationFailed, code);
        Assert.Equal(0, server.HeadCount);
        Assert.Equal(0, server.PostCount);
        Assert.DoesNotContain("wrong-secret", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-secret", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("AUTHINFO failed", stderr.ToString(), StringComparison.Ordinal);
    }

    private IConfiguration BuildConfiguration(int port) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NntpCancelMessage:Host"] = "127.0.0.1",
            ["NntpCancelMessage:UseTls"] = "false",
            ["NntpCancelMessage:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["NntpCancelMessage:From"] = "newsmaster@usenet.ninja",
            ["NntpCancelMessage:Pgp:Enabled"] = "true",
            ["NntpCancelMessage:Pgp:PrivateKeyPath"] = _pgp.Path,
            ["NntpCancelMessage:Pgp:PrivateKeyPassphrase"] = TestPgpKeys.Passphrase,
            ["Nntpd:XTraceKey"] = TestKey,
        }).Build();
}
