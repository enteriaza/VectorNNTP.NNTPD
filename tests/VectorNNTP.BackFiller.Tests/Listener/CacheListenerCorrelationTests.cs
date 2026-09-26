using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Listener;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.Retention;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.Listener;

public sealed class CacheListenerCorrelationTests
{
    [Fact]
    public async Task Receipt_ack_releases_only_the_matching_outstanding_request()
    {
        var first = "alpha-payload"u8.ToArray();
        var second = "beta-payload"u8.ToArray();
        var (authority, handler, transport, session) = CreateTwoArticleSession(first, second, out var md5A, out var md5B);
        await using var _ = session;
        var run = session.RunAsync(CancellationToken.None);

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(11, md5A));
        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(22, md5B));
        var found = await WaitForFoundAsync(transport, expectedCount: 2);
        Assert.Equal(2, found.Count);
        Assert.Equal(first, found[11]);
        Assert.Equal(second, found[22]);
        Assert.Equal(2, handler.HeldLeaseCount);
        Assert.True(handler.HoldsLease(11));
        Assert.True(handler.HoldsLease(22));

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetReceiptAck(22));
        await ArticleWorkTestDeliveries.WaitUntilAsync(() => !handler.HoldsLease(22), TimeSpan.FromSeconds(2));
        Assert.True(handler.HoldsLease(11));
        Assert.Equal(1, handler.HeldLeaseCount);
        Assert.Equal(2, authority.RetainedCount);

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetReceiptAck(11));
        await ArticleWorkTestDeliveries.WaitUntilAsync(() => handler.HeldLeaseCount == 0, TimeSpan.FromSeconds(2));
        Assert.False(handler.HoldsLease(11));
        Assert.Equal(2, authority.RetainedCount);
        Assert.Equal(2, CountOpcode(transport, ListenerOpcode.GetResponseFound));

        transport.CompleteInbound();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Duplicate_receipt_ack_does_not_release_another_request()
    {
        var first = "keep-a"u8.ToArray();
        var second = "keep-b"u8.ToArray();
        var (authority, handler, transport, session) = CreateTwoArticleSession(first, second, out var md5A, out var md5B);
        await using var _ = session;
        var run = session.RunAsync(CancellationToken.None);

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(11, md5A));
        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(22, md5B));
        await WaitForFoundAsync(transport, expectedCount: 2);

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetReceiptAck(11));
        await ArticleWorkTestDeliveries.WaitUntilAsync(() => !handler.HoldsLease(11), TimeSpan.FromSeconds(2));
        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetReceiptAck(11));
        await WaitForErrorAsync(transport, ListenerProtocolErrorCode.InvalidRequestId);

        Assert.True(handler.HoldsLease(22));
        Assert.Equal(1, handler.HeldLeaseCount);
        Assert.Equal(2, authority.RetainedCount);

        transport.CompleteInbound();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, handler.HeldLeaseCount);
    }

    [Fact]
    public async Task Unknown_receipt_ack_does_not_release_outstanding_leases()
    {
        var first = "still-a"u8.ToArray();
        var second = "still-b"u8.ToArray();
        var (_, handler, transport, session) = CreateTwoArticleSession(first, second, out var md5A, out var md5B);
        await using var _ = session;
        var run = session.RunAsync(CancellationToken.None);

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(11, md5A));
        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(22, md5B));
        await WaitForFoundAsync(transport, expectedCount: 2);

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetReceiptAck(99));
        await WaitForErrorAsync(transport, ListenerProtocolErrorCode.InvalidRequestId);

        Assert.True(handler.HoldsLease(11));
        Assert.True(handler.HoldsLease(22));
        Assert.Equal(2, handler.HeldLeaseCount);

        transport.CompleteInbound();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Connection_close_releases_every_lease_owned_by_that_session()
    {
        var first = "close-a"u8.ToArray();
        var second = "close-b"u8.ToArray();
        var (authority, handler, transport, session) = CreateTwoArticleSession(first, second, out var md5A, out var md5B);
        await using var _ = session;
        var run = session.RunAsync(CancellationToken.None);

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(11, md5A));
        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(22, md5B));
        await WaitForFoundAsync(transport, expectedCount: 2);
        Assert.Equal(2, handler.HeldLeaseCount);

        transport.CompleteInbound();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, handler.HeldLeaseCount);
        Assert.False(handler.HoldsLease(11));
        Assert.False(handler.HoldsLease(22));
        Assert.Equal(2, authority.RetainedCount);
    }

    [Fact]
    public async Task Concurrent_requests_for_the_same_article_hold_independent_leases()
    {
        var payload = "shared-article"u8.ToArray();
        var authority = ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024 * 1024);
        authority.Retain(ArticleWorkTestDeliveries.CanonicalMessageId, payload);
        var md5 = ArticleIdentity.FromExactMessageId(ArticleWorkTestDeliveries.CanonicalMessageId).Md5Hex;
        var handler = new CacheListenerRetentionHandler(authority);
        var transport = new ScriptedCacheListenerTransport();
        await using var session = CreateSession(transport, handler);
        var run = session.RunAsync(CancellationToken.None);

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(31, md5));
        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(32, md5));
        var found = await WaitForFoundAsync(transport, expectedCount: 2);
        Assert.Equal(payload, found[31]);
        Assert.Equal(payload, found[32]);
        Assert.Equal(2, handler.HeldLeaseCount);
        Assert.True(handler.HoldsLease(31));
        Assert.True(handler.HoldsLease(32));

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetReceiptAck(31));
        await ArticleWorkTestDeliveries.WaitUntilAsync(() => !handler.HoldsLease(31), TimeSpan.FromSeconds(2));
        Assert.True(handler.HoldsLease(32));
        Assert.Equal(1, handler.HeldLeaseCount);
        Assert.Equal(1, authority.RetainedCount);

        transport.CompleteInbound();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, handler.HeldLeaseCount);
        Assert.Equal(1, authority.RetainedCount);
    }

    [Fact]
    public async Task Receipt_timeout_of_one_request_does_not_block_another_request()
    {
        var first = "timeout-a"u8.ToArray();
        var second = "complete-b"u8.ToArray();
        var authority = ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024 * 1024);
        authority.Retain("<timeout-a@example.invalid>", first);
        authority.Retain("<complete-b@example.invalid>", second);
        var md5A = ArticleIdentity.FromExactMessageId("<timeout-a@example.invalid>").Md5Hex;
        var md5B = ArticleIdentity.FromExactMessageId("<complete-b@example.invalid>").Md5Hex;
        var handler = new CacheListenerRetentionHandler(authority);
        var transport = new ScriptedCacheListenerTransport();
        await using var session = CreateSession(
            transport,
            handler,
            new BackFillerListenerRuntimeOptions(
                65536,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(50),
                1024 * 1024,
                8));
        var run = session.RunAsync(CancellationToken.None);

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(11, md5A));
        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(22, md5B));
        await WaitForFoundAsync(transport, expectedCount: 2);

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetReceiptAck(22));
        await ArticleWorkTestDeliveries.WaitUntilAsync(() => !handler.HoldsLease(22), TimeSpan.FromSeconds(2));
        Assert.True(handler.HoldsLease(11));

        await ArticleWorkTestDeliveries.WaitUntilAsync(() => !handler.HoldsLease(11), TimeSpan.FromSeconds(2));
        Assert.Equal(0, handler.HeldLeaseCount);
        Assert.Equal(2, authority.RetainedCount);
        Assert.Equal(2, CountOpcode(transport, ListenerOpcode.GetResponseFound));

        transport.CompleteInbound();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Separate_sessions_do_not_share_or_release_each_others_leases()
    {
        var payload = "shared-across-connections"u8.ToArray();
        var authority = ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024 * 1024);
        authority.Retain(ArticleWorkTestDeliveries.CanonicalMessageId, payload);
        var md5 = ArticleIdentity.FromExactMessageId(ArticleWorkTestDeliveries.CanonicalMessageId).Md5Hex;

        var firstHandler = new CacheListenerRetentionHandler(authority);
        var firstTransport = new ScriptedCacheListenerTransport();
        await using var firstSession = CreateSession(firstTransport, firstHandler);
        var firstRun = firstSession.RunAsync(CancellationToken.None);

        var secondHandler = new CacheListenerRetentionHandler(authority);
        var secondTransport = new ScriptedCacheListenerTransport();
        await using var secondSession = CreateSession(secondTransport, secondHandler);
        var secondRun = secondSession.RunAsync(CancellationToken.None);

        await firstTransport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(1, md5));
        await secondTransport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(2, md5));
        await WaitForFoundAsync(firstTransport, expectedCount: 1);
        await WaitForFoundAsync(secondTransport, expectedCount: 1);
        Assert.True(firstHandler.HoldsLease(1));
        Assert.True(secondHandler.HoldsLease(2));
        Assert.False(firstHandler.HoldsLease(2));
        Assert.False(secondHandler.HoldsLease(1));

        await firstTransport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetReceiptAck(1));
        await ArticleWorkTestDeliveries.WaitUntilAsync(() => firstHandler.HeldLeaseCount == 0, TimeSpan.FromSeconds(2));
        Assert.True(secondHandler.HoldsLease(2));
        Assert.Equal(1, authority.RetainedCount);

        firstTransport.CompleteInbound();
        await firstRun.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(secondHandler.HoldsLease(2));
        Assert.Equal(1, authority.RetainedCount);

        await secondTransport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetReceiptAck(2));
        await ArticleWorkTestDeliveries.WaitUntilAsync(() => secondHandler.HeldLeaseCount == 0, TimeSpan.FromSeconds(2));
        secondTransport.CompleteInbound();
        await secondRun.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, authority.RetainedCount);
    }

    private static (ArticleRetentionAuthority Authority, CacheListenerRetentionHandler Handler, ScriptedCacheListenerTransport Transport, CacheListenerSession Session)
        CreateTwoArticleSession(
            byte[] firstPayload,
            byte[] secondPayload,
            out string md5A,
            out string md5B)
    {
        var authority = ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024 * 1024);
        authority.Retain("<corr-a@example.invalid>", firstPayload);
        authority.Retain("<corr-b@example.invalid>", secondPayload);
        md5A = ArticleIdentity.FromExactMessageId("<corr-a@example.invalid>").Md5Hex;
        md5B = ArticleIdentity.FromExactMessageId("<corr-b@example.invalid>").Md5Hex;
        var handler = new CacheListenerRetentionHandler(authority);
        var transport = new ScriptedCacheListenerTransport();
        return (authority, handler, transport, CreateSession(transport, handler));
    }

    private static CacheListenerSession CreateSession(
        ScriptedCacheListenerTransport transport,
        CacheListenerRetentionHandler handler,
        BackFillerListenerRuntimeOptions? listener = null) =>
        new(
            transport,
            handler,
            listener ?? new BackFillerListenerRuntimeOptions(
                65536,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                1024 * 1024,
                8));

    private static async Task<Dictionary<uint, byte[]>> WaitForFoundAsync(
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
                    Assert.False(found.ContainsKey(frame.RequestId), $"RequestId {frame.RequestId} received more than one Found response.");
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

    private static async Task WaitForErrorAsync(
        ScriptedCacheListenerTransport transport,
        ListenerProtocolErrorCode code)
    {
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!safety.IsCancellationRequested)
        {
            foreach (var frame in ReadFrames(transport.Written))
            {
                if (frame.Opcode == ListenerOpcode.GetResponseError
                    && ReadError(frame.Payload) == code)
                {
                    return;
                }
            }

            await Task.Delay(10, safety.Token).ConfigureAwait(false);
        }

        throw new TimeoutException($"Did not observe error {code}.");
    }

    private static int CountOpcode(ScriptedCacheListenerTransport transport, ListenerOpcode opcode) =>
        ReadFrames(transport.Written).Count(frame => frame.Opcode == opcode);

    private static List<(uint RequestId, ListenerOpcode Opcode, byte[] Payload)> ReadFrames(byte[] written)
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

    private static ListenerProtocolErrorCode ReadError(byte[] payload) =>
        (ListenerProtocolErrorCode)System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(payload);
}
