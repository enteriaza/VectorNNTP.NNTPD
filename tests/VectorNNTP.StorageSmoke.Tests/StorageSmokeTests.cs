using System.Text;
using VectorNNTP.StorageSmoke;
using Xunit;

namespace VectorNNTP.StorageSmoke.Tests;

public sealed class StorageSmokeTests
{
    [Fact]
    public void Help_ExplainsBoundsAndThatAcceptedIsNotDurable()
    {
        var error = new StringWriter();
        var parsed = CommandLine.Parse(["help"], error);
        Assert.True(parsed.ShowHelp);
        Assert.Equal(0, parsed.ExitCode);
        Assert.Contains("smoke", CommandLine.HelpText, StringComparison.Ordinal);
        Assert.Contains("soak", CommandLine.HelpText, StringComparison.Ordinal);
        Assert.Contains("not durable spool publication", CommandLine.HelpText, StringComparison.Ordinal);
        Assert.Contains("21600", CommandLine.HelpText, StringComparison.Ordinal);
        Assert.Contains("cache01.usenet.ninja", CommandLine.HelpText, StringComparison.Ordinal);
        Assert.Contains("does not insert nntpd01.usenet.ninja", CommandLine.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void Smoke_RequiresResultsAndUsesDocumentedDefaults()
    {
        var error = new StringWriter();
        var missing = CommandLine.Parse(["smoke"], error);
        Assert.Equal(2, missing.ExitCode);
        Assert.Contains("results directory is required", error.ToString(), StringComparison.Ordinal);

        var parsed = CommandLine.Parse(["smoke", "--results", Path.Combine("vatp", "smoke-1")], new StringWriter());
        Assert.NotNull(parsed.Command);
        var command = parsed.Command;
        Assert.Equal(SmokeMode.Smoke, command.Mode);
        Assert.Equal(4, command.Articles);
        Assert.Equal(4, command.Sessions);
        Assert.Equal(2, command.OutstandingStores);
        Assert.Equal(768_000, command.BodyBytes);
        Assert.Equal("cache01.usenet.ninja", command.Host);
        Assert.Equal(1191, command.Port);
        Assert.EndsWith(Path.Combine("vatp", "smoke-1"), command.ResultsDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Soak_PositionalFormKeepsHarnessOrder()
    {
        var parsed = CommandLine.Parse(
            ["soak", "18000", "4", "768000", Path.Combine("vatp", "soak-1")],
            new StringWriter());
        Assert.NotNull(parsed.Command);
        var command = parsed.Command;
        Assert.Equal(SmokeMode.Soak, command.Mode);
        Assert.Equal(18000, command.Seconds);
        Assert.Equal(4, command.Sessions);
        Assert.Equal(768_000, command.BodyBytes);
        Assert.Equal(2, command.OutstandingStores);
        Assert.Equal("cache01.usenet.ninja", command.Host);
        Assert.Equal(1191, command.Port);
    }

    [Fact]
    public void Soak_NamedFormRequiresSecondsAndRejectsArticles()
    {
        var error = new StringWriter();
        var missing = CommandLine.Parse(["soak", "--results", "vatp-out"], error);
        Assert.Equal(2, missing.ExitCode);
        Assert.Contains("soak requires --seconds", error.ToString(), StringComparison.Ordinal);

        error = new StringWriter();
        var articles = CommandLine.Parse(
            ["soak", "--seconds", "60", "--articles", "4", "--results", "vatp-out"],
            error);
        Assert.Equal(2, articles.ExitCode);
        Assert.Contains("does not take --articles", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("21601")]
    [InlineData("abc")]
    public void Soak_RejectsDurationOutsideRange(string seconds)
    {
        var error = new StringWriter();
        var parsed = CommandLine.Parse(["soak", seconds, "4", "768000", "vatp-out"], error);
        Assert.Equal(2, parsed.ExitCode);
        Assert.Null(parsed.Command);
        Assert.Contains("seconds must be an integer from 1 to 21600", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--sessions", "0")]
    [InlineData("--sessions", "17")]
    [InlineData("--outstanding", "3")]
    [InlineData("--body-bytes", "63")]
    [InlineData("--body-bytes", "2000001")]
    [InlineData("--port", "0")]
    [InlineData("--articles", "0")]
    [InlineData("--host", "")]
    public void Smoke_RejectsInvalidOptions(string name, string value)
    {
        var error = new StringWriter();
        var parsed = CommandLine.Parse(["smoke", "--results", "vatp-out", name, value], error);
        Assert.Equal(2, parsed.ExitCode);
        Assert.Null(parsed.Command);
        Assert.NotEmpty(error.ToString());
    }

    [Fact]
    public void ResultsDirectory_RefusesExistingClientCsv()
    {
        var root = Path.Combine(Path.GetTempPath(), "vectornntp-storage-smoke-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ResultsLayout.ClientCsv), "existing\n");
            var error = new StringWriter();
            var code = ResultsLayout.EnsureFresh(root, error);
            Assert.Equal(2, code);
            Assert.Contains("client.csv", error.ToString(), StringComparison.Ordinal);
            Assert.Equal("existing\n", File.ReadAllText(Path.Combine(root, ResultsLayout.ClientCsv)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Normalize_OffersTrackerThenPeerWithoutAnApplicationHop()
    {
        var body = "x\r\n"u8.ToArray();
        Assert.True(PlacementArticle.TryCreate(body, "smoke-path", out var record, out var failure), failure);
        var text = Encoding.ASCII.GetString(record.ArtData.Span);
        Assert.Contains("Path: news.usenet.ninja!peer.example\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("nntpd01.usenet.ninja", text, StringComparison.Ordinal);
        Assert.DoesNotContain("cache01.usenet.ninja", text, StringComparison.Ordinal);
        Assert.Equal(1, CountHop(text, "news.usenet.ninja"));
        Assert.Equal(1, CountHop(text, "peer.example"));
    }

    private static int CountHop(string article, string hop)
    {
        var start = article.IndexOf("Path: ", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = article.IndexOf("\r\n", start, StringComparison.Ordinal);
        var value = article[(start + "Path: ".Length)..end];
        var count = 0;
        foreach (var token in value.Split('!'))
        {
            if (string.Equals(token, hop, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }
}
