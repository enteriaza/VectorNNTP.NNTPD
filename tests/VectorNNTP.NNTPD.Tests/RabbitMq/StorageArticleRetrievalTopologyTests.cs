using VectorNNTP.NNTPD.RabbitMq;

namespace VectorNNTP.NNTPD.Tests.RabbitMq;

public sealed class StorageArticleRetrievalTopologyTests
{
    [Fact]
    public void Definition_IsStorageRequests_FanoutQuorumBinding()
    {
        var definition = StorageArticleRetrievalTopology.Definition;

        Assert.Equal("storage.requests", StorageArticleRetrievalTopology.EntityName);
        Assert.Equal("storage.requests", definition.ExchangeName);
        Assert.Equal("storage.requests", definition.QueueName);
        Assert.Equal("storage.requests", definition.RoutingKey);
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
    }

    [Fact]
    public void StorageRequests_IsNotABackFillerProvider()
    {
        Assert.Equal(12, BackfillArticleRetrievalTopology.Providers.Length);
        Assert.Equal(12, BackfillArticleRetrievalTopology.Definitions.Count);
        Assert.DoesNotContain("storage.requests", BackfillArticleRetrievalTopology.Providers, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            BackfillArticleRetrievalTopology.Definitions,
            static definition => definition.ExchangeName.StartsWith("grabbers.storage", StringComparison.Ordinal));
        Assert.False(StorageArticleRetrievalTopology.EntityName.StartsWith("grabbers.", StringComparison.Ordinal));
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
        Assert.Equal("storage.requests", storage.ExchangeName);
        Assert.Equal("storage.requests", storage.QueueName);
        Assert.Equal("storage.requests", storage.RoutingKey);
        Assert.DoesNotContain(
            ArticleRetrievalTopology.Required.Take(12),
            static endpoint => endpoint.ExchangeName == "storage.requests");
    }
}
