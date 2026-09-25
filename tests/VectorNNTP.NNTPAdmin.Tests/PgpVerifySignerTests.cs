using System.Text;
using VectorNNTP.NNTPAdmin;
using VectorNNTP.NNTPAdmin.PgpVerify;

namespace VectorNNTP.NNTPAdmin.Tests;

public sealed class PgpVerifySignerTests : IClassFixture<SharedPgpKeyFixture>
{
    private readonly SharedPgpKeyFixture _pgp;

    public PgpVerifySignerTests(SharedPgpKeyFixture pgp)
    {
        _pgp = pgp;
    }

    [Fact]
    public void Sign_VerifiesAgainstIndependentlyBuiltCanonicalBytes()
    {
        using var signer = CreateSigner(_pgp.Path);
        Assert.True(CancelArticleBuilder.TryBuild(
            "<original@example.com>",
            "misc.test,alt.test",
            "newsmaster@usenet.ninja",
            new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero),
            out var unsigned,
            out var cancelId,
            out _));
        Assert.True(signer.TrySignArticle(unsigned, out var signed, out _));
        Assert.Contains("X-PGP-Sig:", signed, StringComparison.Ordinal);
        Assert.DoesNotContain("cmsg", signed, StringComparison.Ordinal);
        Assert.Contains("Newsgroups: misc.test,alt.test\r\n", signed, StringComparison.Ordinal);
        Assert.DoesNotContain("Path:", signed, StringComparison.Ordinal);
        Assert.DoesNotContain("Injection-Date:", signed, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Trace:", signed, StringComparison.Ordinal);

        Assert.True(PgpVerifySigner.TryExtractSignatureBlock(signed, out _, out var headerList, out var armored));
        Assert.Equal("Subject,Control,Message-ID,Date,From,Sender", headerList);

        var parsed = PgpVerifyArticleParser.Parse(signed);
        var expectedCanonical = Encoding.UTF8.GetBytes(
            "X-Signed-Headers: Subject,Control,Message-ID,Date,From,Sender\n" +
            "Subject: cancel <original@example.com>\n" +
            "Control: cancel <original@example.com>\n" +
            "Message-ID: " + cancelId + "\n" +
            "Date: Fri, 25 Sep 2026 12:00:00 +0000\n" +
            "From: newsmaster@usenet.ninja\n" +
            "Sender:\n" +
            "\n" +
            "This is an administrative cancellation of <original@example.com>.\n");
        Assert.True(PgpVerifyVerifier.VerifyDetached(expectedCanonical, armored, _pgp.Key.PublicArmored));
        Assert.Equal(expectedCanonical, PgpVerifyCanonicalizer.CanonicalizeArticle(signed, PgpVerifyCanonicalizer.CancelSignedHeaderNames));
        Assert.True(PgpVerifyVerifier.TryVerify(signed, _pgp.Key.PublicArmored, PgpVerifyCanonicalizer.CancelSignedHeaderNames));
    }

    [Fact]
    public void MutatingSignedFields_FailsVerification_UnsignedFieldsDoNot()
    {
        using var signer = CreateSigner(_pgp.Path);
        Assert.True(CancelArticleBuilder.TryBuild(
            "<original@example.com>",
            "misc.test,alt.test",
            "newsmaster@usenet.ninja",
            new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero),
            out var unsigned,
            out _,
            out _));
        Assert.True(signer.TrySignArticle(unsigned, out var signed, out _));

        Assert.False(Verify(ReplaceHeaderValue(signed, "Control", "cancel <other@example.com>")));
        Assert.False(Verify(ReplaceHeaderValue(signed, "Message-ID", "<mutated@example.com>")));
        Assert.False(Verify(ReplaceHeaderValue(signed, "Date", "Fri, 01 Jan 1999 00:00:00 +0000")));
        Assert.False(Verify(ReplaceHeaderValue(signed, "From", "other@example.com")));
        Assert.False(Verify(ReplaceHeaderValue(signed, "Subject", "cancel-mutated <original@example.com>")));
        Assert.False(Verify(signed.Replace(
            "This is an administrative cancellation of <original@example.com>.",
            "This is a mutated body.",
            StringComparison.Ordinal)));
        Assert.False(Verify(InsertHeaderBeforeBody(signed, "Sender: evil@example.com")));

        Assert.True(Verify(ReplaceHeaderValue(signed, "Newsgroups", "alt.other")));
        Assert.True(Verify(InsertHeaderBeforeBody(signed, "Path: .POSTED")));
        Assert.True(Verify(InsertHeaderBeforeBody(signed, "Injection-Date: Fri, 25 Sep 2026 12:00:01 +0000")));
        Assert.True(Verify(InsertHeaderBeforeBody(signed, "Injection-Info: nntpd01.usenet.ninja; logging-data=\"<x@example.com>\"")));
        Assert.True(Verify(InsertHeaderBeforeBody(signed, "X-Trace: v1.not-a-real-token")));
    }

    [Fact]
    public void WrongPublicKey_FailsVerification()
    {
        var other = TestPgpKeys.CreateRsa("other <other@example.com>", seed: 3);
        using var signer = CreateSigner(_pgp.Path);
        Assert.True(CancelArticleBuilder.TryBuild(
            "<original@example.com>",
            "misc.test",
            "newsmaster@usenet.ninja",
            DateTimeOffset.UnixEpoch,
            out var unsigned,
            out _,
            out _));
        Assert.True(signer.TrySignArticle(unsigned, out var signed, out _));
        Assert.False(PgpVerifyVerifier.TryVerify(
            signed,
            other.PublicArmored,
            PgpVerifyCanonicalizer.CancelSignedHeaderNames));
    }

    [Fact]
    public void WrongPassphrase_FailsWithoutEchoingSecret()
    {
        Assert.False(PgpVerifySigner.TryCreate(
            new PgpOptions
            {
                Enabled = true,
                PrivateKeyPath = _pgp.Path,
                PrivateKeyPassphrase = "wrong-passphrase",
            },
            out _,
            out var error));
        Assert.Contains("did not unlock", error, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-passphrase", error, StringComparison.Ordinal);
        Assert.DoesNotContain(TestPgpKeys.Passphrase, error, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingKeyFile_Fails()
    {
        Assert.False(PgpVerifySigner.TryCreate(
            new PgpOptions
            {
                Enabled = true,
                PrivateKeyPath = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".asc"),
                PrivateKeyPassphrase = TestPgpKeys.Passphrase,
            },
            out _,
            out var error));
        Assert.Contains("does not exist", error, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidKeyFile_Fails()
    {
        var path = Path.Combine(Path.GetTempPath(), "vectornntp-pgp-invalid-" + Guid.NewGuid().ToString("N") + ".asc");
        File.WriteAllText(path, "not-a-pgp-key");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        try
        {
            Assert.False(PgpVerifySigner.TryCreate(
                new PgpOptions { Enabled = true, PrivateKeyPath = path, PrivateKeyPassphrase = "x" },
                out _,
                out var error));
            Assert.Contains("parsed", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MultipleKeys_RequireKeyId()
    {
        var second = TestPgpKeys.CreateRsa("two <two@example.com>", seed: 6);
        var path = TestPgpKeys.WriteSecretFile(_pgp.Key, second.SecretArmored);
        try
        {
            Assert.False(PgpVerifySigner.TryCreate(
                new PgpOptions { Enabled = true, PrivateKeyPath = path, PrivateKeyPassphrase = TestPgpKeys.Passphrase },
                out _,
                out var error));
            Assert.Contains("KeyId is required", error, StringComparison.Ordinal);

            Assert.False(PgpVerifySigner.TryCreate(
                new PgpOptions
                {
                    Enabled = true,
                    PrivateKeyPath = path,
                    PrivateKeyPassphrase = TestPgpKeys.Passphrase,
                    KeyId = "DEADBEEFDEADBEEF",
                },
                out _,
                out var missing));
            Assert.Contains("did not match", missing, StringComparison.Ordinal);

            Assert.True(PgpVerifySigner.TryCreate(
                new PgpOptions
                {
                    Enabled = true,
                    PrivateKeyPath = path,
                    PrivateKeyPassphrase = TestPgpKeys.Passphrase,
                    KeyId = _pgp.Key.Fingerprint,
                },
                out var signer,
                out _));
            using (signer)
            {
                Assert.Equal(_pgp.Key.Fingerprint, signer.Identity.Fingerprint);
                Assert.Equal("VectorNNTP test <newsmaster@usenet.ninja>", signer.Identity.UserId);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Disabled_FailsCreate()
    {
        Assert.False(PgpVerifySigner.TryCreate(new PgpOptions { Enabled = false }, out _, out var error));
        Assert.Contains("CANCEL requires PGP signing", error, StringComparison.Ordinal);
    }

    private static PgpVerifySigner CreateSigner(string path)
    {
        Assert.True(PgpVerifySigner.TryCreate(
            new PgpOptions
            {
                Enabled = true,
                PrivateKeyPath = path,
                PrivateKeyPassphrase = TestPgpKeys.Passphrase,
            },
            out var signer,
            out var error),
            error);
        return signer;
    }

    private bool Verify(string article) =>
        PgpVerifyVerifier.TryVerify(article, _pgp.Key.PublicArmored, PgpVerifyCanonicalizer.CancelSignedHeaderNames);

    private static string ReplaceHeaderValue(string article, string name, string value)
    {
        var prefix = name + ": ";
        var start = article.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = article.IndexOf("\r\n", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return string.Concat(article.AsSpan(0, start), prefix, value, article.AsSpan(end));
    }

    private static string InsertHeaderBeforeBody(string article, string headerLine)
    {
        var separator = article.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(separator >= 0);
        return string.Concat(article.AsSpan(0, separator), "\r\n", headerLine, article.AsSpan(separator));
    }
}
