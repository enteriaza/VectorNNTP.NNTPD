// Copyright © Chris Knipe cknipe@opticnetworks.net

using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.Tests.Fixtures;
using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.BackFiller.Tests.ArticleWork
{
    public sealed class ArticleWorkOversizedSecurityTests
    {
        private const int ConfiguredMaxPayload = 1024;

        [Fact]
        public void Parser_rejects_oversized_payload_and_returns_reason()
        {
            var validJson = """{" + "\"version\":1,\"requestId\":\"7c1cb8a0-95f9-4c13-8e53-339773e3afaa\",\"messageId\":\"<test@example.invalid>\",\"backbone\":\"TestBone\"}""";
            var oversized = validJson + new string('X', ConfiguredMaxPayload);
            var delivery = ArticleWorkTestDeliveries.Create(oversized);

            var parsed = ArticleWorkRequestParser.Parse(delivery, "TestBone", ConfiguredMaxPayload);

            Assert.False(parsed.IsValid);
            Assert.NotNull(parsed.Failure);
            Assert.Contains("WorkRequestMaxPayloadBytes", parsed.Failure.Reason, System.StringComparison.Ordinal);
        }

        [Fact]
        public async Task Pipeline_ProcessAsync_returns_diagnostic_reason_for_oversized_payload()
        {
            var handler = new TestNullHandler();
            var publisher = new TestPublisher();
            var pipeline = new ArticleWorkDeliveryPipeline(handler, publisher, ConfiguredMaxPayload);

            var validJson = """{" + "\"version\":1,\"requestId\":\"7c1cb8a0-95f9-4c13-8e53-339773e3afaa\",\"messageId\":\"<test@example.invalid>\",\"backbone\":\"TestBone\"}""";
            var oversized = validJson + new string('X', ConfiguredMaxPayload);
            var delivery = ArticleWorkTestDeliveries.Create(oversized);

            var channel = new TestManualAckChannel();
            var backbone = "TestBone";
            var channelStillCurrent = () => true;

            var parsed = ArticleWorkRequestParser.Parse(delivery, backbone, ConfiguredMaxPayload);
            var outcome = await pipeline.ProcessAsync(
                delivery,
                backbone,
                channel,
                channelStillCurrent,
                CancellationToken.None,
                parsed);

            Assert.Equal(ArticleWorkOutcome.InvalidRequest, outcome);
            Assert.False(parsed.IsValid);
            Assert.NotNull(parsed.Failure);
            Assert.Contains("WorkRequestMaxPayloadBytes", parsed.Failure.Reason, System.StringComparison.Ordinal);
        }

        private sealed class TestNullHandler : IArticleWorkHandler
        {
            public ValueTask<ArticleWorkHandlerResult> HandleAsync(ArticleWorkItem item, CancellationToken cancellationToken)
            {
                return ValueTask.FromResult(new ArticleWorkHandlerResult(ArticleWorkOutcome.Cancelled, null, null, null, null));
            }
        }

        private sealed class TestPublisher : IArticleWorkResponsePublisher
        {
            public bool CompletesSuccessPublication => false;

            public Task PublishAsync(ArticleWorkResponseIntent intent, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private sealed class TestManualAckChannel : IRabbitMqManualAckChannel
        {
            public long Generation => 1;
            public bool IsOpen => true;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            public Task ExchangeDeclareAsync(string exchange, string type, bool durable, bool autoDelete, IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task QueueDeclareAsync(string queue, bool durable, bool exclusive, bool autoDelete, IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task QueueBindAsync(string queue, string exchange, string routingKey, IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<string> BasicConsumeAsync(string queue, ushort prefetch, Func<RabbitMqManualAckDelivery, Task> onDeliveryAsync, CancellationToken cancellationToken) => Task.FromResult(string.Empty);
            public Task BasicCancelAsync(string consumerTag, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task BasicAckAsync(ulong deliveryTag, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task BasicNackAsync(ulong deliveryTag, bool requeue, CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
