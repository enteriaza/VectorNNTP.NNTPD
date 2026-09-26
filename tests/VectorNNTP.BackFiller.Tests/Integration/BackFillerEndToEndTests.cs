using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.Listener;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.Integration;

public sealed class BackFillerEndToEndTests
{
    private static readonly byte[] CanonicalPayload = "From: a@b\r\n\r\nbody"u8.ToArray();

    [Fact]
    public async Task Successful_article_work_confirms_then_acks_and_serves_exact_bytes()
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
        Assert.Equal(ArticleWorkTestDeliveries.CanonicalReplyTo, publication.ReplyTo);
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

        var md5 = ArticleIdentity.FromExactMessageId(ArticleWorkTestDeliveries.CanonicalMessageId).Md5Hex;
        Assert.EndsWith("/" + md5, harness.Handler.LastCacheUri, StringComparison.Ordinal);
        using var lookup = harness.Retention.TryGetByMd5(md5);
        Assert.Equal(ArticleLookupKind.Found, lookup.Kind);
        Assert.True(lookup.Lease!.Payload.Span.SequenceEqual(destuffed));

        var listenerHandler = new CacheListenerRetentionHandler(harness.Retention);
        var transport = new ScriptedCacheListenerTransport();
        await using var session = BackFillerPipelineHarness.CreateListenerSession(transport, listenerHandler);
        var run = session.RunAsync(CancellationToken.None);
        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(11, md5));
        var found = await WaitForFoundAsync(transport, expectedCount: 1);
        Assert.True(found[11].AsSpan().SequenceEqual(destuffed));
        Assert.True(listenerHandler.HoldsLease(11));
        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetReceiptAck(11));
        await ArticleWorkTestDeliveries.WaitUntilAsync(() => listenerHandler.HeldLeaseCount == 0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, harness.Retention.RetainedCount);
        Assert.Equal(destuffed.Length, harness.Retention.RetainedPayloadBytes);
        using var afterAck = harness.Retention.TryGetByMd5(md5);
        Assert.Equal(ArticleLookupKind.Found, afterAck.Kind);
        transport.CompleteInbound();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Publish_failure_and_confirm_failure_never_ack()
    {
        await using var publishFail = await BackFillerPipelineHarness.StartAsync(FakePublishConfirmBehavior.ThrowOnPublish);
        publishFail.EnqueueArticle(CanonicalPayload);
        var publishChannel = new FakeBackFillerRabbitMqChannel(1);
        var publishOutcome = await publishFail.ProcessCanonicalAsync(publishChannel);
        Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, publishOutcome);
        Assert.False(Assert.Single(publishChannel.Settlements).Acknowledge);
        Assert.True(Assert.Single(publishChannel.Settlements).Requeue);

        await using var confirmFail = await BackFillerPipelineHarness.StartAsync(FakePublishConfirmBehavior.Nack);
        confirmFail.EnqueueArticle(CanonicalPayload);
        var confirmChannel = new FakeBackFillerRabbitMqChannel(1);
        var confirmOutcome = await confirmFail.ProcessCanonicalAsync(confirmChannel);
        Assert.Equal(ArticleWorkOutcome.UnexpectedFailure, confirmOutcome);
        Assert.False(Assert.Single(confirmChannel.Settlements).Acknowledge);
        Assert.True(Assert.Single(confirmChannel.Settlements).Requeue);
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
    public async Task Same_message_id_first_wins_and_listener_keeps_original_bytes()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        var first = "From: a@b\r\n\r\nfirst-body"u8.ToArray();
        var second = "From: a@b\r\n\r\nsecond-body"u8.ToArray();
        harness.EnqueueArticle(first);
        harness.EnqueueArticle(second);
        var channel = new FakeBackFillerRabbitMqChannel(1);

        Assert.Equal(ArticleWorkOutcome.Success, await harness.ProcessCanonicalAsync(channel, deliveryTag: 7));
        var destuffedFirst = harness.Handler.LastPayload;
        Assert.NotNull(destuffedFirst);
        Assert.Contains("first-body"u8, destuffedFirst);
        Assert.Equal(ArticleWorkOutcome.Success, await harness.ProcessCanonicalAsync(channel, deliveryTag: 8));
        Assert.Equal(destuffedFirst.Length, harness.Retention.RetainedPayloadBytes);
        Assert.Equal(1, harness.Retention.RetainedCount);
        Assert.Equal(2, channel.Settlements.Count);
        Assert.All(channel.Settlements, static settlement => Assert.True(settlement.Acknowledge));

        var md5 = ArticleIdentity.FromExactMessageId(ArticleWorkTestDeliveries.CanonicalMessageId).Md5Hex;
        var listenerHandler = new CacheListenerRetentionHandler(harness.Retention);
        var transport = new ScriptedCacheListenerTransport();
        await using var session = BackFillerPipelineHarness.CreateListenerSession(transport, listenerHandler);
        var run = session.RunAsync(CancellationToken.None);
        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(1, md5));
        var found = await WaitForFoundAsync(transport, expectedCount: 1);
        Assert.True(found[1].AsSpan().SequenceEqual(destuffedFirst));
        Assert.DoesNotContain("second-body"u8, found[1]);
        transport.CompleteInbound();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Distinct_message_ids_keep_independent_identities()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        var first = "payload-a"u8.ToArray();
        var second = "payload-b"u8.ToArray();
        Assert.Equal(ArticleRetentionKind.Retained, harness.Retention.Retain("<one@example.invalid>", first).Kind);
        Assert.Equal(ArticleRetentionKind.Retained, harness.Retention.Retain("<two@example.invalid>", second).Kind);
        Assert.NotEqual(
            ArticleIdentity.FromExactMessageId("<one@example.invalid>").Md5Hex,
            ArticleIdentity.FromExactMessageId("<two@example.invalid>").Md5Hex);
        Assert.Equal(first.Length + second.Length, harness.Retention.RetainedPayloadBytes);
        Assert.Equal(2, harness.Retention.RetainedCount);
    }

    [Fact]
    public async Task Concurrent_retention_of_the_same_identity_keeps_one_owner()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        var tasks = Enumerable.Range(0, 16).Select(async i =>
        {
            await Task.Yield();
            return harness.Retention.Retain(ArticleWorkTestDeliveries.CanonicalMessageId, [(byte)i]);
        });
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(static result => result.Kind == ArticleRetentionKind.Retained));
        Assert.Equal(15, results.Count(static result => result.Kind == ArticleRetentionKind.AlreadyPresent));
        Assert.Equal(1, harness.Retention.RetainedCount);
        Assert.Equal(1, harness.Retention.RetainedPayloadBytes);
    }

    [Fact]
    public async Task Listener_three_request_correlation_does_not_cross_leases()
    {
        await using var harness = await BackFillerPipelineHarness.StartAsync();
        var a = "article-a"u8.ToArray();
        var b = "article-b"u8.ToArray();
        var c = "article-c"u8.ToArray();
        harness.Retention.Retain("<a@example.invalid>", a);
        harness.Retention.Retain("<b@example.invalid>", b);
        harness.Retention.Retain("<c@example.invalid>", c);
        var md5A = ArticleIdentity.FromExactMessageId("<a@example.invalid>").Md5Hex;
        var md5B = ArticleIdentity.FromExactMessageId("<b@example.invalid>").Md5Hex;
        var md5C = ArticleIdentity.FromExactMessageId("<c@example.invalid>").Md5Hex;
        var handler = new CacheListenerRetentionHandler(harness.Retention);
        var transport = new ScriptedCacheListenerTransport();
        await using var session = BackFillerPipelineHarness.CreateListenerSession(transport, handler);
        var run = session.RunAsync(CancellationToken.None);

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(1, md5A));
        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(2, md5B));
        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(3, md5A));
        var found = await WaitForFoundAsync(transport, expectedCount: 3);
        Assert.True(found[1].AsSpan().SequenceEqual(a));
        Assert.True(found[2].AsSpan().SequenceEqual(b));
        Assert.True(found[3].AsSpan().SequenceEqual(a));
        Assert.Equal(3, handler.HeldLeaseCount);

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetReceiptAck(2));
        await ArticleWorkTestDeliveries.WaitUntilAsync(() => !handler.HoldsLease(2), TimeSpan.FromSeconds(2));
        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetReceiptAck(2));
        await WaitForErrorAsync(transport, ListenerProtocolErrorCode.InvalidRequestId);
        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetReceiptAck(99));
        await WaitForErrorAsync(transport, ListenerProtocolErrorCode.InvalidRequestId);
        Assert.True(handler.HoldsLease(1));
        Assert.True(handler.HoldsLease(3));

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetReceiptAck(1));
        await ArticleWorkTestDeliveries.WaitUntilAsync(() => !handler.HoldsLease(1), TimeSpan.FromSeconds(2));
        Assert.True(handler.HoldsLease(3));
        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetReceiptAck(3));
        await ArticleWorkTestDeliveries.WaitUntilAsync(() => handler.HeldLeaseCount == 0, TimeSpan.FromSeconds(2));
        Assert.Equal(3, harness.Retention.RetainedCount);

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(4, md5C));
        await WaitForFoundAsync(transport, expectedCount: 4);
        transport.CompleteInbound();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, handler.HeldLeaseCount);
        Assert.Equal(3, harness.Retention.RetainedCount);
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

    internal static async Task<Dictionary<uint, byte[]>> WaitForFoundAsync(
        ScriptedCacheListenerTransport transport,
        int expectedCount)
    {
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!safety.IsCancellationRequested)
        {
            var found = new Dictionary<uint, byte[]>();
            foreach (var frame in ReadFrames(transport.Written))
            {
                if (frame.Opcode == ListenerOpcode.GetResponseFound)
                {
                    found[frame.RequestId] = frame.Payload;
                }
            }

            if (found.Count >= expectedCount)
            {
                return found;
            }

            await Task.Delay(10, safety.Token).ConfigureAwait(false);
        }

        throw new TimeoutException($"Did not observe {expectedCount} Found frames.");
    }

    internal static async Task WaitForErrorAsync(
        ScriptedCacheListenerTransport transport,
        ListenerProtocolErrorCode code)
    {
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!safety.IsCancellationRequested)
        {
            foreach (var frame in ReadFrames(transport.Written))
            {
                if (frame.Opcode == ListenerOpcode.GetResponseError
                    && BinaryPrimitives.ReadUInt16BigEndian(frame.Payload) == (ushort)code)
                {
                    return;
                }
            }

            await Task.Delay(10, safety.Token).ConfigureAwait(false);
        }

        throw new TimeoutException($"Did not observe error {code}.");
    }

    internal static async Task WaitForOpcodeAsync(
        ScriptedCacheListenerTransport transport,
        ListenerOpcode opcode)
    {
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!safety.IsCancellationRequested)
        {
            if (ReadFrames(transport.Written).Any(frame => frame.Opcode == opcode))
            {
                return;
            }

            await Task.Delay(10, safety.Token).ConfigureAwait(false);
        }

        throw new TimeoutException($"Did not observe opcode {opcode}.");
    }

    internal static List<(uint RequestId, ListenerOpcode Opcode, byte[] Payload)> ReadFrames(byte[] written)
    {
        var frames = new List<(uint, ListenerOpcode, byte[])>();
        var offset = 0;
        while (offset + ListenerProtocol.HeaderLengthBytes <= written.Length)
        {
            var header = ListenerFrameHeader.ReadFrom(written.AsSpan(offset, ListenerProtocol.HeaderLengthBytes));
            var total = ListenerProtocol.HeaderLengthBytes + (int)header.PayloadLength;
            if (offset + total > written.Length)
            {
                break;
            }

            frames.Add((
                header.RequestId,
                header.Opcode,
                written[(offset + ListenerProtocol.HeaderLengthBytes)..(offset + total)]));
            offset += total;
        }

        return frames;
    }
}
