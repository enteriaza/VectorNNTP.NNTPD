using System.Net;
using Microsoft.Extensions.Configuration;
using VectorNNTP.NNTPCancelMessage;
using VectorNNTP.NNTPCancelMessage.PgpVerify;
using VectorNNTP.NNTPD.Session.Commands.Posting;

namespace VectorNNTP.NNTPCancelMessage.Tests;

public sealed class AdminAppTests : IClassFixture<SharedPgpKeyFixture>
{
    private const string TestKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string PreviousKey = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
    private readonly SharedPgpKeyFixture _pgp;

    public AdminAppTests(SharedPgpKeyFixture pgp)
    {
        _pgp = pgp;
    }

    [Fact]
    public async Task Inspect_DoesNotPost()
    {
        var protector = CreateProtector();
        var token = protector.Protect(SamplePayload());
        var fake = new FakeNntpCancelMessageTransport
        {
            HeadResponse = SuccessHead(token),
        };

        var code = await RunAsync(["<abc@example.com>"], fake);
        Assert.Equal(AdminExitCode.Success, code);
        Assert.Equal(1, fake.HeadCalls);
        Assert.Equal(0, fake.PostCalls);
        Assert.Equal("<abc@example.com>", fake.LastHeadMessageId);
        Assert.True(fake.Authenticated);
        Assert.Equal("newsmaster", fake.LastAuthenticatedUsername);
        Assert.Equal(0, fake.PostCalls);
    }

    [Fact]
    public async Task InspectWithoutCredentials_DoesNotAuthenticate()
    {
        var protector = CreateProtector();
        var fake = new FakeNntpCancelMessageTransport
        {
            HeadResponse = SuccessHead(protector.Protect(SamplePayload())),
        };

        var config = BuildConfiguration(includeCredentials: false);
        var code = await AdminApp.RunAsync(["<abc@example.com>"], new StringWriter(), new StringWriter(), config, fake);
        Assert.Equal(AdminExitCode.Success, code);
        Assert.False(fake.Authenticated);
        Assert.Equal(0, fake.PostCalls);
    }

    [Fact]
    public async Task Cancel_TargetsExactMessageId()
    {
        var protector = CreateProtector();
        var token = protector.Protect(SamplePayload());
        var fake = new FakeNntpCancelMessageTransport
        {
            HeadResponse = SuccessHead(token),
        };

        var stdout = new StringWriter();
        var code = await RunAsync(["--cancel", "<abc@example.com>"], fake, stdout);
        Assert.Equal(AdminExitCode.Success, code);
        Assert.Equal(1, fake.HeadCalls);
        Assert.Equal(1, fake.PostCalls);
        Assert.Equal("<abc@example.com>", fake.LastHeadMessageId);
        Assert.True(fake.Authenticated);
        Assert.Contains("Control: cancel <abc@example.com>", fake.LastPostedArticle, StringComparison.Ordinal);
        Assert.DoesNotContain("Message-ID: <abc@example.com>", fake.LastPostedArticle, StringComparison.Ordinal);
        Assert.Contains("Cancellation requested", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("CANCEL submission identity", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("Authenticated user: newsmaster", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("Peer IP:", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("198.18.0.66", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("Newsgroups: misc.test\r\n", fake.LastPostedArticle, StringComparison.Ordinal);
        Assert.NotNull(fake.LastPostedArticle);
        Assert.Contains("X-PGP-Sig:", fake.LastPostedArticle, StringComparison.Ordinal);
        Assert.True(PgpVerifyVerifier.TryVerify(
            fake.LastPostedArticle,
            _pgp.Key.PublicArmored,
            PgpVerifyCanonicalizer.CancelSignedHeaderNames));
        Assert.DoesNotContain("cmsg", fake.LastPostedArticle, StringComparison.Ordinal);
        Assert.Contains("PGPVERIFY", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains(_pgp.Key.Fingerprint, stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("unit-test-newsmaster-password", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(TestPgpKeys.Passphrase, stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelWithoutPgp_DoesNotPost()
    {
        var protector = CreateProtector();
        var fake = new FakeNntpCancelMessageTransport
        {
            HeadResponse = SuccessHead(protector.Protect(SamplePayload())),
        };

        var stderr = new StringWriter();
        var code = await AdminApp.RunAsync(
            ["--cancel", "<abc@example.com>"],
            new StringWriter(),
            stderr,
            BuildConfiguration(includePgp: false),
            fake);
        Assert.Equal(AdminExitCode.Configuration, code);
        Assert.False(fake.Connected);
        Assert.Equal(0, fake.PostCalls);
        Assert.Contains("CANCEL requires PGP signing", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthFailure_DoesNotHeadOrPost()
    {
        var fake = new FakeNntpCancelMessageTransport
        {
            AuthenticateException = new NntpCancelMessageAuthenticationException("AUTHINFO PASS failed."),
        };

        var stderr = new StringWriter();
        var code = await AdminApp.RunAsync(
            ["--cancel", "<abc@example.com>"],
            new StringWriter(),
            stderr,
            BuildConfiguration(),
            fake);
        Assert.Equal(AdminExitCode.AuthenticationFailed, code);
        Assert.Equal(0, fake.HeadCalls);
        Assert.Equal(0, fake.PostCalls);
        Assert.DoesNotContain("unit-test-newsmaster-password", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidNewsgroups_DoesNotPost()
    {
        var protector = CreateProtector();
        var fake = new FakeNntpCancelMessageTransport
        {
            HeadResponse = new HeadExchange
            {
                Code = 221,
                StatusLine = "221 0 <abc@example.com>",
                Headers =
                [
                    "Newsgroups: not a group",
                    "X-Trace: " + protector.Protect(SamplePayload()),
                ],
            },
        };

        var code = await RunAsync(["--cancel", "<abc@example.com>"], fake);
        Assert.Equal(AdminExitCode.CancelFailed, code);
        Assert.Equal(0, fake.PostCalls);
    }

    [Fact]
    public async Task HeadNotFound_DoesNotCancel()
    {
        var fake = new FakeNntpCancelMessageTransport
        {
            HeadResponse = new HeadExchange
            {
                Code = 430,
                StatusLine = "430 No such article",
            },
        };

        var code = await RunAsync(["--cancel", "<missing@example.com>"], fake);
        Assert.Equal(AdminExitCode.HeadFailed, code);
        Assert.Equal(0, fake.PostCalls);
    }

    [Fact]
    public async Task HeadServerError_DoesNotCancel()
    {
        var fake = new FakeNntpCancelMessageTransport
        {
            HeadResponse = new HeadExchange
            {
                Code = 503,
                StatusLine = "503 program fault",
            },
        };

        var code = await RunAsync(["--cancel", "<abc@example.com>"], fake);
        Assert.Equal(AdminExitCode.HeadFailed, code);
        Assert.Equal(0, fake.PostCalls);
    }

    [Fact]
    public async Task HeadMalformed_DoesNotCancel()
    {
        var fake = new FakeNntpCancelMessageTransport
        {
            HeadResponse = new HeadExchange
            {
                Code = 0,
                StatusLine = "not-a-status",
            },
        };

        var code = await RunAsync(["--cancel", "<abc@example.com>"], fake);
        Assert.Equal(AdminExitCode.HeadFailed, code);
        Assert.Equal(0, fake.PostCalls);
    }

    [Fact]
    public async Task ConnectionFailure_DoesNotCancel()
    {
        var fake = new FakeNntpCancelMessageTransport
        {
            ConnectException = new IOException("refused"),
        };

        var code = await RunAsync(["--cancel", "<abc@example.com>"], fake);
        Assert.Equal(AdminExitCode.Connection, code);
        Assert.Equal(0, fake.HeadCalls);
        Assert.Equal(0, fake.PostCalls);
    }

    [Fact]
    public async Task HeadIoFailure_DoesNotCancel()
    {
        var fake = new FakeNntpCancelMessageTransport
        {
            HeadException = new IOException("truncated"),
        };

        var code = await RunAsync(["--cancel", "<abc@example.com>"], fake);
        Assert.Equal(AdminExitCode.HeadFailed, code);
        Assert.Equal(0, fake.PostCalls);
    }

    [Fact]
    public async Task MissingXTrace_InspectReportsFailure_CancelRefused()
    {
        var fake = new FakeNntpCancelMessageTransport
        {
            HeadResponse = SuccessHead(token: null),
        };

        var inspect = await RunAsync(["<abc@example.com>"], fake);
        Assert.Equal(AdminExitCode.TraceFailed, inspect);
        Assert.Equal(0, fake.PostCalls);

        var cancel = await RunAsync(["--cancel", "<abc@example.com>"], fake);
        Assert.Equal(AdminExitCode.TraceFailed, cancel);
        Assert.Equal(0, fake.PostCalls);
    }

    [Fact]
    public async Task TamperedXTrace_IsNotTreatedAsSuccess()
    {
        var protector = CreateProtector();
        var token = protector.Protect(SamplePayload()).ToCharArray();
        token[^1] = token[^1] == 'A' ? 'B' : 'A';
        var fake = new FakeNntpCancelMessageTransport
        {
            HeadResponse = SuccessHead(new string(token)),
        };

        var stdout = new StringWriter();
        var code = await RunAsync(["<abc@example.com>"], fake, stdout);
        Assert.Equal(AdminExitCode.TraceFailed, code);
        Assert.Contains("could not be decrypted", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Peer IP:", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, fake.PostCalls);
    }

    [Fact]
    public async Task PreviousKey_DecryptsRotatedToken()
    {
        var oldProtector = new AesGcmPostingTraceProtector(Convert.FromHexString(PreviousKey));
        var token = oldProtector.Protect(SamplePayload());
        var fake = new FakeNntpCancelMessageTransport
        {
            HeadResponse = SuccessHead(token),
        };

        var stdout = new StringWriter();
        var config = BuildConfiguration(includePrevious: true);
        var code = await AdminApp.RunAsync(
            ["<abc@example.com>"],
            stdout,
            new StringWriter(),
            config,
            fake);
        Assert.Equal(AdminExitCode.Success, code);
        Assert.Contains("198.18.0.66", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidToken_IsRejected()
    {
        var fake = new FakeNntpCancelMessageTransport
        {
            HeadResponse = SuccessHead("not-a-token"),
        };

        var code = await RunAsync(["<abc@example.com>"], fake);
        Assert.Equal(AdminExitCode.TraceFailed, code);
    }

    [Fact]
    public async Task ServerRejectsCancel_ReportsFailure()
    {
        var protector = CreateProtector();
        var fake = new FakeNntpCancelMessageTransport
        {
            HeadResponse = SuccessHead(protector.Protect(SamplePayload())),
            PostResponse = new PostExchange
            {
                Code = 441,
                StatusLine = "441 Posting failed",
            },
        };

        var code = await RunAsync(["--cancel", "<abc@example.com>"], fake);
        Assert.Equal(AdminExitCode.CancelFailed, code);
        Assert.Equal(1, fake.PostCalls);
    }

    [Fact]
    public async Task InspectDisplaysHeadersAndDecryptedTrace()
    {
        var protector = CreateProtector();
        var token = protector.Protect(SamplePayload());
        var fake = new FakeNntpCancelMessageTransport
        {
            HeadResponse = SuccessHead(token),
        };

        var stdout = new StringWriter();
        var code = await RunAsync(["<abc@example.com>"], fake, stdout);
        Assert.Equal(AdminExitCode.Success, code);
        var text = stdout.ToString();
        Assert.Contains("Article headers", text, StringComparison.Ordinal);
        Assert.Contains("From: poster@example.com", text, StringComparison.Ordinal);
        Assert.Contains("Decrypted X-Trace", text, StringComparison.Ordinal);
        Assert.Contains("Peer Port:     54321", text, StringComparison.Ordinal);
        Assert.Contains("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", text, StringComparison.Ordinal);
        Assert.Contains("Authenticated user: (unauthenticated)", text, StringComparison.Ordinal);
        Assert.DoesNotContain(TestKey, text, StringComparison.Ordinal);
    }

    private Task<int> RunAsync(string[] args, FakeNntpCancelMessageTransport fake, StringWriter? stdout = null) =>
        AdminApp.RunAsync(args, stdout ?? new StringWriter(), new StringWriter(), BuildConfiguration(), fake);

    private IConfiguration BuildConfiguration(
        bool includePrevious = false,
        bool includeCredentials = true,
        bool includePgp = true)
    {
        var values = new Dictionary<string, string?>
        {
            ["NntpCancelMessage:Host"] = "127.0.0.1",
            ["NntpCancelMessage:UseTls"] = "false",
            ["NntpCancelMessage:Port"] = "1199",
            ["NntpCancelMessage:From"] = "newsmaster@usenet.ninja",
            ["BindPort"] = "1199",
            ["Nntpd:XTraceKey"] = TestKey,
        };
        if (includeCredentials)
        {
            values["Nntpd:NewsmasterUser"] = "newsmaster";
            values["Nntpd:NewsmasterPassword"] = "unit-test-newsmaster-password";
        }

        if (includePrevious)
        {
            values["Nntpd:XTracePreviousKey"] = PreviousKey;
        }

        if (includePgp)
        {
            values["NntpCancelMessage:Pgp:Enabled"] = "true";
            values["NntpCancelMessage:Pgp:PrivateKeyPath"] = _pgp.Path;
            values["NntpCancelMessage:Pgp:PrivateKeyPassphrase"] = TestPgpKeys.Passphrase;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static AesGcmPostingTraceProtector CreateProtector() =>
        new(Convert.FromHexString(TestKey));

    private static PostingTracePayload SamplePayload() =>
        new(
            IPAddress.Parse("198.18.0.66"),
            54321,
            new DateTimeOffset(2026, 9, 25, 14, 0, 0, TimeSpan.Zero),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

    private static HeadExchange SuccessHead(string? token) =>
        new()
        {
            Code = 221,
            StatusLine = "221 0 <abc@example.com>",
            Headers =
            [
                "From: poster@example.com",
                "Newsgroups: misc.test",
                "Subject: test",
                "Message-ID: <abc@example.com>",
                .. token is null ? Array.Empty<string>() : ["X-Trace: " + token],
            ],
        };
}
