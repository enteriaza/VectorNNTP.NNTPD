using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;

namespace VectorNNTP.BackFiller.Tests.Retention;

/// <summary>
/// Hard Success capability: FIFO reclaim must not invalidate openable RequestIds before TTL.
/// </summary>
public sealed class VatpSuccessPinningTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void PublishedSuccess_IsPinnedAgainstFifoEviction()
    {
        var pinned = RetentionTestArticles.Create("<pin0@ex>", "same-body\r\n");
        var pressure = RetentionTestArticles.Create("<prs0@ex>", "same-body\r\n");
        Assert.Equal(pinned.Record.ArtSize, pressure.Record.ArtSize);
        var authority = ArticleRetentionAuthorityTests.Create(
            new ManualTimeProvider(Start),
            maxBytes: pinned.Record.ArtSize);
        Assert.Equal(
            ArticleRetentionKind.Retained,
            authority.RetainCanonical(
                pinned.MessageId, pinned.RequestId, pinned.Record, pinned.SelectedDateHeaderName).Kind);

        var rejected = authority.RetainCanonical(
            pressure.MessageId, pressure.RequestId, pressure.Record, pressure.SelectedDateHeaderName);
        Assert.Equal(ArticleRetentionKind.CapacityUnavailable, rejected.Kind);
        Assert.Equal(pinned.Record.ArtSize, authority.RetainedPayloadBytes);

        using var open = authority.TryOpenTransfer(pinned.RequestId, pinned.Record.ArtId);
        Assert.Equal(VatpOpenKind.Opened, open.Kind);
    }

    [Fact]
    public void UnpinnedEntry_RemainsFifoEvictable()
    {
        var unpinned = RetentionTestArticles.Create("<unp0@ex>", "same-body\r\n");
        var next = RetentionTestArticles.Create("<nxt0@ex>", "same-body\r\n");
        Assert.Equal(unpinned.Record.ArtSize, next.Record.ArtSize);
        var authority = ArticleRetentionAuthorityTests.Create(
            new ManualTimeProvider(Start),
            maxBytes: unpinned.Record.ArtSize);
        Assert.Equal(
            ArticleRetentionKind.Retained,
            authority.RetainCanonical(
                unpinned.MessageId, unpinned.RequestId, unpinned.Record, unpinned.SelectedDateHeaderName).Kind);
        Assert.True(authority.TryCancelPendingRequest(unpinned.RequestId));

        var admitted = authority.RetainCanonical(
            next.MessageId, next.RequestId, next.Record, next.SelectedDateHeaderName);
        Assert.Equal(ArticleRetentionKind.Retained, admitted.Kind);
        Assert.Equal(next.Record.ArtSize, authority.RetainedPayloadBytes);

        using var missing = authority.TryOpenTransfer(unpinned.RequestId, unpinned.Record.ArtId);
        Assert.Equal(VatpOpenKind.Rejected, missing.Kind);
        using var openNext = authority.TryOpenTransfer(next.RequestId, next.Record.ArtId);
        Assert.Equal(VatpOpenKind.Opened, openNext.Kind);
    }

    [Fact]
    public void PinnedOldest_UnpinnedNewer_IsSkipped()
    {
        var pinned = RetentionTestArticles.Create("<old0@ex>", "body-aa\r\n");
        var unpinned = RetentionTestArticles.Create("<new0@ex>", "body-bb\r\n");
        var incoming = RetentionTestArticles.Create("<inc0@ex>", "body-bb\r\n");
        Assert.Equal(unpinned.Record.ArtSize, incoming.Record.ArtSize);
        var maxBytes = pinned.Record.ArtSize + unpinned.Record.ArtSize;
        var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: maxBytes);

        Assert.Equal(
            ArticleRetentionKind.Retained,
            authority.RetainCanonical(
                pinned.MessageId, pinned.RequestId, pinned.Record, pinned.SelectedDateHeaderName).Kind);
        Assert.Equal(
            ArticleRetentionKind.Retained,
            authority.RetainCanonical(
                unpinned.MessageId, unpinned.RequestId, unpinned.Record, unpinned.SelectedDateHeaderName).Kind);
        Assert.True(authority.TryCancelPendingRequest(unpinned.RequestId));

        var admitted = authority.RetainCanonical(
            incoming.MessageId, incoming.RequestId, incoming.Record, incoming.SelectedDateHeaderName);
        Assert.Equal(ArticleRetentionKind.Retained, admitted.Kind);

        using (var openPinned = authority.TryOpenTransfer(pinned.RequestId, pinned.Record.ArtId))
        {
            Assert.Equal(VatpOpenKind.Opened, openPinned.Kind);
        }

        using (var missingUnpinned = authority.TryOpenTransfer(unpinned.RequestId, unpinned.Record.ArtId))
        {
            Assert.Equal(VatpOpenKind.Rejected, missingUnpinned.Kind);
        }

        Assert.Equal(pinned.Record.ArtSize + incoming.Record.ArtSize, authority.RetainedPayloadBytes);
    }

    [Fact]
    public void AllCandidatesPinned_ReturnsCapacityUnavailable()
    {
        var one = RetentionTestArticles.Create("<one0@ex>", "body-aa\r\n");
        var two = RetentionTestArticles.Create("<two0@ex>", "body-bb\r\n");
        var three = RetentionTestArticles.Create("<thr0@ex>", "body-cc\r\n");
        Assert.Equal(one.Record.ArtSize, two.Record.ArtSize);
        Assert.Equal(two.Record.ArtSize, three.Record.ArtSize);
        var maxBytes = one.Record.ArtSize + two.Record.ArtSize;
        var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: maxBytes);
        Assert.Equal(
            ArticleRetentionKind.Retained,
            authority.RetainCanonical(one.MessageId, one.RequestId, one.Record, one.SelectedDateHeaderName).Kind);
        Assert.Equal(
            ArticleRetentionKind.Retained,
            authority.RetainCanonical(two.MessageId, two.RequestId, two.Record, two.SelectedDateHeaderName).Kind);

        var rejected = authority.RetainCanonical(
            three.MessageId, three.RequestId, three.Record, three.SelectedDateHeaderName);
        Assert.Equal(ArticleRetentionKind.CapacityUnavailable, rejected.Kind);
        Assert.Equal(one.Record.ArtSize + two.Record.ArtSize, authority.RetainedPayloadBytes);

        using var openOne = authority.TryOpenTransfer(one.RequestId, one.Record.ArtId);
        using var openTwo = authority.TryOpenTransfer(two.RequestId, two.Record.ArtId);
        Assert.Equal(VatpOpenKind.Opened, openOne.Kind);
        Assert.Equal(VatpOpenKind.Opened, openTwo.Kind);
    }

    [Fact]
    public void MultipleRequestIdsKeepEntryPinned_UntilLastConsumed()
    {
        var seed = RetentionTestArticles.Create("<tmul@ex>", "body-aa\r\n");
        var tight = ArticleRetentionAuthorityTests.Create(
            new ManualTimeProvider(Start),
            maxBytes: seed.Record.ArtSize);
        Assert.Equal(
            ArticleRetentionKind.Retained,
            tight.RetainCanonical(seed.MessageId, seed.RequestId, seed.Record, seed.SelectedDateHeaderName).Kind);
        var second = RetentionTestArticles.Create("<tmul@ex>", "body-bb\r\n");
        Assert.Equal(
            ArticleRetentionKind.AlreadyPresent,
            tight.RetainCanonical(second.MessageId, second.RequestId, second.Record, second.SelectedDateHeaderName).Kind);

        using (var openFirst = tight.TryOpenTransfer(seed.RequestId, seed.Record.ArtId))
        {
            Assert.Equal(VatpOpenKind.Opened, openFirst.Kind);
        }

        var blocked = RetentionTestArticles.Create("<tblk@ex>", "body-aa\r\n");
        Assert.Equal(seed.Record.ArtSize, blocked.Record.ArtSize);
        Assert.Equal(
            ArticleRetentionKind.CapacityUnavailable,
            tight.RetainCanonical(blocked.MessageId, blocked.RequestId, blocked.Record, blocked.SelectedDateHeaderName).Kind);

        using (var openSecond = tight.TryOpenTransfer(second.RequestId, seed.Record.ArtId))
        {
            Assert.Equal(VatpOpenKind.Opened, openSecond.Kind);
        }

        var replacement = RetentionTestArticles.Create("<trep@ex>", "body-aa\r\n");
        Assert.Equal(seed.Record.ArtSize, replacement.Record.ArtSize);
        Assert.Equal(
            ArticleRetentionKind.Retained,
            tight.RetainCanonical(
                replacement.MessageId, replacement.RequestId, replacement.Record, replacement.SelectedDateHeaderName).Kind);
        Assert.Equal(replacement.Record.ArtSize, tight.RetainedPayloadBytes);
    }

    [Fact]
    public void CancelLastRequestId_MakesEntryEvictable()
    {
        var pinned = RetentionTestArticles.Create("<can0@ex>", "same-body\r\n");
        var replacement = RetentionTestArticles.Create("<can1@ex>", "same-body\r\n");
        Assert.Equal(pinned.Record.ArtSize, replacement.Record.ArtSize);
        var authority = ArticleRetentionAuthorityTests.Create(
            new ManualTimeProvider(Start),
            maxBytes: pinned.Record.ArtSize);
        Assert.Equal(
            ArticleRetentionKind.Retained,
            authority.RetainCanonical(
                pinned.MessageId, pinned.RequestId, pinned.Record, pinned.SelectedDateHeaderName).Kind);
        Assert.True(authority.TryCancelPendingRequest(pinned.RequestId));

        Assert.Equal(
            ArticleRetentionKind.Retained,
            authority.RetainCanonical(
                replacement.MessageId, replacement.RequestId, replacement.Record, replacement.SelectedDateHeaderName).Kind);
        using var missing = authority.TryOpenTransfer(pinned.RequestId, pinned.Record.ArtId);
        Assert.Equal(VatpOpenKind.Rejected, missing.Kind);
    }

    [Fact]
    public void TtlExpiry_RemovesPinnedEntry()
    {
        var time = new ManualTimeProvider(Start);
        var authority = ArticleRetentionAuthorityTests.Create(
            time,
            maxBytes: 1024 * 1024,
            ttl: TimeSpan.FromSeconds(5));
        var prepared = RetentionTestArticles.RetainPrepared(authority, "<ttl-pin@b>");
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(authority.SweepExpired() > 0);
        using var open = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
        Assert.Equal(VatpOpenKind.Rejected, open.Kind);
        Assert.Equal(0, authority.RetainedPayloadBytes);
    }

    [Fact]
    public async Task ConcurrentOpenAndReclaim_DoesNotInvalidateOpenableCapability()
    {
        const string body = "same-body\r\n";
        var prepared = RetentionTestArticles.Create("<copen@ex>", body);
        var pressure = RetentionTestArticles.Create("<cpres@ex>", body);
        Assert.Equal(prepared.Record.ArtSize, pressure.Record.ArtSize);
        var authority = ArticleRetentionAuthorityTests.Create(
            new ManualTimeProvider(Start),
            maxBytes: prepared.Record.ArtSize);
        Assert.Equal(
            ArticleRetentionKind.Retained,
            authority.RetainCanonical(
                prepared.MessageId, prepared.RequestId, prepared.Record, prepared.SelectedDateHeaderName).Kind);

        var barrier = new Barrier(2);
        var openTask = Task.Run(() =>
        {
            barrier.SignalAndWait(TestTimeout);
            return authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
        });
        var reclaimTask = Task.Run(() =>
        {
            barrier.SignalAndWait(TestTimeout);
            return authority.RetainCanonical(
                pressure.MessageId, pressure.RequestId, pressure.Record, pressure.SelectedDateHeaderName);
        });

        await Task.WhenAll(openTask, reclaimTask).WaitAsync(TestTimeout);
        using var open = await openTask;
        var reclaim = await reclaimTask;

        // Under the gate, either OPEN wins first (RequestId consumed; reclaim may admit after unpin)
        // or reclaim runs first and must CapacityUnavailable while RequestId remains openable.
        if (open.Kind == VatpOpenKind.Opened)
        {
            Assert.True(open.Lease!.Record.ArtData.Span.SequenceEqual(prepared.ArtData));
            Assert.True(
                reclaim.Kind is ArticleRetentionKind.Retained or ArticleRetentionKind.CapacityUnavailable);
        }
        else
        {
            Assert.Equal(ArticleRetentionKind.CapacityUnavailable, reclaim.Kind);
            using var retry = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, retry.Kind);
        }
    }

    [Fact]
    public async Task ConcurrentAlreadyPresentAndReclaim_AttachPinsAtomically()
    {
        const string body = "same-body\r\n";
        var first = RetentionTestArticles.Create("<att0@ex>", body);
        var authority = ArticleRetentionAuthorityTests.Create(
            new ManualTimeProvider(Start),
            maxBytes: first.Record.ArtSize);
        Assert.Equal(
            ArticleRetentionKind.Retained,
            authority.RetainCanonical(first.MessageId, first.RequestId, first.Record, first.SelectedDateHeaderName).Kind);
        Assert.True(authority.TryCancelPendingRequest(first.RequestId));

        var attach = RetentionTestArticles.Create("<att0@ex>", body);
        var pressure = RetentionTestArticles.Create("<prs0@ex>", body);
        Assert.Equal(first.Record.ArtSize, pressure.Record.ArtSize);
        var barrier = new Barrier(2);
        var attachTask = Task.Run(() =>
        {
            barrier.SignalAndWait(TestTimeout);
            return authority.RetainCanonical(
                attach.MessageId, attach.RequestId, attach.Record, attach.SelectedDateHeaderName);
        });
        var reclaimTask = Task.Run(() =>
        {
            barrier.SignalAndWait(TestTimeout);
            return authority.RetainCanonical(
                pressure.MessageId, pressure.RequestId, pressure.Record, pressure.SelectedDateHeaderName);
        });

        await Task.WhenAll(attachTask, reclaimTask).WaitAsync(TestTimeout);
        var attachResult = await attachTask;
        var reclaimResult = await reclaimTask;

        if (attachResult.Kind == ArticleRetentionKind.AlreadyPresent)
        {
            using var open = authority.TryOpenTransfer(attach.RequestId, first.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, open.Kind);
            Assert.Equal(ArticleRetentionKind.CapacityUnavailable, reclaimResult.Kind);
        }
        else if (attachResult.Kind == ArticleRetentionKind.Retained)
        {
            using var open = authority.TryOpenTransfer(attach.RequestId, attach.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, open.Kind);
        }
        else
        {
            Assert.Equal(ArticleRetentionKind.Retained, reclaimResult.Kind);
            using var open = authority.TryOpenTransfer(pressure.RequestId, pressure.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, open.Kind);
        }
    }
}
