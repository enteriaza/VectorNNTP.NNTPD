using VectorNNTP.NNTPAdmin;
using VectorNNTP.NNTPD.Session.Commands.Posting;

namespace VectorNNTP.NNTPAdmin.Tests;

public sealed class CancelArticleBuilderTests
{
    [Fact]
    public void BuildsDistinctCancelArticleForOriginalMessageId()
    {
        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        Assert.True(CancelArticleBuilder.TryBuild(
            "<original@example.com>",
            "misc.test",
            "newsmaster@usenet.ninja",
            now,
            out var article,
            out var cancelId,
            out _));

        Assert.NotEqual("<original@example.com>", cancelId);
        Assert.StartsWith("<", cancelId, StringComparison.Ordinal);
        Assert.EndsWith("@usenet.ninja>", cancelId, StringComparison.Ordinal);
        Assert.Contains("Control: cancel <original@example.com>\r\n", article, StringComparison.Ordinal);
        Assert.Contains("Message-ID: " + cancelId + "\r\n", article, StringComparison.Ordinal);
        Assert.DoesNotContain("Message-ID: <original@example.com>", article, StringComparison.Ordinal);
        Assert.Contains("Subject: cancel <original@example.com>\r\n", article, StringComparison.Ordinal);
        Assert.DoesNotContain("cmsg", article, StringComparison.Ordinal);
        Assert.Contains("Newsgroups: misc.test\r\n", article, StringComparison.Ordinal);
        Assert.Contains("From: newsmaster@usenet.ninja\r\n", article, StringComparison.Ordinal);
        Assert.Contains("Date: " + PostRfcDate.Format(now) + "\r\n", article, StringComparison.Ordinal);
        Assert.Contains(
            "\r\n\r\nThis is an administrative cancellation of <original@example.com>.\r\n",
            article,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Path:", article, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Trace:", article, StringComparison.Ordinal);
        Assert.DoesNotContain("Injection-Date:", article, StringComparison.Ordinal);
        Assert.DoesNotContain("X-PGP-Sig:", article, StringComparison.Ordinal);
        Assert.DoesNotContain("Sender:", article, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingNewsgroups_Fails()
    {
        Assert.False(CancelArticleBuilder.TryBuild(
            "<original@example.com>",
            "",
            "newsmaster@usenet.ninja",
            DateTimeOffset.UtcNow,
            out _,
            out _,
            out var error));
        Assert.Contains("Newsgroups", error, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleNewsgroups_ArePreservedExactly()
    {
        Assert.True(CancelArticleBuilder.TryBuild(
            "<original@example.com>",
            "misc.test,alt.test",
            "newsmaster@usenet.ninja",
            DateTimeOffset.UtcNow,
            out var article,
            out _,
            out _));
        Assert.Contains("Newsgroups: misc.test,alt.test\r\n", article, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not a group")]
    [InlineData("misc.test,,alt.test")]
    [InlineData(".")]
    public void InvalidNewsgroups_Fail(string groups)
    {
        Assert.False(CancelArticleBuilder.TryBuild(
            "<original@example.com>",
            groups,
            "newsmaster@usenet.ninja",
            DateTimeOffset.UtcNow,
            out _,
            out _,
            out var error));
        Assert.Contains("Newsgroups", error, StringComparison.Ordinal);
    }
}
