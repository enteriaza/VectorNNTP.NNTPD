using System.Text;
using VectorNNTP.NNTPCancelMessage;
using VectorNNTP.NNTPCancelMessage.PgpVerify;

namespace VectorNNTP.NNTPCancelMessage.Tests;

public sealed class InnPgpverifyInteropTests : IClassFixture<SharedPgpKeyFixture>
{
    private readonly SharedPgpKeyFixture _pgp;

    public InnPgpverifyInteropTests(SharedPgpKeyFixture pgp)
    {
        _pgp = pgp;
    }

    [Fact]
    public void ProductionCancel_VerifiesWithInnPgpverify131()
    {
        using var inn = InnPgpverifyProcess.Create(_pgp.Key.PublicArmored);
        var signed = SignProductionCancel(out var cancelId);
        AssertProductionArticleShape(signed, cancelId);

        var canonical = Encoding.UTF8.GetString(
            PgpVerifyCanonicalizer.CanonicalizeArticle(signed, PgpVerifyCanonicalizer.CancelSignedHeaderNames));
        Assert.Contains("Sender:\n", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("Sender: \n", canonical, StringComparison.Ordinal);

        var result = inn.Verify(signed);
        if (result.ExitCode != 0)
        {
            var diagnostic = inn.VerifyWithTestOutput(signed);
            Assert.Fail(
                "INN pgpverify 1.31 rejected a production VectorNNTP CANCEL." + Environment.NewLine +
                "exit=" + result.ExitCode + Environment.NewLine +
                "stdout=" + result.Stdout + Environment.NewLine +
                "stderr=" + result.Stderr + Environment.NewLine +
                "test-stdout=" + diagnostic.Stdout + Environment.NewLine +
                "canonical=" + Convert.ToHexString(Encoding.UTF8.GetBytes(canonical)));
        }

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void InnPgpverify_RejectsMutationsToSignedFields()
    {
        using var inn = InnPgpverifyProcess.Create(_pgp.Key.PublicArmored);
        var signed = SignProductionCancel(out _);

        AssertInnFails(inn, ReplaceHeaderValue(signed, "Subject", "cancel <mutated@example.com>"), "Subject");
        AssertInnFails(inn, ReplaceHeaderValue(signed, "Control", "cancel <mutated@example.com>"), "Control");
        AssertInnFails(inn, ReplaceHeaderValue(signed, "Message-ID", "<mutated@example.com>"), "Message-ID");
        AssertInnFails(inn, ReplaceHeaderValue(signed, "Date", "Fri, 01 Jan 1999 00:00:00 +0000"), "Date");
        AssertInnFails(inn, ReplaceHeaderValue(signed, "From", "other@example.com"), "From");
        AssertInnFails(
            inn,
            signed.Replace(
                "This is an administrative cancellation of <original@example.com>.",
                "This is a mutated body.",
                StringComparison.Ordinal),
            "body");
    }

    [Fact]
    public void InnPgpverify_AcceptsMutationsToUnsignedFields()
    {
        using var inn = InnPgpverifyProcess.Create(_pgp.Key.PublicArmored);
        var signed = SignProductionCancel(out _);

        AssertInnSucceeds(inn, ReplaceHeaderValue(signed, "Newsgroups", "alt.other"), "Newsgroups");
        AssertInnSucceeds(inn, InsertHeaderBeforeBody(signed, "Path: .POSTED"), "Path");
        AssertInnSucceeds(
            inn,
            InsertHeaderBeforeBody(signed, "Injection-Date: Fri, 25 Sep 2026 12:00:01 +0000"),
            "Injection-Date");
        AssertInnSucceeds(
            inn,
            InsertHeaderBeforeBody(signed, "Injection-Info: nntpd01.usenet.ninja; logging-data=\"<x@example.com>\""),
            "Injection-Info");
        AssertInnSucceeds(inn, InsertHeaderBeforeBody(signed, "X-Trace: v1.not-a-real-token"), "X-Trace");
    }

    private string SignProductionCancel(out string cancelId)
    {
        using var signer = CreateSigner();
        Assert.True(CancelArticleBuilder.TryBuild(
            "<original@example.com>",
            "misc.test",
            "newsmaster@usenet.ninja",
            new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero),
            out var unsigned,
            out cancelId,
            out var buildError),
            buildError);
        Assert.True(signer.TrySignArticle(unsigned, out var signed, out var signError), signError);
        return signed;
    }

    private static void AssertProductionArticleShape(string signed, string cancelId)
    {
        Assert.Contains("Subject: cancel <original@example.com>\r\n", signed, StringComparison.Ordinal);
        Assert.Contains("Control: cancel <original@example.com>\r\n", signed, StringComparison.Ordinal);
        Assert.Contains("Message-ID: " + cancelId + "\r\n", signed, StringComparison.Ordinal);
        Assert.Contains("Date: Fri, 25 Sep 2026 12:00:00 +0000\r\n", signed, StringComparison.Ordinal);
        Assert.Contains("From: newsmaster@usenet.ninja\r\n", signed, StringComparison.Ordinal);
        Assert.DoesNotContain("Sender:", signed, StringComparison.Ordinal);
        Assert.Contains(
            "This is an administrative cancellation of <original@example.com>.\r\n",
            signed,
            StringComparison.Ordinal);
        Assert.Contains("X-PGP-Sig:", signed, StringComparison.Ordinal);
        Assert.True(PgpVerifySigner.TryExtractSignatureBlock(signed, out _, out var headerList, out var armored));
        Assert.Equal("Subject,Control,Message-ID,Date,From,Sender", headerList);
        Assert.Contains("-----BEGIN PGP SIGNATURE-----", armored, StringComparison.Ordinal);
    }

    private PgpVerifySigner CreateSigner()
    {
        Assert.True(PgpVerifySigner.TryCreate(
            new PgpOptions
            {
                Enabled = true,
                PrivateKeyPath = _pgp.Path,
                PrivateKeyPassphrase = TestPgpKeys.Passphrase,
            },
            out var signer,
            out var error),
            error);
        return signer;
    }

    private static void AssertInnFails(InnPgpverifyProcess inn, string article, string field)
    {
        var result = inn.Verify(article);
        Assert.True(
            result.ExitCode != 0,
            "INN pgpverify unexpectedly accepted a mutation of " + field +
            " (exit 0). stdout=" + result.Stdout + " stderr=" + result.Stderr);
    }

    private static void AssertInnSucceeds(InnPgpverifyProcess inn, string article, string field)
    {
        var result = inn.Verify(article);
        Assert.True(
            result.ExitCode == 0,
            "INN pgpverify rejected unsigned-field mutation of " + field +
            " (exit " + result.ExitCode + "). stdout=" + result.Stdout + " stderr=" + result.Stderr);
    }

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
