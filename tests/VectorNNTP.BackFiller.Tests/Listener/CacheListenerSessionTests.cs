using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Listener;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.Retention;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.Listener;

public sealed class CacheListenerSessionTests
{
    [Fact]
    public async Task Valid_md5_returns_exact_retained_bytes()
    {
        var payload = new byte[] { 0x00, 0x0A, 0x0D, 0xFF, (byte)'x' };
        var (authority, md5) = Retain(payload);
        var transport = new ScriptedCacheListenerTransport();
        await using var session = CreateSession(transport, authority);
        var run = session.RunAsync(CancellationToken.None);

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(11, md5));
        var found = await WaitForOpcodeAsync(transport, ListenerOpcode.GetResponseFound);
        Assert.True(found.Payload.SequenceEqual(payload));
        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetReceiptAck(11));
        transport.CompleteInbound();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, session.OutstandingRequestCount);
        Assert.Equal(1, authority.RetainedCount);
    }

    [Fact]
    public async Task Missing_and_expired_identities_return_not_found()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
        var authority = ArticleRetentionAuthorityTests.Create(time, 1024, ttl: TimeSpan.FromSeconds(1));
        var expiredId = "<expire@example.invalid>";
        authority.Retain(expiredId, "expired-body"u8.ToArray());
        time.Advance(TimeSpan.FromSeconds(2));
        var transport = new ScriptedCacheListenerTransport();
        await using var session = CreateSession(transport, authority);
        var run = session.RunAsync(CancellationToken.None);

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(1, ArticleIdentity.FromExactMessageId(expiredId).Md5Hex));
        await WaitForOpcodeAsync(transport, ListenerOpcode.GetResponseNotFound);
        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(2, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        await WaitForOpcodeAsync(transport, ListenerOpcode.GetResponseNotFound);
        transport.CompleteInbound();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Duplicate_request_id_returns_error_without_second_lease()
    {
        var (authority, md5) = Retain("abc"u8.ToArray());
        var transport = new ScriptedCacheListenerTransport();
        await using var session = CreateSession(transport, authority);
        var run = session.RunAsync(CancellationToken.None);

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(5, md5));
        await WaitForOpcodeAsync(transport, ListenerOpcode.GetResponseFound);
        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(5, md5));
        var error = await WaitForOpcodeAsync(transport, ListenerOpcode.GetResponseError);
        Assert.Equal(ListenerProtocolErrorCode.DuplicateRequestId, ReadError(error.Payload));
        transport.CompleteInbound();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Receipt_ack_timeout_releases_the_lease_without_removing_the_article()
    {
        var (authority, md5) = Retain("keep"u8.ToArray());
        var transport = new ScriptedCacheListenerTransport();
        await using var session = CreateSession(
            transport,
            authority,
            new BackFillerListenerRuntimeOptions(65536, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50), 1024 * 1024, 8));
        var run = session.RunAsync(CancellationToken.None);

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(9, md5));
        await WaitForOpcodeAsync(transport, ListenerOpcode.GetResponseFound);
        await ArticleWorkTestDeliveries.WaitUntilAsync(() => session.OutstandingRequestCount == 0, TimeSpan.FromSeconds(2));
        Assert.Equal(1, authority.RetainedCount);
        transport.CompleteInbound();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Queued_found_bytes_are_bounded()
    {
        var payload = new byte[64];
        var (authority, md5) = Retain(payload);
        var transport = new ScriptedCacheListenerTransport();
        await using var session = CreateSession(
            transport,
            authority,
            new BackFillerListenerRuntimeOptions(65536, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), 16, 8));
        var run = session.RunAsync(CancellationToken.None);

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(3, md5));
        var error = await WaitForOpcodeAsync(transport, ListenerOpcode.GetResponseError);
        Assert.Equal(ListenerProtocolErrorCode.InternalError, ReadError(error.Payload));
        transport.CompleteInbound();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Shutdown_while_waiting_for_receipt_ack_releases_the_lease()
    {
        var (authority, md5) = Retain("body"u8.ToArray());
        var transport = new ScriptedCacheListenerTransport();
        await using var session = CreateSession(transport, authority);
        using var cts = new CancellationTokenSource();
        var run = session.RunAsync(cts.Token);

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(4, md5));
        await WaitForOpcodeAsync(transport, ListenerOpcode.GetResponseFound);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, session.OutstandingRequestCount);
        Assert.Equal(1, authority.RetainedCount);
    }

    [Fact]
    public async Task Malformed_identity_and_unknown_opcode_return_protocol_errors()
    {
        var transport = new ScriptedCacheListenerTransport();
        await using var session = CreateSession(transport, ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024));
        var run = session.RunAsync(CancellationToken.None);

        var uppercase = ListenerProtocolEncoder.EncodeGetRequest(1, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        uppercase[16] = (byte)'A';
        await transport.EnqueueAsync(uppercase);
        await WaitForErrorAsync(transport, ListenerProtocolErrorCode.InvalidMessageIdMd5);

        var unknown = new byte[ListenerProtocol.HeaderLengthBytes];
        new ListenerFrameHeader(ListenerProtocol.Version1, (ListenerOpcode)0x99, 16, 8, 0, 0).WriteTo(unknown);
        await transport.EnqueueAsync(unknown);
        await WaitForErrorAsync(transport, ListenerProtocolErrorCode.UnsupportedOpcode);

        transport.CompleteInbound();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Truncated_request_completes_when_remaining_bytes_arrive()
    {
        var payload = "split"u8.ToArray();
        var (authority, md5) = Retain(payload);
        var transport = new ScriptedCacheListenerTransport();
        await using var session = CreateSession(transport, authority);
        var run = session.RunAsync(CancellationToken.None);

        var frame = ListenerProtocolEncoder.EncodeGetRequest(6, md5);
        await transport.EnqueueAsync(frame.AsMemory(0, 8));
        await transport.EnqueueAsync(frame.AsMemory(8));
        var found = await WaitForOpcodeAsync(transport, ListenerOpcode.GetResponseFound);
        Assert.Equal(payload, found.Payload);
        transport.CompleteInbound();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Oversized_declared_payload_forces_session_shutdown()
    {
        var transport = new ScriptedCacheListenerTransport();
        await using var session = CreateSession(
            transport,
            ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024),
            new BackFillerListenerRuntimeOptions(64, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), 1024, 8));
        var run = session.RunAsync(CancellationToken.None);

        var header = new byte[ListenerProtocol.HeaderLengthBytes];
        new ListenerFrameHeader(ListenerProtocol.Version1, ListenerOpcode.GetRequest, 16, 1, 10_000, 0).WriteTo(header);
        await transport.EnqueueAsync(header);
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CacheListenerSessionState.Completed, session.State);
    }

    [Fact]
    public async Task Premature_disconnect_releases_the_lease_without_removing_the_article()
    {
        var (authority, md5) = Retain("keep-me"u8.ToArray());
        var transport = new ScriptedCacheListenerTransport();
        await using var session = CreateSession(transport, authority);
        var run = session.RunAsync(CancellationToken.None);

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(7, md5));
        await WaitForOpcodeAsync(transport, ListenerOpcode.GetResponseFound);
        transport.CompleteInbound();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, session.OutstandingRequestCount);
        Assert.Equal(1, authority.RetainedCount);
    }

    [Fact]
    public async Task Shutdown_while_parsing_is_safe()
    {
        var transport = new ScriptedCacheListenerTransport
        {
            ReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            BlockRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var session = CreateSession(transport, ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024));
        using var cts = new CancellationTokenSource();
        var run = session.RunAsync(cts.Token);
        await transport.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();
        transport.BlockRead.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Concurrent_requests_use_independent_leases()
    {
        var first = "one"u8.ToArray();
        var second = "two-two"u8.ToArray();
        var authority = ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024);
        authority.Retain("<one@example.invalid>", first);
        authority.Retain("<two@example.invalid>", second);
        var transport = new ScriptedCacheListenerTransport();
        await using var session = CreateSession(transport, authority);
        var run = session.RunAsync(CancellationToken.None);

        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(1, ArticleIdentity.FromExactMessageId("<one@example.invalid>").Md5Hex));
        await transport.EnqueueAsync(ListenerProtocolEncoder.EncodeGetRequest(2, ArticleIdentity.FromExactMessageId("<two@example.invalid>").Md5Hex));
        var frames = await WaitForFoundCountAsync(transport, 2);
        Assert.Contains(frames, frame => frame.Payload.SequenceEqual(first));
        Assert.Contains(frames, frame => frame.Payload.SequenceEqual(second));
        transport.CompleteInbound();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static CacheListenerSession CreateSession(
        ScriptedCacheListenerTransport transport,
        IArticleRetentionAuthority authority,
        BackFillerListenerRuntimeOptions? listener = null) =>
        new(
            transport,
            new CacheListenerRetentionHandler(authority),
            listener ?? new BackFillerListenerRuntimeOptions(
                65536,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                1024 * 1024,
                8));

    private static (ArticleRetentionAuthority Authority, string Md5) Retain(byte[] payload)
    {
        var authority = ArticleRetentionAuthorityTests.Create(TimeProvider.System, 1024 * 1024);
        authority.Retain(ArticleWorkTestDeliveries.CanonicalMessageId, payload);
        return (authority, ArticleIdentity.FromExactMessageId(ArticleWorkTestDeliveries.CanonicalMessageId).Md5Hex);
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
                if (frame.Opcode == ListenerOpcode.GetResponseError && ReadError(frame.Payload) == code)
                {
                    return;
                }
            }

            await Task.Delay(10, safety.Token).ConfigureAwait(false);
        }

        throw new TimeoutException($"Did not observe error {code}.");
    }

    private static async Task<(ListenerOpcode Opcode, byte[] Payload)> WaitForOpcodeAsync(
        ScriptedCacheListenerTransport transport,
        ListenerOpcode opcode)
    {
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!safety.IsCancellationRequested)
        {
            foreach (var frame in ReadFrames(transport.Written))
            {
                if (frame.Opcode == opcode)
                {
                    return frame;
                }
            }

            await Task.Delay(10, safety.Token).ConfigureAwait(false);
        }

        throw new TimeoutException($"Did not observe {opcode}.");
    }

    private static async Task<List<(ListenerOpcode Opcode, byte[] Payload)>> WaitForFoundCountAsync(
        ScriptedCacheListenerTransport transport,
        int count)
    {
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!safety.IsCancellationRequested)
        {
            var found = ReadFrames(transport.Written).Where(static frame => frame.Opcode == ListenerOpcode.GetResponseFound).ToList();
            if (found.Count >= count)
            {
                return found;
            }

            await Task.Delay(10, safety.Token).ConfigureAwait(false);
        }

        throw new TimeoutException("Did not observe enough Found frames.");
    }

    private static List<(ListenerOpcode Opcode, byte[] Payload)> ReadFrames(byte[] written)
    {
        var frames = new List<(ListenerOpcode, byte[])>();
        var offset = 0;
        while (offset + ListenerProtocol.HeaderLengthBytes <= written.Length)
        {
            var header = ListenerFrameHeader.ReadFrom(written.AsSpan(offset, ListenerProtocol.HeaderLengthBytes));
            var total = ListenerProtocol.HeaderLengthBytes + (int)header.PayloadLength;
            if (offset + total > written.Length)
            {
                break;
            }

            frames.Add((header.Opcode, written[(offset + ListenerProtocol.HeaderLengthBytes)..(offset + total)]));
            offset += total;
        }

        return frames;
    }

    private static ListenerProtocolErrorCode ReadError(byte[] payload) =>
        (ListenerProtocolErrorCode)System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(payload);
}
