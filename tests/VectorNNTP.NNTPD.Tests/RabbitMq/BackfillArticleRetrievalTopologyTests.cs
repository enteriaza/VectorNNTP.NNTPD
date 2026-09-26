using VectorNNTP.NNTPD.RabbitMq;

namespace VectorNNTP.NNTPD.Tests.RabbitMq;

public sealed class BackfillArticleRetrievalTopologyTests
{
    private static readonly string[] ExpectedProviders =
    [
        "Abavia",
        "Altopia",
        "BaseIP",
        "Eweka",
        "Elbracht",
        "Giganews",
        "GTT",
        "Highwinds",
        "ItsHosted",
        "Novia",
        "UExpress",
        "UsenetNode1",
    ];

    [Fact]
    public void Providers_AreExactlyTheTwelveBackFillerIdentifiers()
    {
        Assert.Equal(ExpectedProviders, BackfillArticleRetrievalTopology.Providers);
        Assert.Equal(12, BackfillArticleRetrievalTopology.Definitions.Count);
        Assert.Equal(ExpectedProviders, BackfillArticleRetrievalTopology.Definitions.Select(static d => d.Provider));
    }

    [Fact]
    public void Definitions_UseBackFillerLegacyEntityNamesAndFanoutBinding()
    {
        foreach (var definition in BackfillArticleRetrievalTopology.Definitions)
        {
            var entityName = $"grabbers.{definition.Provider.ToLowerInvariant()}";
            Assert.Equal(entityName, definition.ExchangeName);
            Assert.Equal(entityName, definition.QueueName);
            Assert.Equal(entityName, definition.RoutingKey);
            Assert.Equal("fanout", definition.ExchangeType);
            Assert.True(definition.ExchangeDurable);
            Assert.False(definition.ExchangeAutoDelete);
            Assert.True(definition.QueueDurable);
            Assert.False(definition.QueueExclusive);
            Assert.False(definition.QueueAutoDelete);
        }

        Assert.Equal("grabbers.abavia", BackfillArticleRetrievalTopology.BuildLegacyProviderEntityName("Abavia"));
        Assert.Equal("grabbers.gtt", BackfillArticleRetrievalTopology.BuildLegacyProviderEntityName("GTT"));
        Assert.Equal("grabbers.usenetnode1", BackfillArticleRetrievalTopology.BuildLegacyProviderEntityName("UsenetNode1"));
        Assert.Equal("grabbers.baseip", BackfillArticleRetrievalTopology.BuildLegacyProviderEntityName("BaseIP"));
        Assert.Equal("grabbers.itshosted", BackfillArticleRetrievalTopology.BuildLegacyProviderEntityName("ItsHosted"));
        Assert.Equal("grabbers.uexpress", BackfillArticleRetrievalTopology.BuildLegacyProviderEntityName("UExpress"));
    }

    [Fact]
    public void Definitions_RequireQuorumQueueTypeOnEveryQueue()
    {
        foreach (var definition in BackfillArticleRetrievalTopology.Definitions)
        {
            Assert.NotNull(definition.QueueArguments);
            Assert.Single(definition.QueueArguments);
            Assert.True(definition.QueueArguments.TryGetValue(
                BackfillArticleRetrievalTopology.QueueTypeArgumentName,
                out var queueType));
            Assert.Equal(BackfillArticleRetrievalTopology.QuorumQueueType, queueType as string);
            Assert.False(definition.QueueArguments.ContainsKey("x-message-ttl"));
            Assert.False(definition.QueueArguments.ContainsKey("x-expires"));
        }
    }

    [Fact]
    public void BuildDefinitions_PreservesProviderCasing_AndLowercasesOnlyEntityNames()
    {
        var definition = Assert.Single(BackfillArticleRetrievalTopology.BuildDefinitions(["Giganews"]));
        Assert.Equal("Giganews", definition.Provider);
        Assert.Equal("grabbers.giganews", definition.ExchangeName);
        Assert.Equal("grabbers.giganews", definition.QueueName);
        Assert.Equal("grabbers.giganews", definition.RoutingKey);
    }

    [Fact]
    public void BuildDefinitions_SkipsBlanksAndDuplicateProviders()
    {
        var definitions = BackfillArticleRetrievalTopology.BuildDefinitions(
            ["Giganews", " ", "giganews", "Eweka", "Giganews"]);

        Assert.Equal(2, definitions.Count);
        Assert.Equal("Giganews", definitions[0].Provider);
        Assert.Equal("Eweka", definitions[1].Provider);
    }
}
