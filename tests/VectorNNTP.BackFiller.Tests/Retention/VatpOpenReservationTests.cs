using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Listener;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Transport.ArticleTransfer;

namespace VectorNNTP.BackFiller.Tests.Retention;

/// <summary>
/// Regressions for VATP OPEN RequestId consumption vs outbound/found-byte reservation ordering.
/// </summary>
public sealed class VatpOpenReservationTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Open_WhenReservationFails_RequestIdRemainsUsable()
    {
        var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
        var prepared = RetentionTestArticles.RetainPrepared(authority, "<reserve-fail-retry@example.test>");

        var reserveAttempts = 0;
        using (var rejected = authority.TryOpenTransfer(
                   prepared.RequestId,
                   prepared.Record.ArtId,
                   _ =>
                   {
                       Interlocked.Increment(ref reserveAttempts);
                       return false;
                   }))
        {
            Assert.Equal(VatpOpenKind.Rejected, rejected.Kind);
            Assert.Null(rejected.Lease);
        }

        Assert.Equal(1, reserveAttempts);

        // Temporary pressure cleared: same Success RequestId must still OPEN.
        using var opened = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId, _ => true);
        Assert.Equal(VatpOpenKind.Opened, opened.Kind);
        Assert.NotNull(opened.Lease);
    }

    [Fact]
    public async Task OpenReservationFailure_DoesNotLeakLease()
    {
        await using var harness = await SessionHarness.CreateAsync(maxQueuedFoundPayloadBytes: 1);
        var prepared = RetentionTestArticles.RetainPrepared(
            harness.Authority,
            "<reserve-fail-lease@example.test>",
            body: BuildBody(512));

        var runTask = harness.Session.RunAsync(CancellationToken.None);
        harness.Transport.EnqueueInbound(EncodeHello());
        await WaitAsync(() => harness.Transport.WriteCalls >= 2);

        harness.Transport.EnqueueInbound(EncodeOpen(1, prepared.RequestId, prepared.Record.ArtId));
        await WaitAsync(() =>
            harness.Session.ActiveStreamCount == 0
            && harness.Session.ReservedFoundPayloadBytes == 0
            && RequestIdStillPending(harness.Authority, prepared.RequestId, prepared.Record.ArtId)
            && CountFailFrames(harness.Transport) >= 1);

        Assert.Equal(0, harness.Session.ActiveStreamCount);

        // Lease was not held: a direct OPEN (no session reservation) succeeds with the same RequestId.
        using var open = harness.Authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
        Assert.Equal(VatpOpenKind.Opened, open.Kind);

        harness.Transport.CompleteInbound();
        await AssertCompletesAsync(runTask);
    }

    [Fact]
    public async Task OpenReservationFailure_DoesNotLeakReservedBytes()
    {
        await using var harness = await SessionHarness.CreateAsync(maxQueuedFoundPayloadBytes: 1);
        var prepared = RetentionTestArticles.RetainPrepared(
            harness.Authority,
            "<reserve-fail-bytes@example.test>",
            body: BuildBody(512));

        var runTask = harness.Session.RunAsync(CancellationToken.None);
        harness.Transport.EnqueueInbound(EncodeHello());
        await WaitAsync(() => harness.Transport.WriteCalls >= 2);

        harness.Transport.EnqueueInbound(EncodeOpen(1, prepared.RequestId, prepared.Record.ArtId));
        await WaitAsync(() =>
            harness.Session.ReservedFoundPayloadBytes == 0
            && RequestIdStillPending(harness.Authority, prepared.RequestId, prepared.Record.ArtId)
            && CountFailFrames(harness.Transport) >= 1);

        Assert.Equal(0, harness.Session.ReservedFoundPayloadBytes);
        Assert.Equal(0, harness.Session.ActiveStreamCount);

        harness.Transport.CompleteInbound();
        await AssertCompletesAsync(runTask);
        Assert.Equal(0, harness.Session.ReservedFoundPayloadBytes);
    }

    [Fact]
    public async Task Open_WhenReservationFails_ThenCapacityFreed_RetrySucceeds()
    {
        var small = RetentionTestArticles.Create("<small-hold@example.test>", "s\r\n");
        var large = RetentionTestArticles.Create("<large-retry@example.test>", BuildBody(2048));
        Assert.True(large.Record.ArtSize > small.Record.ArtSize);

        await using var harness = await SessionHarness.CreateAsync(
            maxQueuedFoundPayloadBytes: large.Record.ArtSize);
        Assert.Equal(
            ArticleRetentionKind.Retained,
            harness.Authority.RetainCanonical(
                small.MessageId,
                small.RequestId,
                small.Record,
                small.SelectedDateHeaderName).Kind);
        Assert.Equal(
            ArticleRetentionKind.Retained,
            harness.Authority.RetainCanonical(
                large.MessageId,
                large.RequestId,
                large.Record,
                large.SelectedDateHeaderName).Kind);

        // Hold writer after HELLO so the small transfer keeps its reservation.
        harness.Transport.AllowSuccessfulWritesThenBlock(2);
        var runTask = harness.Session.RunAsync(CancellationToken.None);
        harness.Transport.EnqueueInbound(EncodeHello());
        await WaitAsync(() => harness.Transport.WriteCalls >= 2);

        harness.Transport.EnqueueInbound(EncodeOpen(1, small.RequestId, small.Record.ArtId));
        await WaitAsync(() => harness.Session.ActiveStreamCount == 1);
        Assert.Equal(small.Record.ArtSize, harness.Session.ReservedFoundPayloadBytes);

        // Large OPEN fails on reservation; RequestId must survive (probe via reserve callback).
        harness.Transport.EnqueueInbound(EncodeOpen(2, large.RequestId, large.Record.ArtId));
        await WaitAsync(() =>
            harness.Session.ActiveStreamCount == 1
            && harness.Session.ReservedFoundPayloadBytes == small.Record.ArtSize
            && RequestIdStillPending(harness.Authority, large.RequestId, large.Record.ArtId));

        // Free capacity by cancelling the holding stream.
        harness.Transport.EnqueueInbound(EncodeCancel(1));
        await WaitAsync(() => harness.Session.ActiveStreamCount == 0);
        Assert.Equal(0, harness.Session.ReservedFoundPayloadBytes);

        harness.Transport.EnqueueInbound(EncodeOpen(3, large.RequestId, large.Record.ArtId));
        await WaitAsync(() => harness.Session.ActiveStreamCount == 1);
        Assert.Equal(large.Record.ArtSize, harness.Session.ReservedFoundPayloadBytes);

        // Writer is intentionally blocked holding the META write; fail it so RunAsync can finish.
        harness.Transport.FailWrites();
        await AssertCompletesAsync(runTask);
    }

    [Fact]
    public async Task ConcurrentOpen_SameRequestId_OnlyOneSucceeds()
    {
        var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
        var prepared = RetentionTestArticles.RetainPrepared(authority, "<concurrent-open@example.test>");

        var opened = 0;
        var rejected = 0;
        var barrier = new Barrier(2);
        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            var result = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId, _ => true);
            if (result.Kind == VatpOpenKind.Opened)
            {
                Interlocked.Increment(ref opened);
            }
            else
            {
                Interlocked.Increment(ref rejected);
            }

            result.Dispose();
        })).ToArray();

        await Task.WhenAll(tasks).WaitAsync(TestTimeout);
        Assert.Equal(1, opened);
        Assert.Equal(1, rejected);
    }

    [Fact]
    public void WrongArticleId_DoesNotConsumeRequestId()
    {
        var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
        var prepared = RetentionTestArticles.RetainPrepared(authority, "<wrong-artid-reserve@example.test>");
        var wrongId = ArticleId.FromMessageId("<other@example.test>"u8);
        var reserveCalled = 0;

        using (var rejected = authority.TryOpenTransfer(
                   prepared.RequestId,
                   wrongId,
                   _ =>
                   {
                       Interlocked.Increment(ref reserveCalled);
                       return true;
                   }))
        {
            Assert.Equal(VatpOpenKind.Rejected, rejected.Kind);
        }

        Assert.Equal(0, reserveCalled);

        using var opened = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
        Assert.Equal(VatpOpenKind.Opened, opened.Kind);
    }

    [Fact]
    public void SuccessfulOpen_ConsumesRequestIdExactlyOnce()
    {
        var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
        var prepared = RetentionTestArticles.RetainPrepared(authority, "<consume-once@example.test>");

        using (var first = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId, _ => true))
        {
            Assert.Equal(VatpOpenKind.Opened, first.Kind);
        }

        using var second = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId, _ => true);
        Assert.Equal(VatpOpenKind.Rejected, second.Kind);
    }

    [Fact]
    public void ExpiredRequestId_RemainsRejected()
    {
        var time = new ManualTimeProvider(Start);
        var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(5));
        var prepared = RetentionTestArticles.RetainPrepared(authority, "<expired-open@example.test>");
        time.Advance(TimeSpan.FromSeconds(5));

        using var expired = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId, _ => true);
        Assert.Equal(VatpOpenKind.Rejected, expired.Kind);

        using var stillGone = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
        Assert.Equal(VatpOpenKind.Rejected, stillGone.Kind);
    }

    private static bool RequestIdStillPending(
        ArticleRetentionAuthority authority,
        Guid requestId,
        ArticleId articleId)
    {
        var reserveSeen = false;
        using var probe = authority.TryOpenTransfer(
            requestId,
            articleId,
            _ =>
            {
                reserveSeen = true;
                return false;
            });
        return reserveSeen && probe.Kind == VatpOpenKind.Rejected;
    }

    private static int CountFailFrames(ControllableCacheListenerTransport transport)
    {
        var count = 0;
        foreach (var chunk in transport.WrittenFrames)
        {
            // VATP header: flags at [0], type at [1].
            if (chunk.Length >= VatpProtocol.HeaderLengthBytes
                && (VatpFrameType)chunk[1] == VatpFrameType.Fail)
            {
                count++;
            }
        }

        return count;
    }

    private static string BuildBody(int minimumBytes)
    {
        const string line = "abcdefghijklmnopqrstuvwxyz0123456789\r\n";
        var builder = new System.Text.StringBuilder(minimumBytes + line.Length);
        while (builder.Length < minimumBytes)
        {
            builder.Append(line);
        }

        return builder.ToString();
    }

    private static byte[] EncodeHello() =>
        VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeHello(VatpProtocol.DefaultMaxFramePayload));

    private static byte[] EncodeOpen(uint streamId, Guid requestId, ArticleId articleId)
    {
        Span<byte> idBytes = stackalloc byte[VatpProtocol.ArticleIdLength];
        articleId.CopyTo(idBytes);
        return VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeOpen(streamId, requestId, idBytes));
    }

    private static byte[] EncodeCancel(uint streamId) =>
        VatpFrameEncoder.ToSingleBuffer(VatpFrameEncoder.EncodeCancel(streamId));

    private static async Task AssertCompletesAsync(Task task)
    {
        var finished = await Task.WhenAny(task, Task.Delay(TestTimeout));
        Assert.Same(task, finished);
        try
        {
            await task;
        }
        catch (IOException)
        {
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = TimeProvider.System.GetUtcNow() + TestTimeout;
        while (!condition())
        {
            if (TimeProvider.System.GetUtcNow() > deadline)
            {
                Assert.Fail("condition not met before timeout");
            }

            await Task.Delay(10);
        }
    }

    private sealed class SessionHarness : IAsyncDisposable
    {
        private SessionHarness(
            ControllableCacheListenerTransport transport,
            VatpListenerSession session,
            ArticleRetentionAuthority authority)
        {
            Transport = transport;
            Session = session;
            Authority = authority;
        }

        public ControllableCacheListenerTransport Transport { get; }

        public VatpListenerSession Session { get; }

        public ArticleRetentionAuthority Authority { get; }

        public static Task<SessionHarness> CreateAsync(int maxQueuedFoundPayloadBytes = 8 * 1024 * 1024)
        {
            var authority = ArticleRetentionAuthorityTests.Create(TimeProvider.System, 16 * 1024 * 1024);
            var listener = new BackFillerListenerRuntimeOptions(
                ParserAccumulationMaxBytes: 256 * 1024,
                TlsHandshakeTimeout: TimeSpan.FromSeconds(5),
                IoProgressTimeout: TimeSpan.FromSeconds(5),
                AwaitingReceiptAckTimeout: TimeSpan.FromSeconds(5),
                MaxQueuedFoundPayloadBytes: maxQueuedFoundPayloadBytes,
                MaxActiveConnections: 8);
            var transport = new ControllableCacheListenerTransport();
            var session = new VatpListenerSession(
                transport,
                authority,
                listener,
                NullLogger.Instance);
            return Task.FromResult(new SessionHarness(transport, session, authority));
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await Authority.DisposeAsync();
        }
    }
}
