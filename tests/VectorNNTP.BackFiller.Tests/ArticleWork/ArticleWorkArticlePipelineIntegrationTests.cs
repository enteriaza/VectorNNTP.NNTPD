using System.Text;
using System.Text.Json;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;
using VectorNNTP.Common.Articles.Parsing;

namespace VectorNNTP.BackFiller.Tests.ArticleWork;

public sealed class ArticleWorkArticlePipelineIntegrationTests
{
    private const string LocalFqdn = "backfiller01.usenet.ninja";

    [Fact]
    public async Task Valid_normal_article_parses_materializes_retains_publishes_and_acks()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        harness.EnqueueArticle(ArticleWorkTestArticles.Valid());
        var channel = new FakeBackFillerRabbitMqChannel(1);

        var outcome = await harness.ProcessCanonicalAsync(channel);

        Assert.Equal(ArticleWorkOutcome.Success, outcome);
        Assert.Equal(ArticleRetentionKind.Retained, harness.Handler.LastRetentionKind);
        Assert.Equal(1, harness.Retention.RetainedCount);
        Assert.True(Assert.Single(channel.Settlements).Acknowledge);
        var publication = Assert.Single(harness.PublishChannel.Publications);
        using var document = JsonDocument.Parse(publication.Body);
        Assert.Equal("Success", document.RootElement.GetProperty("outcome").GetString());
        AssertCanonicalRetainedArticle(harness.Handler.LastPayload);
    }

    [Fact]
    public async Task Valid_yenc_article_validates_materializes_retains_encoded_body_and_acks()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        harness.EnqueueArticle(ArticleWorkTestArticles.ValidYEnc());
        var channel = new FakeBackFillerRabbitMqChannel(1);

        var outcome = await harness.ProcessCanonicalAsync(channel);

        Assert.Equal(ArticleWorkOutcome.Success, outcome);
        Assert.Equal(ArticleRetentionKind.Retained, harness.Handler.LastRetentionKind);
        Assert.True(Assert.Single(channel.Settlements).Acknowledge);
        var retained = harness.Handler.LastPayload;
        Assert.NotNull(retained);
        Assert.Contains("=ybegin "u8, retained);
        Assert.Contains("=yend "u8, retained);
    }

    [Fact]
    public async Task Invalid_date_nacks_without_requeue_or_retain()
        => await AssertInvalidArticleAsync(ArticleWorkTestArticles.InvalidDate());

    [Fact]
    public async Task Invalid_headers_nacks_without_requeue_or_retain()
        => await AssertInvalidArticleAsync(ArticleWorkTestArticles.InvalidHeaders());

    [Fact]
    public async Task Invalid_message_id_nacks_without_requeue_or_retain()
        => await AssertInvalidArticleAsync(ArticleWorkTestArticles.InvalidMessageId());

    [Fact]
    public async Task Invalid_newsgroups_nacks_without_requeue_or_retain()
        => await AssertInvalidArticleAsync(ArticleWorkTestArticles.InvalidNewsgroups());

    [Fact]
    public async Task Invalid_path_nacks_without_requeue_or_retain()
        => await AssertInvalidArticleAsync(ArticleWorkTestArticles.InvalidPath());

    [Fact]
    public async Task Bad_yenc_crc_nacks_without_requeue_or_retain()
        => await AssertInvalidArticleAsync(ArticleWorkTestArticles.BadYEncCrc());

    [Fact]
    public async Task Invalid_yenc_escape_nacks_without_requeue_or_retain()
        => await AssertInvalidArticleAsync(ArticleWorkTestArticles.InvalidYEncEscape());

    [Fact]
    public async Task Canonical_materialization_exceeding_line_limit_nacks_without_requeue_or_retain()
        => await AssertInvalidArticleAsync(ArticleWorkTestArticles.PathRewriteExceedsLineLimit(LocalFqdn));

    [Fact]
    public async Task Article_message_id_mismatch_nacks_without_requeue_retain_or_success_response()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        harness.EnqueueArticle(ArticleWorkTestArticles.Valid("<actual-provider-identity@example.com>"));
        var channel = new FakeBackFillerRabbitMqChannel(1);

        var outcome = await harness.ProcessCanonicalAsync(channel);

        Assert.Equal(ArticleWorkOutcome.InvalidArticle, outcome);
        Assert.Null(harness.Handler.LastRetentionKind);
        Assert.Equal(0, harness.Retention.RetainedCount);
        var settlement = Assert.Single(channel.Settlements);
        Assert.False(settlement.Acknowledge);
        Assert.False(settlement.Requeue);
        var publication = Assert.Single(harness.PublishChannel.Publications);
        using var document = JsonDocument.Parse(publication.Body);
        Assert.Equal("InvalidArticle", document.RootElement.GetProperty("outcome").GetString());
        Assert.NotEqual("Success", document.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Provider_network_failure_nacks_with_requeue()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        harness.Nntp.ConnectException = new IOException("down");
        var channel = new FakeBackFillerRabbitMqChannel(1);

        var outcome = await harness.ProcessCanonicalAsync(channel);

        Assert.Equal(ArticleWorkOutcome.ProviderFailure, outcome);
        Assert.Equal(0, harness.Retention.RetainedCount);
        var settlement = Assert.Single(channel.Settlements);
        Assert.False(settlement.Acknowledge);
        Assert.True(settlement.Requeue);
        Assert.Empty(harness.PublishChannel.Publications);
    }

    [Fact]
    public async Task Successful_article_acks_when_requester_has_disappeared()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync(FakePublishConfirmBehavior.ThrowOnPublish);
        harness.EnqueueArticle(ArticleWorkTestArticles.Valid());
        var channel = new FakeBackFillerRabbitMqChannel(1);

        var outcome = await harness.ProcessCanonicalAsync(channel);

        Assert.Equal(ArticleWorkOutcome.Success, outcome);
        Assert.Equal(ArticleRetentionKind.Retained, harness.Handler.LastRetentionKind);
        var settlement = Assert.Single(channel.Settlements);
        Assert.True(settlement.Acknowledge);
        Assert.False(settlement.Requeue);
    }

    [Fact]
    public async Task Canonical_retained_article_rewrites_date_and_path_and_keeps_body()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        harness.EnqueueArticle(ArticleWorkTestArticles.Valid(body: "unchanged-body\r\n", extraHeaders: "Path: news.example.org!feed2\r\n"));
        var channel = new FakeBackFillerRabbitMqChannel(1);

        Assert.Equal(ArticleWorkOutcome.Success, await harness.ProcessCanonicalAsync(channel));
        var retained = harness.Handler.LastPayload;
        Assert.NotNull(retained);
        var text = Encoding.ASCII.GetString(retained);
        Assert.Contains($"Date: {ArticleWorkTestArticles.CanonicalUtcDate}\r\n", text, StringComparison.Ordinal);
        Assert.Contains($"Path: news.usenet.ninja!{LocalFqdn}!news.example.org!feed2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Path: news.example.org!feed2\r\n", text, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(text, "news.usenet.ninja"));
        Assert.Contains("unchanged-body", text, StringComparison.Ordinal);
    }

    private static async Task AssertInvalidArticleAsync(byte[] destuffed)
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        harness.EnqueueArticle(destuffed);
        var channel = new FakeBackFillerRabbitMqChannel(1);

        var outcome = await harness.ProcessCanonicalAsync(channel);

        Assert.Equal(ArticleWorkOutcome.InvalidArticle, outcome);
        Assert.Null(harness.Handler.LastRetentionKind);
        Assert.Equal(0, harness.Retention.RetainedCount);
        var settlement = Assert.Single(channel.Settlements);
        Assert.False(settlement.Acknowledge);
        Assert.False(settlement.Requeue);
        var publication = Assert.Single(harness.PublishChannel.Publications);
        using var document = JsonDocument.Parse(publication.Body);
        Assert.Equal("InvalidArticle", document.RootElement.GetProperty("outcome").GetString());
    }

    private static void AssertCanonicalRetainedArticle(byte[]? retained)
    {
        Assert.NotNull(retained);
        var text = Encoding.ASCII.GetString(retained);
        Assert.Contains($"Date: {ArticleWorkTestArticles.CanonicalUtcDate}\r\n", text, StringComparison.Ordinal);
        Assert.Contains($"Path: news.usenet.ninja!{LocalFqdn}", text, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(text, "news.usenet.ninja"));
        Assert.Contains("body", text, StringComparison.Ordinal);
        var parser = new NntpArticleParser(LocalFqdn);
        var parse = parser.Parse(retained);
        Assert.True(parse.IsAccepted);
    }

    [Fact]
    public async Task First_traversal_does_not_duplicate_existing_leading_tracker()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        harness.EnqueueArticle(ArticleWorkTestArticles.Valid(extraHeaders: "Path: news.usenet.ninja!foo\r\n"));
        var channel = new FakeBackFillerRabbitMqChannel(1);

        Assert.Equal(ArticleWorkOutcome.Success, await harness.ProcessCanonicalAsync(channel));
        var text = Encoding.ASCII.GetString(harness.Handler.LastPayload!);
        Assert.Contains($"Path: {LocalFqdn}!news.usenet.ninja!foo", text, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(text, "news.usenet.ninja"));
    }

    [Fact]
    public async Task First_traversal_does_not_duplicate_tracker_that_occurs_later()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        harness.EnqueueArticle(ArticleWorkTestArticles.Valid(extraHeaders: "Path: foo!news.usenet.ninja!bar\r\n"));
        var channel = new FakeBackFillerRabbitMqChannel(1);

        Assert.Equal(ArticleWorkOutcome.Success, await harness.ProcessCanonicalAsync(channel));
        var text = Encoding.ASCII.GetString(harness.Handler.LastPayload!);
        Assert.Contains($"Path: {LocalFqdn}!foo!news.usenet.ninja!bar", text, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(text, "news.usenet.ninja"));
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while (true)
        {
            index = source.IndexOf(value, index, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }

            count++;
            index += value.Length;
        }
    }
}
