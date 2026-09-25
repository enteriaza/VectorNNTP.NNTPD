using VectorNNTP.NNTPAdmin;

namespace VectorNNTP.NNTPAdmin.Tests;

public sealed class AdminCliParserTests
{
    [Fact]
    public void MessageIdOnly_IsInspect()
    {
        Assert.True(AdminCliParser.TryParse(["<abc@example.com>"], out var args, out _));
        Assert.Equal("<abc@example.com>", args.MessageId);
        Assert.False(args.Cancel);
    }

    [Fact]
    public void BareMessageId_IsWrappedOnce()
    {
        Assert.True(AdminCliParser.TryParse(["abc@example.com"], out var args, out _));
        Assert.Equal("<abc@example.com>", args.MessageId);
    }

    [Theory]
    [InlineData("--cancel")]
    [InlineData("-cancel")]
    public void CancelFlag_BeforeOrAfterMessageId(string flag)
    {
        Assert.True(AdminCliParser.TryParse([flag, "<abc@example.com>"], out var before, out _));
        Assert.True(before.Cancel);
        Assert.Equal("<abc@example.com>", before.MessageId);

        Assert.True(AdminCliParser.TryParse(["<abc@example.com>", flag], out var after, out _));
        Assert.True(after.Cancel);
        Assert.Equal("<abc@example.com>", after.MessageId);
    }

    [Fact]
    public void MissingMessageId_Fails()
    {
        Assert.False(AdminCliParser.TryParse(["--cancel"], out _, out var error));
        Assert.Contains("Message-ID", error, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyArgs_Fails()
    {
        Assert.False(AdminCliParser.TryParse([], out _, out var error));
        Assert.Contains("Message-ID", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-an-id")]
    [InlineData("<<abc@example.com>>")]
    [InlineData("<>")]
    [InlineData("<no-at>")]
    public void InvalidMessageId_Fails(string raw)
    {
        Assert.False(AdminCliParser.TryParse([raw], out _, out var error));
        Assert.Contains("well-formed", error, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateCancel_Fails()
    {
        Assert.False(AdminCliParser.TryParse(["--cancel", "-cancel", "<abc@example.com>"], out _, out var error));
        Assert.Contains("Duplicate", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoMessageIds_Fails()
    {
        Assert.False(AdminCliParser.TryParse(["<a@b.com>", "<c@d.com>"], out _, out var error));
        Assert.Contains("one Message-ID", error, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownArgument_Fails()
    {
        Assert.False(AdminCliParser.TryParse(["<abc@example.com>", "--explode"], out _, out var error));
        Assert.Contains("Unknown argument", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--no-sign")]
    [InlineData("--unsigned")]
    [InlineData("--skip-signature")]
    public void UnsignedBypassFlags_AreRejected(string flag)
    {
        Assert.False(AdminCliParser.TryParse([flag, "--cancel", "<abc@example.com>"], out _, out var error));
        Assert.Contains("Unknown argument", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_DoesNotRequireMessageId()
    {
        Assert.True(AdminCliParser.TryParse(["--help"], out var args, out _));
        Assert.True(args.Help);
    }

    [Fact]
    public void TlsAndPlaintext_AreMutuallyExclusive()
    {
        Assert.False(AdminCliParser.TryParse(["--tls", "--plaintext", "<abc@example.com>"], out _, out var error));
        Assert.Contains("TLS mode", error, StringComparison.Ordinal);
    }

    [Fact]
    public void HostAndPort_AreCaptured()
    {
        Assert.True(AdminCliParser.TryParse(
            ["--host", "nntpd01.example", "--port", "5633", "--tls", "<abc@example.com>"],
            out var args,
            out _));
        Assert.Equal("nntpd01.example", args.Host);
        Assert.Equal(5633, args.Port);
        Assert.True(args.UseTls);
    }

    [Fact]
    public void UsernameAndPassword_AreCapturedIncludingLeadingDashPassword()
    {
        Assert.True(AdminCliParser.TryParse(
            [
                "--host", "nntpd01.usenet.ninja",
                "--port", "563",
                "--tls",
                "--username", "newsmaster",
                "--password", "-secret-dash",
                "--cancel",
                "<abc@example.com>",
            ],
            out var args,
            out _));
        Assert.Equal("nntpd01.usenet.ninja", args.Host);
        Assert.Equal(563, args.Port);
        Assert.True(args.UseTls);
        Assert.Equal("newsmaster", args.Username);
        Assert.Equal("-secret-dash", args.Password);
        Assert.True(args.Cancel);
    }

    [Fact]
    public void MissingPasswordValue_Fails()
    {
        Assert.False(AdminCliParser.TryParse(
            ["<abc@example.com>", "--password"],
            out _,
            out var error));
        Assert.Contains("--password requires a value", error, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateUsername_Fails()
    {
        Assert.False(AdminCliParser.TryParse(
            ["--username", "a", "--username", "b", "<abc@example.com>"],
            out _,
            out var error));
        Assert.Contains("Duplicate --username", error, StringComparison.Ordinal);
    }
}
