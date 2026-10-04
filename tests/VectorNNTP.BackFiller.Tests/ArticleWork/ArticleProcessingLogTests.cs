using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.Configuration;
using VectorNNTP.BackFiller.Nntp;
using VectorNNTP.BackFiller.Retention;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.BackFiller.Tests.TestDoubles;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;

namespace VectorNNTP.BackFiller.Tests.ArticleWork
{
    public sealed class ArticleProcessingLogTests
    {
        private const int ArticleProcessedEventId = 5321;

        [Fact]
        public async Task Found_logs_one_information_event_with_identity_outcome_and_elapsed()
        {
            var started = Stopwatch.GetTimestamp() - (long)Math.Round(1.25 * Stopwatch.Frequency);
            var logger = new CollectingLogger<ProviderArticleWorkHandler>();
            await using var retention = CreateRetention();
            var handler = CreateHandler(
                logger,
                retention,
                new ScriptedRetriever(ArticleWorkTestArticles.Valid(), ArticleRetrievalResult.Retrieved(220, "follows"), started));

            var result = await handler.HandleAsync(Item(ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews"), CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
            var entry = AssertSingleCompletion(logger);
            AssertCompletion(entry, ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews", "Found");
            var seconds = ElapsedSeconds(entry.Message);
            Assert.InRange(seconds, 1.25, 5.0);
            Assert.DoesNotContain("body", entry.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task NotFound_logs_one_information_event()
        {
            var logger = new CollectingLogger<ProviderArticleWorkHandler>();
            await using var retention = CreateRetention();
            var handler = CreateHandler(
                logger,
                retention,
                new ScriptedRetriever(
                    payload: null,
                    ArticleRetrievalResult.Failed(ArticleRetrievalKind.ArticleNotFound, 430, "no such article", sessionReusable: true),
                    commandStartedTimestamp: Stopwatch.GetTimestamp()));

            var result = await handler.HandleAsync(Item(ArticleWorkTestDeliveries.CanonicalMessageId, "Eweka"), CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.ArticleNotFound, result.Outcome);
            var entry = AssertSingleCompletion(logger);
            AssertCompletion(entry, ArticleWorkTestDeliveries.CanonicalMessageId, "Eweka", "NotFound");
        }

        [Fact]
        public async Task ValidationFailure_logs_one_information_event_with_the_parser_reason()
        {
            var logger = new CollectingLogger<ProviderArticleWorkHandler>();
            await using var retention = CreateRetention();
            var handler = CreateHandler(
                logger,
                retention,
                new ScriptedRetriever(
                    ArticleWorkTestArticles.InvalidMessageId(),
                    ArticleRetrievalResult.Retrieved(220, "follows"),
                    Stopwatch.GetTimestamp()));

            var result = await handler.HandleAsync(Item(ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews"), CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.InvalidArticle, result.Outcome);
            Assert.Equal("InvalidMessageId", result.Error);
            var entry = AssertSingleCompletion(logger);
            AssertCompletion(entry, ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews", "ValidationFailed");
            Assert.Contains("reason=InvalidMessageId", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("malformed-id", entry.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task TransportFailure_logs_one_information_event_with_failed()
        {
            var logger = new CollectingLogger<ProviderArticleWorkHandler>();
            await using var retention = CreateRetention();
            var handler = CreateHandler(
                logger,
                retention,
                new ScriptedRetriever(
                    payload: null,
                    ArticleRetrievalResult.Failed(ArticleRetrievalKind.ProviderFailure, null, "connection reset", sessionReusable: false),
                    Stopwatch.GetTimestamp()));

            var result = await handler.HandleAsync(Item(ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews"), CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.ProviderFailure, result.Outcome);
            var entry = AssertSingleCompletion(logger);
            AssertCompletion(entry, ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews", "Failed");
            Assert.DoesNotContain("connection reset", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("ValidationFailed", entry.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Unresolved_identity_uses_the_existing_none_placeholder()
        {
            var logger = new CollectingLogger<ProviderArticleWorkHandler>();
            await using var retention = CreateRetention();
            var handler = CreateHandler(
                logger,
                retention,
                new ScriptedRetriever(
                    payload: null,
                    ArticleRetrievalResult.Failed(ArticleRetrievalKind.ProviderFailure, null, "down", sessionReusable: false),
                    commandStartedTimestamp: 0));

            var result = await handler.HandleAsync(Item(" ", ""), CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.ProviderFailure, result.Outcome);
            var entry = AssertSingleCompletion(logger);
            AssertCompletion(entry, "(none)", "(none)", "Failed");
            Assert.Contains("elapsed=0.000 seconds", entry.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Live_article_keeps_debug_wire_logging_and_emits_one_completion_event()
        {
            var wire = new CollectingLogger<NntpProviderRegistry>();
            var completion = new CollectingLogger<ProviderArticleWorkHandler>();
            var factory = new ScriptedNntpTransportFactory();
            var server = new ScriptedNntpServer();
            server.Respond(_ => ArticleWorkTestArticles.ArticleResponse(ArticleWorkTestArticles.Valid()));
            factory.Enqueue(server);
            await using var registry = new NntpProviderRegistry(
                new StaticBackFillerProviderCatalog(
                [
                    new BackFillerProviderDefinition("Giganews", "127.0.0.1", 119, false, null, null, 0, 1),
                ]),
                factory,
                NntpSessionOptions.Default with
                {
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    CommandTimeout = TimeSpan.FromSeconds(2),
                    ReceiveTimeout = TimeSpan.FromSeconds(2),
                },
                TimeSpan.FromSeconds(2),
                wire);
            await using var retention = CreateRetention();
            var handler = new ProviderArticleWorkHandler(
                new NntpArticleRetriever(registry),
                retention,
                new NntpArticleParser("backfiller.test"),
                sharedConfiguration: null,
                serverId: 0,
                completion);

            var result = await handler.HandleAsync(Item(ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews"), CancellationToken.None);

            Assert.Equal(ArticleWorkOutcome.Success, result.Outcome);
            var entry = AssertSingleCompletion(completion);
            AssertCompletion(entry, ArticleWorkTestDeliveries.CanonicalMessageId, "Giganews", "Found");
            Assert.Contains(
                wire.Entries,
                static candidate => candidate.Level == LogLevel.Debug
                    && candidate.Message.Contains("TX: ARTICLE <12345@example.invalid>", StringComparison.Ordinal));
            Assert.Contains(
                wire.Entries,
                static candidate => candidate.Level == LogLevel.Debug
                    && candidate.Message.Contains("RX: ARTICLE payload complete", StringComparison.Ordinal));
            Assert.DoesNotContain(
                wire.Entries,
                static candidate => candidate.Message.Contains("Article processed", StringComparison.Ordinal));
            Assert.DoesNotContain(
                wire.Entries,
                static candidate => candidate.Message.Contains("NNTP provider retrieval failed", StringComparison.Ordinal));
            Assert.DoesNotContain("body", entry.Message, StringComparison.Ordinal);
        }

        private static ProviderArticleWorkHandler CreateHandler(
            CollectingLogger<ProviderArticleWorkHandler> logger,
            ArticleRetentionAuthority retention,
            INntpArticleRetriever retriever)
        {
            return new ProviderArticleWorkHandler(
                retriever,
                retention,
                new NntpArticleParser("backfiller.test"),
                sharedConfiguration: null,
                serverId: 0,
                logger);
        }

        private static ArticleRetentionAuthority CreateRetention() =>
            new(
                new BackFillerArticleRetentionRuntimeOptions(1024 * 1024, TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), 256),
                "backfiller.test",
                563,
                TimeProvider.System,
                NullLogger<ArticleRetentionAuthority>.Instance);

        private static ArticleWorkItem Item(string messageId, string backbone)
        {
            var channel = new FakeBackFillerRabbitMqChannel(1);
            return new ArticleWorkItem(
                new ArticleWorkRequest(1, Guid.NewGuid(), messageId, backbone),
                "corr",
                "reply",
                new ArticleWorkSettlementLease(channel, 1, 1));
        }

        private static CollectedLog AssertSingleCompletion(CollectingLogger<ProviderArticleWorkHandler> logger)
        {
            var information = logger.Entries.Where(static entry => entry.Level == LogLevel.Information).ToArray();
            var entry = Assert.Single(information);
            Assert.Equal(ArticleProcessedEventId, entry.EventId.Id);
            Assert.DoesNotContain(
                logger.Entries,
                static candidate => candidate.Level == LogLevel.Information && candidate.EventId.Id != ArticleProcessedEventId);
            return entry;
        }

        private static void AssertCompletion(CollectedLog entry, string messageId, string backbone, string outcome)
        {
            Assert.Contains($"messageId={messageId}", entry.Message, StringComparison.Ordinal);
            Assert.Contains($"backbone={backbone}", entry.Message, StringComparison.Ordinal);
            Assert.Contains($"outcome={outcome}", entry.Message, StringComparison.Ordinal);
            Assert.Matches(ElapsedPattern(), entry.Message);
        }

        private static double ElapsedSeconds(string message)
        {
            var match = ElapsedPattern().Match(message);
            Assert.True(match.Success);
            return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        private static Regex ElapsedPattern() => new(@"elapsed=(\d+\.\d{3}) seconds", RegexOptions.CultureInvariant);

        private sealed class ScriptedRetriever : INntpArticleRetriever
        {
            private readonly byte[]? _payload;
            private readonly ArticleRetrievalResult _result;
            private readonly long _commandStartedTimestamp;

            public ScriptedRetriever(byte[]? payload, ArticleRetrievalResult result, long commandStartedTimestamp)
            {
                _payload = payload;
                _result = result;
                _commandStartedTimestamp = commandStartedTimestamp;
            }

            public Task<ArticleRetrievalResult> RetrieveAsync(
                ArticleWorkItem item,
                int maxArticleBytes,
                Func<ReadOnlyMemory<byte>, ArticleRecordCreateResult> consumePayload,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(item);
                ArgumentNullException.ThrowIfNull(consumePayload);
                if (_payload is not null)
                {
                    _ = consumePayload(_payload);
                }

                _result.CommandStartedTimestamp = _commandStartedTimestamp;
                return Task.FromResult(_result);
            }
        }
    }
}
