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
        Assert.DoesNotContain("grabbers.", endpoint.ExchangeName, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredTopology_HasThirteenNormalizedEndpoints_AndNoGrabbersPrefix()
    {
        Assert.Equal(13, ArticleRetrievalTopology.Required.Count);
        Assert.All(
            ArticleRetrievalTopology.Required,
            static endpoint =>
            {
                Assert.Equal(endpoint.ExchangeName, endpoint.QueueName);
                Assert.Equal(endpoint.QueueName, endpoint.RoutingKey);
                Assert.Equal(endpoint.ExchangeName, RabbitMqTopologyNames.Normalize(endpoint.ExchangeName));
                Assert.StartsWith("backfiller.", endpoint.ExchangeName, StringComparison.Ordinal);
                Assert.DoesNotContain("storage.requests", endpoint.ExchangeName, StringComparison.Ordinal);
                Assert.DoesNotContain("storage.requests", endpoint.QueueName, StringComparison.Ordinal);
                Assert.DoesNotContain("storage.requests", endpoint.RoutingKey, StringComparison.Ordinal);
                Assert.DoesNotContain("grabbers.", endpoint.ExchangeName, StringComparison.Ordinal);
                Assert.DoesNotContain("grabbers.", endpoint.QueueName, StringComparison.Ordinal);
                Assert.DoesNotContain("grabbers.", endpoint.RoutingKey, StringComparison.Ordinal);
            });

        Assert.Equal(
            [
                "backfiller.abavia",
                "backfiller.altopia",
                "backfiller.baseip",
                "backfiller.eweka",
                "backfiller.elbracht",
                "backfiller.giganews",
                "backfiller.gtt",
                "backfiller.highwinds",
                "backfiller.itshosted",
                "backfiller.novia",
                "backfiller.uexpress",
                "backfiller.usenetnode1",
                "backfiller.storage",
            ],
            ArticleRetrievalTopology.Required.Select(static endpoint => endpoint.ExchangeName));
    }
}
