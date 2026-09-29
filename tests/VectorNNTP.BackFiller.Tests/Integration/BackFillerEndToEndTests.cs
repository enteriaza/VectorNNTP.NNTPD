using System.Text.Json;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Transport.ArticleTransfer;

namespace VectorNNTP.BackFiller.Tests.Integration;

public sealed class BackFillerEndToEndTests
{
    private static readonly byte[] CanonicalPayload = ArticleWorkTestArticles.Valid();

    [Fact]
    public async Task Successful_article_work_confirms_then_acks_and_vatp_open_serves_artdata()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        harness.EnqueueArticle(CanonicalPayload);
        var channel = new FakeBackFillerRabbitMqChannel(1);

        var outcome = await harness.ProcessCanonicalAsync(channel);

        Assert.Equal(ArticleWorkOutcome.Success, outcome);
        Assert.Equal(ArticleRetentionKind.Retained, harness.Handler.LastRetentionKind);
        var publication = Assert.Single(harness.PublishChannel.Publications);
        using var document = JsonDocument.Parse(publication.Body);
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(ArticleWorkTestDeliveries.CanonicalRequestId, document.RootElement.GetProperty("requestId").GetString());
        Assert.Equal(ArticleWorkTestDeliveries.CanonicalMessageId, document.RootElement.GetProperty("messageId").GetString());
        Assert.Equal("Giganews", document.RootElement.GetProperty("backbone").GetString());
        Assert.Equal("Success", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(harness.Handler.LastCacheUri, document.RootElement.GetProperty("uri").GetString());
        Assert.Equal(ArticleWorkTestDeliveries.CanonicalCorrelationId, publication.CorrelationId);
        Assert.Equal(ArticleWorkTestDeliveries.CanonicalReplyTo, publication.RoutingKey);
        Assert.Equal(ArticleWorkResponseWireProtocol.JsonContentType, publication.ContentType);
        Assert.Equal(ArticleWorkTestDeliveries.CanonicalRequestId, publication.RequestIdHeader);
        Assert.Equal(ArticleWorkResponseWireProtocol.ExpirationMilliseconds, publication.ExpirationMilliseconds);
        var settlement = Assert.Single(channel.Settlements);
        Assert.True(settlement.Acknowledge);
        Assert.Equal(7UL, settlement.DeliveryTag);
        var destuffed = harness.Handler.LastPayload;
        Assert.NotNull(destuffed);
        Assert.Contains("body"u8, destuffed);
        Assert.Equal(destuffed.Length, harness.Retention.RetainedPayloadBytes);

        var articleIdHex = document.RootElement.GetProperty("articleId").GetString();
        Assert.Equal(ArticleId.HexLength, articleIdHex!.Length);
        Assert.EndsWith('/' + articleIdHex, harness.Handler.LastCacheUri, StringComparison.Ordinal);
        Assert.Equal(articleIdHex, document.RootElement.GetProperty("uri").GetString()!.Split('/')[^1]);
        Assert.True(ArticleId.TryParseLowerHex(articleIdHex, out var articleId));
        Assert.Equal(harness.Handler.LastRecord!.Value.ArtId, articleId);
        using var open = harness.Retention.TryOpenTransfer(
            Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId),
            articleId);
        Assert.Equal(VatpOpenKind.Opened, open.Kind);
        Assert.True(open.Lease!.Record.ArtData.Span.SequenceEqual(destuffed));
        Assert.Equal(destuffed.Length, harness.Retention.RetainedPayloadBytes);
        Assert.Equal(1, harness.Retention.RetainedCount);
    }

    [Fact]
    public async Task Publish_failure_and_confirm_failure_ack_successful_work()
    {
        await using var publishFail = await BackFillerPipelineHarness.StartAsync(FakePublishConfirmBehavior.ThrowOnPublish);
        publishFail.EnqueueArticle(CanonicalPayload);
        var publishChannel = new FakeBackFillerRabbitMqChannel(1);
        var publishOutcome = await publishFail.ProcessCanonicalAsync(publishChannel);
        Assert.Equal(ArticleWorkOutcome.Success, publishOutcome);
        Assert.True(Assert.Single(publishChannel.Settlements).Acknowledge);
        Assert.False(Assert.Single(publishChannel.Settlements).Requeue);

        await using var confirmFail = await BackFillerPipelineHarness.StartAsync(FakePublishConfirmBehavior.Nack);
        confirmFail.EnqueueArticle(CanonicalPayload);
        var confirmChannel = new FakeBackFillerRabbitMqChannel(1);
        var confirmOutcome = await confirmFail.ProcessCanonicalAsync(confirmChannel);
        Assert.Equal(ArticleWorkOutcome.Success, confirmOutcome);
        Assert.True(Assert.Single(confirmChannel.Settlements).Acknowledge);
        Assert.False(Assert.Single(confirmChannel.Settlements).Requeue);
    }

    [Fact]
    public async Task Stale_generation_or_closed_channel_after_confirm_does_not_ack()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        harness.EnqueueArticle(CanonicalPayload);
        var stale = new FakeBackFillerRabbitMqChannel(1);
        var seen = 0;
        var staleOutcome = await harness.ProcessCanonicalAsync(stale, channelStillCurrent: () => Interlocked.Increment(ref seen) == 1);
        Assert.Equal(ArticleWorkOutcome.Success, staleOutcome);
        Assert.Single(harness.PublishChannel.Publications);
        Assert.Empty(stale.Settlements);

        harness.EnqueueArticle(CanonicalPayload);
        var closed = new FakeBackFillerRabbitMqChannel(1) { IsOpen = false };
        var closedOutcome = await harness.ProcessCanonicalAsync(closed, deliveryTag: 8);
        Assert.Equal(ArticleWorkOutcome.Success, closedOutcome);
        Assert.Empty(closed.Settlements);
    }

    [Fact]
    public async Task Confirm_then_ack_failure_does_not_record_an_ack()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        harness.EnqueueArticle(CanonicalPayload);
        var channel = new FakeBackFillerRabbitMqChannel(1)
        {
            AckException = new InvalidOperationException("ack failed"),
        };

        var outcome = await harness.ProcessCanonicalAsync(channel);

        Assert.Equal(ArticleWorkOutcome.Success, outcome);
        Assert.Single(harness.PublishChannel.Publications);
        Assert.Empty(channel.Settlements);
        Assert.Equal(1, harness.Retention.RetainedCount);
    }

    [Fact]
    public async Task Same_message_id_first_wins_and_vatp_open_keeps_original_artdata()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        var first = ArticleWorkTestArticles.Valid(body: "first-body\r\n");
        var second = ArticleWorkTestArticles.Valid(body: "second-body\r\n");
        harness.EnqueueArticle(first);
        harness.EnqueueArticle(second);
        var channel = new FakeBackFillerRabbitMqChannel(1);

        Assert.Equal(ArticleWorkOutcome.Success, await harness.ProcessCanonicalAsync(channel, deliveryTag: 7));
        var destuffedFirst = harness.Handler.LastPayload;
        var firstRecord = harness.Handler.LastRecord;
        Assert.NotNull(destuffedFirst);
        Assert.NotNull(firstRecord);
        Assert.Contains("first-body"u8, destuffedFirst);
        Assert.Equal(ArticleWorkOutcome.Success, await harness.ProcessCanonicalAsync(channel, deliveryTag: 8));
        Assert.Equal(ArticleRetentionKind.AlreadyPresent, harness.Handler.LastRetentionKind);
        Assert.Equal(destuffedFirst.Length, harness.Retention.RetainedPayloadBytes);
        Assert.Equal(1, harness.Retention.RetainedCount);
        Assert.Equal(2, channel.Settlements.Count);
        Assert.All(channel.Settlements, static settlement => Assert.True(settlement.Acknowledge));

        using var open = harness.Retention.TryOpenTransfer(
            Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId),
            firstRecord.Value.ArtId);
        Assert.Equal(VatpOpenKind.Opened, open.Kind);
        Assert.True(open.Lease!.Record.ArtData.Span.SequenceEqual(destuffedFirst));
        Assert.DoesNotContain("second-body"u8, open.Lease.Record.ArtData.Span);
        Assert.Equal(destuffedFirst.Length, harness.Retention.RetainedPayloadBytes);
    }

    [Fact]
    public async Task Distinct_message_ids_keep_independent_identities()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        var first = RetentionTestArticles.RetainPrepared(harness.Retention, "<one@example.invalid>", "payload-a\r\n");
        var second = RetentionTestArticles.RetainPrepared(harness.Retention, "<two@example.invalid>", "payload-b\r\n");
        Assert.NotEqual(first.Record.ArtId, second.Record.ArtId);
        Assert.NotEqual(
            CacheArticleUri.Create("backfiller.test", 1190, first.Record.ArtId),
            CacheArticleUri.Create("backfiller.test", 1190, second.Record.ArtId));
        Assert.Equal(first.Record.ArtSize + second.Record.ArtSize, harness.Retention.RetainedPayloadBytes);
        Assert.Equal(2, harness.Retention.RetainedCount);
    }

    [Fact]
    public async Task Concurrent_retention_of_the_same_identity_keeps_one_owner()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        var candidates = Enumerable.Range(0, 16)
            .Select(i => RetentionTestArticles.Create(ArticleWorkTestDeliveries.CanonicalMessageId, $"body-{i}\r\n"))
            .ToArray();
        var tasks = candidates.Select(async item =>
        {
            await Task.Yield();
            return harness.Retention.RetainCanonical(
                item.MessageId,
                item.RequestId,
                item.Record,
                item.SelectedDateHeaderName);
        });
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(static result => result.Kind == ArticleRetentionKind.Retained));
        Assert.Equal(15, results.Count(static result => result.Kind == ArticleRetentionKind.AlreadyPresent));
        Assert.Equal(1, harness.Retention.RetainedCount);
        var winner = Assert.Single(results, static result => result.Kind == ArticleRetentionKind.Retained);
        Assert.Equal(winner.RetainedPayloadBytes, harness.Retention.RetainedPayloadBytes);
    }

    [Fact]
    public async Task Multiple_request_ids_for_same_article_remain_independently_openable()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        var first = RetentionTestArticles.Create("<multi-req@example.invalid>", "canonical-one\r\n");
        var second = RetentionTestArticles.Create("<multi-req@example.invalid>", "canonical-two\r\n");
        Assert.Equal(ArticleRetentionKind.Retained, harness.Retention.RetainCanonical(
            first.MessageId, first.RequestId, first.Record, first.SelectedDateHeaderName).Kind);
        Assert.Equal(ArticleRetentionKind.AlreadyPresent, harness.Retention.RetainCanonical(
            second.MessageId, second.RequestId, second.Record, second.SelectedDateHeaderName).Kind);

        using (var openA = harness.Retention.TryOpenTransfer(first.RequestId, first.Record.ArtId))
        {
            Assert.Equal(VatpOpenKind.Opened, openA.Kind);
            Assert.True(openA.Lease!.Record.ArtData.Span.SequenceEqual(first.ArtData));
        }

        using var openB = harness.Retention.TryOpenTransfer(second.RequestId, first.Record.ArtId);
        Assert.Equal(VatpOpenKind.Opened, openB.Kind);
        Assert.True(openB.Lease!.Record.ArtData.Span.SequenceEqual(first.ArtData));
        Assert.Equal(first.Record.ArtSize, harness.Retention.RetainedPayloadBytes);
    }

    [Fact]
    public async Task Published_article_work_success_survives_fifo_capacity_pressure()
    {
        await using var probe = await BackFillerPipelineHarness.StartAsync();
        probe.EnqueueArticle(CanonicalPayload);
        Assert.Equal(ArticleWorkOutcome.Success, await probe.ProcessCanonicalAsync(new FakeBackFillerRabbitMqChannel(1)));
        var successBytes = probe.Retention.RetainedPayloadBytes;
        Assert.True(successBytes > 0);
        await probe.DisposeAsync();

        var filler = RetentionTestArticles.Create("<fifo-fill@example.invalid>", "shared-body\r\n");
        var pressure = RetentionTestArticles.Create("<fifo-pres@example.invalid>", "shared-body\r\n");
        Assert.Equal(filler.Record.ArtSize, pressure.Record.ArtSize);

        await using var harness = await BackFillerPipelineHarness.StartAsync(
            maxRetainedPayloadBytes: checked((int)(successBytes + filler.Record.ArtSize)));
        harness.EnqueueArticle(CanonicalPayload);
        var channel = new FakeBackFillerRabbitMqChannel(1);
        Assert.Equal(ArticleWorkOutcome.Success, await harness.ProcessCanonicalAsync(channel));
        Assert.Equal(ArticleRetentionKind.Retained, harness.Handler.LastRetentionKind);
        Assert.Single(harness.PublishChannel.Publications);
        Assert.True(Assert.Single(channel.Settlements).Acknowledge);

        Assert.Equal(
            ArticleRetentionKind.Retained,
            harness.Retention.RetainCanonical(
                filler.MessageId, filler.RequestId, filler.Record, filler.SelectedDateHeaderName).Kind);
        Assert.True(harness.Retention.TryCancelPendingRequest(filler.RequestId));

        Assert.Equal(
            ArticleRetentionKind.Retained,
            harness.Retention.RetainCanonical(
                pressure.MessageId, pressure.RequestId, pressure.Record, pressure.SelectedDateHeaderName).Kind);

        var requestId = Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId);
        var articleId = harness.Handler.LastRecord!.Value.ArtId;
        using var open = harness.Retention.TryOpenTransfer(requestId, articleId);
        Assert.Equal(VatpOpenKind.Opened, open.Kind);
        Assert.True(open.Lease!.Record.ArtData.Span.SequenceEqual(harness.Handler.LastPayload!));
        Assert.Equal(successBytes + pressure.Record.ArtSize, harness.Retention.RetainedPayloadBytes);
    }

    [Fact]
    public async Task Article_work_request_path_does_not_query_mysql()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        var queries = harness.Accounts.QueryCount;
        harness.EnqueueArticle(CanonicalPayload);
        await harness.ProcessCanonicalAsync(new FakeBackFillerRabbitMqChannel(1));
        Assert.Equal(queries, harness.Accounts.QueryCount);
    }
}
