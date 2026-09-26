using VectorNNTP.NNTPD.RabbitMq;

namespace VectorNNTP.NNTPD.Tests.RabbitMq;

public sealed class StorageArticleRetrievalTopologyTests
{
    [Fact]
    public void Definition_IsBackfillerStorage_FanoutQuorumBinding()
    {
        var definition = StorageArticleRetrievalTopology.Definition;

        Assert.Equal("backfiller.storage", StorageArticleRetrievalTopology.EntityName);
        Assert.Equal("backfiller.storage", RabbitMqTopologyNames.Normalize("  BackFiller.Storage  "));
        Assert.Equal("backfiller.storage", RabbitMqTopologyNames.Normalize("  BACKFILLER.STORAGE "));
        Assert.Equal("backfiller.storage", definition.ExchangeName);
        Assert.Equal("backfiller.storage", definition.QueueName);
        Assert.Equal("backfiller.storage", definition.RoutingKey);
        Assert.Equal(definition.ExchangeName, definition.QueueName);
        Assert.Equal(definition.QueueName, definition.RoutingKey);
        Assert.Equal("fanout", definition.ExchangeType);
        Assert.True(definition.ExchangeDurable);
        Assert.False(definition.ExchangeAutoDelete);
        Assert.True(definition.QueueDurable);
        Assert.False(definition.QueueExclusive);
        Assert.False(definition.QueueAutoDelete);
        Assert.NotNull(definition.QueueArguments);
        Assert.Single(definition.QueueArguments);
        Assert.True(definition.QueueArguments.TryGetValue(
            RabbitMqArticleRetrievalEndpoints.QueueTypeArgumentName,
            out var queueType));
        Assert.Equal(RabbitMqArticleRetrievalEndpoints.QuorumQueueType, queueType as string);
        Assert.False(definition.QueueArguments.ContainsKey("x-message-ttl"));
        Assert.False(definition.QueueArguments.ContainsKey("x-expires"));
        Assert.DoesNotContain("storage.requests", definition.ExchangeName, StringComparison.Ordinal);
        Assert.DoesNotContain("grabbers.", definition.ExchangeName, StringComparison.Ordinal);
    }

    [Fact]
    public void StorageEndpoint_IsNotABackFillerProvider()
    {
        Assert.Equal(12, BackfillArticleRetrievalTopology.Providers.Length);
        Assert.Equal(12, BackfillArticleRetrievalTopology.Definitions.Count);
        Assert.DoesNotContain("storage", BackfillArticleRetrievalTopology.Providers, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage.requests", BackfillArticleRetrievalTopology.Providers, StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith("backfiller.", StorageArticleRetrievalTopology.EntityName, StringComparison.Ordinal);
        Assert.Equal("backfiller.storage", StorageArticleRetrievalTopology.EntityName);
        Assert.DoesNotContain("storage.requests", StorageArticleRetrievalTopology.EntityName, StringComparison.Ordinal);
        Assert.DoesNotContain("grabbers.", StorageArticleRetrievalTopology.EntityName, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredTopology_IsTwelveBackFillerEndpointsPlusStorage()
    {
        Assert.Equal(13, ArticleRetrievalTopology.Required.Count);
        Assert.Equal(
            BackfillArticleRetrievalTopology.Definitions.Select(static d => d.ExchangeName),
            ArticleRetrievalTopology.Required.Take(12).Select(static d => d.ExchangeName));
        var storage = ArticleRetrievalTopology.Required[12];
        Assert.Equal(StorageArticleRetrievalTopology.Definition, storage);
        Assert.Equal("backfiller.storage", storage.ExchangeName);
        Assert.Equal("backfiller.storage", storage.QueueName);
        Assert.Equal("backfiller.storage", storage.RoutingKey);
        Assert.Equal(storage.ExchangeName, storage.QueueName);
        Assert.Equal(storage.QueueName, storage.RoutingKey);
        Assert.All(
            ArticleRetrievalTopology.Required,
            static endpoint =>
            {
                Assert.StartsWith("backfiller.", endpoint.ExchangeName, StringComparison.Ordinal);
                Assert.StartsWith("backfiller.", endpoint.QueueName, StringComparison.Ordinal);
                Assert.StartsWith("backfiller.", endpoint.RoutingKey, StringComparison.Ordinal);
                Assert.DoesNotContain("storage.requests", endpoint.ExchangeName, StringComparison.Ordinal);
                Assert.DoesNotContain("storage.requests", endpoint.QueueName, StringComparison.Ordinal);
                Assert.DoesNotContain("storage.requests", endpoint.RoutingKey, StringComparison.Ordinal);
                Assert.DoesNotContain("grabbers.", endpoint.ExchangeName, StringComparison.Ordinal);
                Assert.DoesNotContain("grabbers.", endpoint.QueueName, StringComparison.Ordinal);
                Assert.DoesNotContain("grabbers.", endpoint.RoutingKey, StringComparison.Ordinal);
            });
        Assert.DoesNotContain(
            ArticleRetrievalTopology.Required.Take(12),
            static endpoint => endpoint.ExchangeName == "backfiller.storage");
    }
}
