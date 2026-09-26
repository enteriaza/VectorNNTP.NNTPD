using System.Text;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.RabbitMq;

namespace VectorNNTP.BackFiller.Tests.Fixtures;

internal static class ArticleWorkTestDeliveries
{
    internal const string CanonicalRequestJson =
        """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews"}""";

    internal const string ProtocolValidExampleJson =
        """{"version":1,"requestId":"d0648b54-b1b8-4717-95e1-7b31bf7fd1bd","messageId":"<abc@example.invalid>","backbone":"Eweka"}""";

    internal const string ProtocolInvalidExampleJson =
        """{"version":2,"requestId":"not-a-guid","messageId":"","backbone":"WrongBackbone"}""";

    internal const string CanonicalRequestId = "7c1cb8a0-95f9-4c13-8e53-339773e3afaa";
    internal const string CanonicalCorrelationId = "61d4b5f6-8b24-4c14-8d40-741a378abfc8";
    internal const string CanonicalReplyTo = "nnrpd.rpc.responses";
    internal const string CanonicalMessageId = "<12345@example.invalid>";

    internal static BackFillerRabbitMqConsumedDelivery Create(
        string json,
        string? correlationId = CanonicalCorrelationId,
        string? replyTo = CanonicalReplyTo,
        string? contentType = ArticleWorkRequestParser.JsonContentType,
        string? requestIdHeader = CanonicalRequestId,
        ulong deliveryTag = 7,
        long generation = 1)
    {
        return new BackFillerRabbitMqConsumedDelivery(
            deliveryTag,
            Encoding.UTF8.GetBytes(json),
            correlationId,
            replyTo,
            contentType,
            requestIdHeader,
            Redelivered: false,
            RoutingKey: "backfiller.giganews",
            Exchange: "backfiller.giganews",
            ConsumerTag: "ctag-test",
            generation);
    }

    internal static BackFillerRabbitMqConsumedDelivery Canonical(
        ulong deliveryTag = 7,
        long generation = 1,
        string? requestIdHeader = CanonicalRequestId) =>
        Create(CanonicalRequestJson, deliveryTag: deliveryTag, generation: generation, requestIdHeader: requestIdHeader);

    internal static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        using var safety = new CancellationTokenSource(timeout);
        while (!predicate())
        {
            await Task.Delay(10, safety.Token).ConfigureAwait(false);
        }
    }
}
