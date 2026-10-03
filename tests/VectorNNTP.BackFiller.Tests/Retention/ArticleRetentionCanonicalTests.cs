using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Transport.ArticleTransfer;

namespace VectorNNTP.BackFiller.Tests.Retention
{
    public sealed class ArticleRetentionCanonicalTests
    {
        private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public void RetainCanonical_TryOpenTransfer_happy_path()
        {
            var time = new ManualTimeProvider(Start);
            var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.Create("<vatp-canonical@example.test>");
            var retained = authority.RetainCanonical(
                prepared.MessageId,
                prepared.RequestId,
                prepared.Record,
                prepared.SelectedDateHeaderName);
            Assert.Equal(ArticleRetentionKind.Retained, retained.Kind);

            var open = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, open.Kind);
            Assert.NotNull(open.Lease);
            Assert.Equal(prepared.Record.ArtSize, open.Lease!.Record.ArtSize);
            Assert.True(open.Lease.Record.ArtData.Span.SequenceEqual(prepared.ArtData));
            Assert.Equal(prepared.Record.ArtSize, authority.RetainedPayloadBytes);
            open.Dispose();
        }

        [Fact]
        public void AlreadyPresent_cannot_create_divergent_retained_representations()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var first = RetentionTestArticles.Create("<already@example.test>", "first-canonical\r\n");
            var second = RetentionTestArticles.Create("<already@example.test>", "second-canonical\r\n");
            Assert.NotEqual(first.ArtData, second.ArtData);

            Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
                first.MessageId, first.RequestId, first.Record, first.SelectedDateHeaderName).Kind);
            Assert.Equal(ArticleRetentionKind.AlreadyPresent, authority.RetainCanonical(
                second.MessageId, second.RequestId, second.Record, second.SelectedDateHeaderName).Kind);

            Assert.Equal(first.Record.ArtSize, authority.RetainedPayloadBytes);
            Assert.Equal(1, authority.RetainedCount);

            using var open = authority.TryOpenTransfer(second.RequestId, first.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, open.Kind);
            Assert.True(open.Lease!.Record.ArtData.Span.SequenceEqual(first.ArtData));
            Assert.False(open.Lease.Record.ArtData.Span.SequenceEqual(second.ArtData));
            Assert.Equal(first.Record.ArtSize, authority.RetainedPayloadBytes);
        }

        [Fact]
        public void Retention_byte_accounting_equals_canonical_artdata_served_by_vatp_open()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<bytes@example.test>", "accounted\r\n");
            Assert.Equal(prepared.Record.ArtSize, authority.RetainedPayloadBytes);

            using var open = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, open.Kind);
            Assert.Equal(open.Lease!.Record.ArtData.Length, authority.RetainedPayloadBytes);
            Assert.Equal(prepared.Record.ArtSize, open.Lease.Record.ArtData.Length);
        }

        [Fact]
        public void Expired_entries_are_released_and_open_is_rejected()
        {
            var time = new ManualTimeProvider(Start);
            var authority = ArticleRetentionAuthorityTests.Create(time, maxBytes: 1024 * 1024, ttl: TimeSpan.FromSeconds(30));
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<expired@example.test>");
            Assert.Equal(prepared.Record.ArtSize, authority.RetainedPayloadBytes);

            time.Advance(TimeSpan.FromSeconds(30));
            using var open = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
            Assert.Equal(VatpOpenKind.Rejected, open.Kind);
            Assert.Equal(0, authority.RetainedPayloadBytes);
            Assert.Equal(0, authority.RetainedCount);
        }

        [Fact]
        public void Multiple_request_ids_for_same_article_id_remain_independently_openable()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var first = RetentionTestArticles.Create("<reqids@example.test>", "body-a\r\n");
            var second = RetentionTestArticles.Create("<reqids@example.test>", "body-b\r\n");
            Assert.Equal(first.Record.ArtId, second.Record.ArtId);

            Assert.Equal(ArticleRetentionKind.Retained, authority.RetainCanonical(
                first.MessageId, first.RequestId, first.Record, first.SelectedDateHeaderName).Kind);
            Assert.Equal(ArticleRetentionKind.AlreadyPresent, authority.RetainCanonical(
                second.MessageId, second.RequestId, second.Record, second.SelectedDateHeaderName).Kind);

            using (var openA = authority.TryOpenTransfer(first.RequestId, first.Record.ArtId))
            {
                Assert.Equal(VatpOpenKind.Opened, openA.Kind);
                Assert.True(openA.Lease!.Record.ArtData.Span.SequenceEqual(first.ArtData));
            }

            using var openB = authority.TryOpenTransfer(second.RequestId, first.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, openB.Kind);
            Assert.True(openB.Lease!.Record.ArtData.Span.SequenceEqual(first.ArtData));
        }

        [Fact]
        public void Wrong_ArticleId_does_not_consume_RequestId()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<wrong-id@example.test>");

            var wrongId = ArticleId.FromMessageId("<other@example.test>"u8);
            using (var rejected = authority.TryOpenTransfer(prepared.RequestId, wrongId))
            {
                Assert.Equal(VatpOpenKind.Rejected, rejected.Kind);
            }

            using var opened = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
            Assert.Equal(VatpOpenKind.Opened, opened.Kind);
        }

        [Fact]
        public void Second_open_with_same_RequestId_fails()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<second-open@example.test>");

            using (var first = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId))
            {
                Assert.Equal(VatpOpenKind.Opened, first.Kind);
            }

            using var second = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
            Assert.Equal(VatpOpenKind.Rejected, second.Kind);
        }

        [Fact]
        public void TryCancelPendingRequest_removes_open_eligibility()
        {
            var authority = ArticleRetentionAuthorityTests.Create(new ManualTimeProvider(Start), maxBytes: 1024 * 1024);
            var prepared = RetentionTestArticles.RetainPrepared(authority, "<cancel-pending@example.test>");

            Assert.True(authority.TryCancelPendingRequest(prepared.RequestId));
            using var open = authority.TryOpenTransfer(prepared.RequestId, prepared.Record.ArtId);
            Assert.Equal(VatpOpenKind.Rejected, open.Kind);
            Assert.False(authority.TryCancelPendingRequest(prepared.RequestId));
        }
    }
}
