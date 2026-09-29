using VectorNNTP.NNTPD.RabbitMq;

namespace VectorNNTP.NNTPD.Tests.RabbitMq;

public sealed class RabbitMqTopologyNamesTests
{
    [Theory]
    [InlineData("  BackFiller.Abavia  ", "backfiller.abavia")]
    [InlineData("  BackFiller.Storage  ", "backfiller.storage")]
    [InlineData(" BACKFILLER.STORAGE ", "backfiller.storage")]
    [InlineData("Abavia", "abavia")]
    public void Normalize_TrimsAndUsesInvariantLowerCase(string input, string expected)
    {
        Assert.Equal(expected, RabbitMqTopologyNames.Normalize(input));
    }

    [Fact]
    public void CreateFanoutQuorumBinding_NormalizesExchangeQueueAndRoutingKey()
    {
        var endpoint = RabbitMqArticleRetrievalEndpoints.CreateFanoutQuorumBinding("  BackFiller.Abavia  ");
        Assert.Equal("backfiller.abavia", endpoint.ExchangeName);
        Assert.Equal("backfiller.abavia", endpoint.QueueName);
        Assert.Equal("backfiller.abavia", endpoint.RoutingKey);
        Assert.Equal(endpoint.ExchangeName, endpoint.QueueName);
        Assert.Equal(endpoint.QueueName, endpoint.RoutingKey);
        Assert.True(endpoint.QueueDurable);
        Assert.False(endpoint.QueueExclusive);
        Assert.False(endpoint.QueueAutoDelete);
        Assert.True(endpoint.QueueArguments.TryGetValue(
            RabbitMqArticleRetrievalEndpoints.QueueTypeArgumentName,
            out var queueType));
        Assert.Equal(RabbitMqArticleRetrievalEndpoints.QuorumQueueType, queueType);
        Assert.Single(endpoint.QueueArguments);
        Assert.DoesNotContain("grabbers.", endpoint.ExchangeName, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredTopology_IsStorageOnly_AndNoGrabbersPrefix()
    {
        var storage = Assert.Single(ArticleRetrievalTopology.Required);
        Assert.Equal("backfiller.storage", storage.ExchangeName);
        Assert.Equal("backfiller.storage", storage.QueueName);
        Assert.Equal("backfiller.storage", storage.RoutingKey);
        Assert.Equal(storage.ExchangeName, storage.QueueName);
        Assert.Equal(storage.QueueName, storage.RoutingKey);
        Assert.Equal(storage.ExchangeName, RabbitMqTopologyNames.Normalize(storage.ExchangeName));
        Assert.DoesNotContain("storage.requests", storage.ExchangeName, StringComparison.Ordinal);
        Assert.DoesNotContain("grabbers.", storage.ExchangeName, StringComparison.Ordinal);
        Assert.DoesNotContain(
            ArticleRetrievalTopology.Required,
            static endpoint => endpoint.ExchangeName.StartsWith("backfiller.giganews", StringComparison.Ordinal));
    }
}
