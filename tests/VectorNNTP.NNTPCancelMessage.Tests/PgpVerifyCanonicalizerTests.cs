using System.Text;
using VectorNNTP.NNTPCancelMessage.PgpVerify;

namespace VectorNNTP.NNTPCancelMessage.Tests;

public sealed class PgpVerifyCanonicalizerTests
{
    [Fact]
    public void FormatCutHereExample_PreservesListedOrderIncludingDuplicateFrom()
    {
        var names = new[] { "From", "Subject", "Control", "Message-ID", "Date", "From", "Sender" };
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["From"] = "ADDRESS",
            ["Subject"] = "cmsg newgroup GROUP",
            ["Control"] = "newgroup GROUP",
            ["Message-ID"] = "<MSGID>",
            ["Date"] = "DATE",
            ["Sender"] = "ADDRESS",
        };

        var actual = PgpVerifyCanonicalizer.Canonicalize(names, values, "BODY TEXT\n");
        var expected = Encoding.UTF8.GetBytes(
            "X-Signed-Headers: From,Subject,Control,Message-ID,Date,From,Sender\n" +
            "From: ADDRESS\n" +
            "Subject: cmsg newgroup GROUP\n" +
            "Control: newgroup GROUP\n" +
            "Message-ID: <MSGID>\n" +
            "Date: DATE\n" +
            "From: ADDRESS\n" +
            "Sender: ADDRESS\n" +
            "\n" +
            "BODY TEXT\n");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FormatExample_MatchesExactCanonicalBytes()
    {
        var names = new[] { "Subject", "Control", "Message-ID", "Date", "From", "Sender" };
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Subject"] = "cmsg newgroup GROUP",
            ["Control"] = "newgroup GROUP",
            ["Message-ID"] = "<MSGID>",
            ["Date"] = "DATE",
            ["From"] = "ADDRESS",
            ["Sender"] = "ADDRESS",
        };

        var actual = PgpVerifyCanonicalizer.Canonicalize(names, values, "BODY TEXT\n");
        var expected = Encoding.UTF8.GetBytes(
            "X-Signed-Headers: Subject,Control,Message-ID,Date,From,Sender\n" +
            "Subject: cmsg newgroup GROUP\n" +
            "Control: newgroup GROUP\n" +
            "Message-ID: <MSGID>\n" +
            "Date: DATE\n" +
            "From: ADDRESS\n" +
            "Sender: ADDRESS\n" +
            "\n" +
            "BODY TEXT\n");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MissingSender_IsInnEmptyHeaderWithoutColonSpace()
    {
        var names = PgpVerifyCanonicalizer.CancelSignedHeaderNames;
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Subject"] = "cancel <a@example.com>",
            ["Control"] = "cancel <a@example.com>",
            ["Message-ID"] = "<c@example.com>",
            ["Date"] = "Fri, 25 Sep 2026 12:00:00 +0000",
            ["From"] = "newsmaster@usenet.ninja",
        };

        var actual = Encoding.UTF8.GetString(PgpVerifyCanonicalizer.Canonicalize(names, values, "body\n"));
        Assert.Contains("Sender:\n", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("Sender: \n", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("Sender:\n\nSender", actual, StringComparison.Ordinal);
        Assert.StartsWith("X-Signed-Headers: Subject,Control,Message-ID,Date,From,Sender\n", actual, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', actual);

        var senderLine = Encoding.ASCII.GetBytes("Sender:\n");
        Assert.Contains(senderLine, Encoding.UTF8.GetBytes(actual));
    }

    [Fact]
    public void EmptySenderColonSpaceAndColonTab_BothBecomeSenderColonLf()
    {
        Assert.Equal("Sender:\n", PgpVerifyCanonicalizer.StripTrailingSpAndHtBeforeLf("Sender: \n"));
        Assert.Equal("Sender:\n", PgpVerifyCanonicalizer.StripTrailingSpAndHtBeforeLf("Sender:\t\n"));

        var names = new[] { "Sender" };
        var space = Encoding.UTF8.GetString(PgpVerifyCanonicalizer.Canonicalize(names, new Dictionary<string, string>(StringComparer.Ordinal), string.Empty));
        var tab = Encoding.UTF8.GetString(PgpVerifyCanonicalizer.Canonicalize(
            names,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Sender"] = "\t" },
            string.Empty));
        Assert.Equal("X-Signed-Headers: Sender\nSender:\n\n", space);
        Assert.Equal("X-Signed-Headers: Sender\nSender:\n\n", tab);
    }

    [Fact]
    public void TrailingHorizontalWhitespace_IsStripped()
    {
        var names = new[] { "From" };
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["From"] = "a@b.com  \t" };
        var actual = Encoding.UTF8.GetString(PgpVerifyCanonicalizer.Canonicalize(names, values, "line  \n"));
        Assert.Equal("X-Signed-Headers: From\nFrom: a@b.com\n\nline\n", actual);
    }

    [Fact]
    public void InternalAndLeadingWhitespace_AreNotAltered()
    {
        var names = new[] { "From", "Subject" };
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["From"] = "  leading@example.com",
            ["Subject"] = "cancel  two spaces",
        };

        var actual = Encoding.UTF8.GetString(PgpVerifyCanonicalizer.Canonicalize(names, values, "hello  world\n keep\n"));
        Assert.Contains("From:   leading@example.com\n", actual, StringComparison.Ordinal);
        Assert.Contains("Subject: cancel  two spaces\n", actual, StringComparison.Ordinal);
        Assert.Contains("hello  world\n", actual, StringComparison.Ordinal);
        Assert.Contains(" keep\n", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("hello world\n", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void BodyContent_IsPreservedExceptTrailingSpHtBeforeLf()
    {
        var names = new[] { "From" };
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["From"] = "a@b.com" };
        var actual = Encoding.UTF8.GetString(PgpVerifyCanonicalizer.Canonicalize(names, values, "alpha\t \nbravo\n"));
        Assert.Equal("X-Signed-Headers: From\nFrom: a@b.com\n\nalpha\nbravo\n", actual);
    }

    [Fact]
    public void ArticleCrlf_IsCanonicalizedToLf()
    {
        var article =
            "From: newsmaster@usenet.ninja\r\n" +
            "Subject: cancel <a@example.com>\r\n" +
            "Control: cancel <a@example.com>\r\n" +
            "Message-ID: <c@example.com>\r\n" +
            "Date: Fri, 25 Sep 2026 12:00:00 +0000\r\n" +
            "Newsgroups: misc.test\r\n" +
            "\r\n" +
            "This is an administrative cancellation of <a@example.com>.\r\n";
        var canonical = PgpVerifyCanonicalizer.CanonicalizeArticle(article, PgpVerifyCanonicalizer.CancelSignedHeaderNames);
        var text = Encoding.UTF8.GetString(canonical);
        Assert.Contains("Sender:\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Sender: \n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Newsgroups:", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', text);
        Assert.Contains("This is an administrative cancellation of <a@example.com>.\n", text, StringComparison.Ordinal);
    }
}
