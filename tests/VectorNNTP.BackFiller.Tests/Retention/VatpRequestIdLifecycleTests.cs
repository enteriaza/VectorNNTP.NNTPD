using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.BackFiller.Tests.Retention
{
    /// <summary>
    /// Regressions for bounded multi-RequestId OPEN capabilities on one retained ArticleRecord.
    /// </summary>
    public sealed class VatpRequestIdLifecycleTests
    {
        private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

        [Fact]
        public void SuccessA_RemainsOpenable_AfterAlreadyPresentB()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var a = RetentionTestArticles.RetainPrepared(authority, "<lifecycle-a@example.test>", "body-a\r\n");
            var b = RetentionTestArticles.Create("<lifecycle-a@example.test>", "body-b\r\n");

            Assert.Equal(
                ArticleRetentionKind.AlreadyPresent,
                authority.RetainCanonical(b.MessageId, b.RequestId, b.Record, b.SelectedDateHeaderName).Kind);

            using var openA = authority.TryOpenTransfer(a.RequestId, a.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, openA.Kind);
            Assert.True(openA.Lease!.Record.ArtData.Span.SequenceEqual(a.ArtData));
        }

        [Fact]
        public void AlreadyPresentB_AlsoOpenable_WhileAPending()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var a = RetentionTestArticles.RetainPrepared(authority, "<lifecycle-both@example.test>", "body-a\r\n");
            var b = RetentionTestArticles.Create("<lifecycle-both@example.test>", "body-b\r\n");
            Assert.Equal(
                ArticleRetentionKind.AlreadyPresent,
                authority.RetainCanonical(b.MessageId, b.RequestId, b.Record, b.SelectedDateHeaderName).Kind);

            using (var openA = authority.TryOpenTransfer(a.RequestId, a.Record.ArtId))
            {
                Assert.Equal(VatpOpenKind.Opened, openA.Kind);
            }

            using var openB = authority.TryOpenTransfer(b.RequestId, a.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, openB.Kind);
            Assert.True(openB.Lease!.Record.ArtData.Span.SequenceEqual(a.ArtData));
        }

        [Fact]
        public void OpenA_ConsumesOnlyA_BStillOpenable()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var a = RetentionTestArticles.RetainPrepared(authority, "<consume-a@example.test>");
            var b = RetentionTestArticles.Create("<consume-a@example.test>");
            Assert.Equal(
                ArticleRetentionKind.AlreadyPresent,
                authority.RetainCanonical(b.MessageId, b.RequestId, b.Record, b.SelectedDateHeaderName).Kind);

            using (var openA = authority.TryOpenTransfer(a.RequestId, a.Record.ArtId))
            {
                Assert.Equal(VatpOpenKind.Opened, openA.Kind);
            }

            using (var againA = authority.TryOpenTransfer(a.RequestId, a.Record.ArtId))
            {
                Assert.Equal(VatpOpenKind.Rejected, againA.Kind);
            }

            using var openB = authority.TryOpenTransfer(b.RequestId, a.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, openB.Kind);
        }

        [Fact]
        public void OpenB_ConsumesOnlyB()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var a = RetentionTestArticles.RetainPrepared(authority, "<consume-b@example.test>");
            var b = RetentionTestArticles.Create("<consume-b@example.test>");
            Assert.Equal(
                ArticleRetentionKind.AlreadyPresent,
                authority.RetainCanonical(b.MessageId, b.RequestId, b.Record, b.SelectedDateHeaderName).Kind);

            using (var openB = authority.TryOpenTransfer(b.RequestId, a.Record.ArtId))
            {
                Assert.Equal(VatpOpenKind.Opened, openB.Kind);
            }

            using (var againB = authority.TryOpenTransfer(b.RequestId, a.Record.ArtId))
            {
                Assert.Equal(VatpOpenKind.Rejected, againB.Kind);
            }

            using var openA = authority.TryOpenTransfer(a.RequestId, a.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, openA.Kind);
        }

        [Fact]
        public async Task ConcurrentArticleWork_SameMessageId_BothSuccessRequestIdsOpenable()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var first = RetentionTestArticles.Create("<concurrent-aw@example.test>", "first-bytes\r\n");
            var second = RetentionTestArticles.Create("<concurrent-aw@example.test>", "second-bytes\r\n");
            Assert.NotEqual(first.ArtData, second.ArtData);

            var barrier = new Barrier(2);
            var results = new ArticleRetentionResult[2];
            var tasks = new[]
            {
                Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    results[0] = authority.RetainCanonical(
                        first.MessageId, first.RequestId, first.Record, first.SelectedDateHeaderName);
                }),
                Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    results[1] = authority.RetainCanonical(
                        second.MessageId, second.RequestId, second.Record, second.SelectedDateHeaderName);
                }),
            };

            await Task.WhenAll(tasks).WaitAsync(TestTimeout);

            Assert.Equal(1, results.Count(static r => r.Kind == ArticleRetentionKind.Retained));
            Assert.Equal(1, results.Count(static r => r.Kind == ArticleRetentionKind.AlreadyPresent));
            Assert.Equal(1, authority.RetainedCount);

            var winnerArtData = results[0].Kind == ArticleRetentionKind.Retained ? first.ArtData : second.ArtData;
            using (var openFirst = authority.TryOpenTransfer(first.RequestId, first.Record.ArtId))
            {
                Assert.Equal(VatpOpenKind.Opened, openFirst.Kind);
                Assert.True(openFirst.Lease!.Record.ArtData.Span.SequenceEqual(winnerArtData));
            }

            using var openSecond = authority.TryOpenTransfer(second.RequestId, first.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, openSecond.Kind);
            Assert.True(openSecond.Lease!.Record.ArtData.Span.SequenceEqual(winnerArtData));
            Assert.Equal(winnerArtData.Length, authority.RetainedPayloadBytes);
        }

        [Fact]
        public async Task ConcurrentOpen_SameRequestId_OnlyOneSucceeds()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<concurrent-open-id@example.test>");

            var opened = 0;
            var rejected = 0;
            var barrier = new Barrier(2);
            var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                barrier.SignalAndWait();
                var result = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
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
        public void ReservationFailure_KeepsRequestId()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<reserve-keep@example.test>");

            using (var rejected = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId, _ => false))
            {
                Assert.Equal(VatpOpenKind.Rejected, rejected.Kind);
            }

            using var opened = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId, _ => true);
            Assert.Equal(VatpOpenKind.Opened, opened.Kind);
        }

        [Fact]
        public void WrongArticleId_DoesNotConsume()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<wrong-id-keep@example.test>");
            var wrongId = ArticleId.FromMessageId("<other@example.test>"u8);

            using (var rejected = authority.TryOpenTransfer(prepared.RequestId, wrongId))
            {
                Assert.Equal(VatpOpenKind.Rejected, rejected.Kind);
            }

            using var opened = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, opened.Kind);
        }

        [Fact]
        public void TtlExpiry_ClearsAllRequestIdsForEntry()
        {
            var time = new ManualTimeProvider(Start);
            var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(5));
            var a = RetentionTestArticles.RetainPrepared(authority, "<ttl-multi@example.test>");
            var b = RetentionTestArticles.Create("<ttl-multi@example.test>");
            Assert.Equal(
                ArticleRetentionKind.AlreadyPresent,
                authority.RetainCanonical(b.MessageId, b.RequestId, b.Record, b.SelectedDateHeaderName).Kind);

            time.Advance(TimeSpan.FromSeconds(5));
            using (var expiredA = authority.TryOpenTransfer(a.RequestId, a.Record.ArtId))
            {
                Assert.Equal(VatpOpenKind.Rejected, expiredA.Kind);
            }

            using var expiredB = authority.TryOpenTransfer(b.RequestId, a.Record.ArtId);
            Assert.Equal(VatpOpenKind.Rejected, expiredB.Kind);
            Assert.Equal(0, authority.RetainedCount);
            Assert.Equal(0, authority.RetainedPayloadBytes);
        }

        [Fact]
        public void BoundExceeded_DoesNotRevokeExistingRequestIds()
        {
            const int bound = 3;
            var authority = ArticleRetentionAuthorityTests.Create(
                new ManualTimeProvider(Start),
                maxBytes: 1024 * 1024,
                maxOpenableRequestIdsPerArticle: bound);

            var first = RetentionTestArticles.RetainPrepared(authority, "<bound@example.test>", "shared\r\n");
            var attached = new List<Guid> { first.RequestId };
            for (var i = 1; i < bound; i++)
            {
                var next = RetentionTestArticles.Create("<bound@example.test>", "shared\r\n");
                Assert.Equal(
                    ArticleRetentionKind.AlreadyPresent,
                    authority.RetainCanonical(next.MessageId, next.RequestId, next.Record, next.SelectedDateHeaderName).Kind);
                attached.Add(next.RequestId);
            }

            var overflow = RetentionTestArticles.Create("<bound@example.test>", "shared\r\n");
            var rejected = authority.RetainCanonical(
                overflow.MessageId,
                overflow.RequestId,
                overflow.Record,
                overflow.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.OpenableRequestIdLimitExceeded, rejected.Kind);
            Assert.True(rejected.IsCapacityRejected);
            Assert.False(rejected.IsAvailable);

            using (var overflowOpen = authority.TryOpenTransfer(overflow.RequestId, first.Record.ArtId))
            {
                Assert.Equal(VatpOpenKind.Rejected, overflowOpen.Kind);
            }

            foreach (var requestId in attached)
            {
                using var open = authority.TryOpenTransfer(requestId, first.Record.ArtId);
                Assert.Equal(VatpOpenKind.Opened, open.Kind);
                Assert.True(open.Lease!.Record.ArtData.Span.SequenceEqual(first.ArtData));
            }

            Assert.Equal(first.Record.ArtSize, authority.RetainedPayloadBytes);
            Assert.Equal(1, authority.RetainedCount);
        }

        [Fact]
        public void FirstWinsArtData_Unchanged()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var a = RetentionTestArticles.Create("<first-wins@example.test>", "canonical-one\r\n");
            var b = RetentionTestArticles.Create("<first-wins@example.test>", "canonical-two-longer\r\n");
            Assert.NotEqual(a.ArtData, b.ArtData);

            Assert.Equal(
                ArticleRetentionKind.Retained,
                authority.RetainCanonical(a.MessageId, a.RequestId, a.Record, a.SelectedDateHeaderName).Kind);
            Assert.Equal(
                ArticleRetentionKind.AlreadyPresent,
                authority.RetainCanonical(b.MessageId, b.RequestId, b.Record, b.SelectedDateHeaderName).Kind);

            Assert.Equal(a.Record.ArtSize, authority.RetainedPayloadBytes);
            using (var openA = authority.TryOpenTransfer(a.RequestId, a.Record.ArtId))
            {
                Assert.True(openA.Lease!.Record.ArtData.Span.SequenceEqual(a.ArtData));
            }

            using var openB = authority.TryOpenTransfer(b.RequestId, a.Record.ArtId);
            Assert.True(openB.Lease!.Record.ArtData.Span.SequenceEqual(a.ArtData));
            Assert.False(openB.Lease.Record.ArtData.Span.SequenceEqual(b.ArtData));
        }

        [Fact]
        public async Task PublishFailure_Requeue_RequestIdStillOpenable()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var requestId = Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId);
            var prepared = RetentionTestArticles.Create(
                ArticleWorkTestDeliveries.CanonicalMessageId,
                "payload\r\n");
            Assert.Equal(
                ArticleRetentionKind.Retained,
                authority.RetainCanonical(
                    prepared.MessageId,
                    requestId,
                    prepared.Record,
                    prepared.SelectedDateHeaderName).Kind);

            var publisher = new RecordingArticleWorkResponsePublisher { CompletesSuccessPublication = false };
            var handler = new ControllableArticleWorkHandler
            {
                Outcome = ArticleWorkOutcome.Success,
                Fqdn = ArticleWorkTestDeliveries.CanonicalFqdn,
                VatpPort = ArticleWorkTestDeliveries.CanonicalVatpPort,
                ArticleId = prepared.Record.ArtId,
            };
            var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, maxPayloadBytes: 64 * 1024);
            var channel = new FakeBackFillerRabbitMqChannel(generation: 1);

            var outcome = await pipeline.ProcessAsync(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                channel,
                channelStillCurrent: static () => true,
                CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Success, outcome);
            Assert.Empty(publisher.Published);
            var settlement = Assert.Single(channel.Settlements);
            Assert.False(settlement.Acknowledge);
            Assert.True(settlement.Requeue);

            using var open = authority.TryOpenTransfer(requestId, prepared.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, open.Kind);
            Assert.True(open.Lease!.Record.ArtData.Span.SequenceEqual(prepared.ArtData));
        }

        [Fact]
        public void AfterOpenA_Disconnect_RequiresNewRequestId()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var a = RetentionTestArticles.RetainPrepared(authority, "<disconnect-reopen@example.test>", "body\r\n");

            using (var openA = authority.TryOpenTransfer(a.RequestId, a.Record.ArtId))
            {
                Assert.Equal(VatpOpenKind.Opened, openA.Kind);
            }

            // Transfer lease released (disconnect/cancel after OPEN): RequestId A is consumed.
            using (var again = authority.TryOpenTransfer(a.RequestId, a.Record.ArtId))
            {
                Assert.Equal(VatpOpenKind.Rejected, again.Kind);
            }

            var reattach = RetentionTestArticles.Create("<disconnect-reopen@example.test>", "body\r\n");
            Assert.Equal(
                ArticleRetentionKind.AlreadyPresent,
                authority.RetainCanonical(
                    reattach.MessageId,
                    reattach.RequestId,
                    reattach.Record,
                    reattach.SelectedDateHeaderName).Kind);

            using var openNew = authority.TryOpenTransfer(reattach.RequestId, a.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, openNew.Kind);
            Assert.True(openNew.Lease!.Record.ArtData.Span.SequenceEqual(a.ArtData));
        }
    }
}
