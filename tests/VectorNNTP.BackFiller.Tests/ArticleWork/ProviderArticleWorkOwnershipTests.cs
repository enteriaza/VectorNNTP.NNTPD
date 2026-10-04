using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;
using VectorNNTP.Common.Articles.Processing;

namespace VectorNNTP.BackFiller.Tests.ArticleWork
{
    public sealed class ProviderArticleWorkOwnershipTests
    {
        [Fact]
        public async Task CanonicalArtData_RemainsValidAfterTheSourceBufferIsReused()
        {
            var pristine = ArticleWorkTestArticles.Valid();
            var source = pristine.ToArray();
            var parser = new NntpArticleParser("backfiller.test");
            var expected = ArticleRecordFactory.TryCreate(parser, pristine, ArticlePathMode.Traverse);
            Assert.True(expected.IsAccepted);
            var expectedBytes = expected.Record.ArtData.ToArray();
            await using var retention = CreateRetention();
            var retriever = new OwnedBufferRetriever(source, overwriteAfterCallback: true);
            var handler = new ProviderArticleWorkHandler(retriever, retention, parser);

            var result = await handler.HandleAsync(Item(ArticleWorkTestDeliveries.CanonicalMessageId), CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
            Assert.Null(result.Article);
            Assert.NotNull(handler.LastPayload);
            Assert.NotSame(source, handler.LastPayload);
            Assert.Equal(expectedBytes, handler.LastPayload);
            Assert.All(source, static value => Assert.Equal(0xFF, value));
            Assert.Equal(ArticleRetentionKind.Retained, handler.LastRetentionKind);
            Assert.Equal("backfiller.test", handler.LastFqdn);
            Assert.Equal(563, handler.LastVatpPort);
            Assert.True(retriever.Invoked);
        }

        [Fact]
        public async Task MessageIdMismatch_UsesTheCanonicalRecordAfterTheSourceIsReused()
        {
            var source = ArticleWorkTestArticles.Valid();
            var parser = new NntpArticleParser("backfiller.test");
            await using var retention = CreateRetention();
            var retriever = new OwnedBufferRetriever(source, overwriteAfterCallback: true);
            var handler = new ProviderArticleWorkHandler(retriever, retention, parser);

            var result = await handler.HandleAsync(Item("<other@example.invalid>"), CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.InvalidArticle, result.Outcome);
            Assert.Equal("MessageIdMismatch", result.Error);
            Assert.Null(handler.LastPayload);
            Assert.All(source, static value => Assert.Equal(0xFF, value));
            Assert.Equal(0, retention.RetainedCount);
        }

        [Fact]
        public async Task FailureAndCancellation_DoNotRequireThePayloadCallback()
        {
            await using var retention = CreateRetention();
            var notFound = new OwnedBufferRetriever(
                [],
                failure: ArticleRetrievalResult.Failed(
                    ArticleRetrievalKind.ArticleNotFound,
                    430,
                    "no such article",
                    sessionReusable: true));
            var handler = new ProviderArticleWorkHandler(notFound, retention, new NntpArticleParser("backfiller.test"));

            var missing = await handler.HandleAsync(Item(ArticleWorkTestDeliveries.CanonicalMessageId), CancellationToken.None);
            Assert.Equal(ArticleWorkOutcome.ArticleNotFound, missing.Outcome);
            Assert.False(notFound.Invoked);

            var cancelledRetriever = new OwnedBufferRetriever(ArticleWorkTestArticles.Valid());
            var cancelledHandler = new ProviderArticleWorkHandler(
                cancelledRetriever,
                retention,
                new NntpArticleParser("backfiller.test"));
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            var cancelled = await cancelledHandler.HandleAsync(Item(ArticleWorkTestDeliveries.CanonicalMessageId), cts.Token);
            Assert.Equal(ArticleWorkOutcome.Cancelled, cancelled.Outcome);
            Assert.False(cancelledRetriever.Invoked);
        }

        private static ArticleRetentionAuthority CreateRetention() =>
            new(
                new BackFillerArticleRetentionRuntimeOptions(1024 * 1024, TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), 256),
                "backfiller.test",
                563,
                TimeProvider.System,
                NullLogger<ArticleRetentionAuthority>.Instance);

        private static ArticleWorkItem Item(string messageId)
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            return new ArticleWorkItem(
                new ArticleWorkRequest(1, Guid.NewGuid(), messageId, "audit"),
                "corr",
                "reply",
                new ArticleWorkSettlementLease(channel, 1, 1));
        }

        private sealed class OwnedBufferRetriever : INntpArticleRetriever
        {
            private readonly byte[] _payload;
            private readonly ArticleRetrievalResult? _failure;
            private readonly bool _overwriteAfterCallback;

            public OwnedBufferRetriever(
                byte[] payload,
                bool overwriteAfterCallback = false,
                ArticleRetrievalResult? failure = null)
            {
                _payload = payload;
                _overwriteAfterCallback = overwriteAfterCallback;
                _failure = failure;
            }

            public bool Invoked { get; private set; }

            public Task<ArticleRetrievalResult> RetrieveAsync(
                ArticleWorkItem item,
                Func<ReadOnlyMemory<byte>, ArticleRecordCreateResult> consumePayload,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(consumePayload);
                if (_failure is not null)
                {
                    return Task.FromResult(_failure);
                }

                Invoked = true;
                _ = consumePayload(_payload);
                if (_overwriteAfterCallback)
                {
                    _payload.AsSpan().Fill(0xFF);
                }

                return Task.FromResult(ArticleRetrievalResult.Retrieved(220, "article follows"));
            }
        }
    }
}
