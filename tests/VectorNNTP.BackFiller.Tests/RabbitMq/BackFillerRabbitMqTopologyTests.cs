using VectorNNTP.BackFiller.RabbitMq;

namespace VectorNNTP.BackFiller.Tests.RabbitMq;

public sealed class BackFillerRabbitMqTopologyTests
{
    [Fact]
    public void ComposeProviderEntity_uses_backfiller_prefix_and_invariant_lower_case()
    {
        Assert.Equal("backfiller.giganews", BackFillerRabbitMqTopology.ComposeProviderEntity("Giganews"));
        Assert.Equal("backfiller.usenetnode1", BackFillerRabbitMqTopology.ComposeProviderEntity(" UsenetNode1 "));
        Assert.Equal("backfiller.storage", BackFillerRabbitMqTopology.StorageEntity);
        Assert.DoesNotContain(
            BackFillerRabbitMqTopology.ProviderBackbones,
            static backbone => backbone.Contains("storage", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(12, BackFillerRabbitMqTopology.ProviderBackbones.Count);
        Assert.All(
            BackFillerRabbitMqTopology.ProviderBackbones,
            static backbone => Assert.StartsWith("backfiller.", BackFillerRabbitMqTopology.ComposeProviderEntity(backbone), StringComparison.Ordinal));
        Assert.DoesNotContain(
            BackFillerRabbitMqTopology.ProviderBackbones.Select(BackFillerRabbitMqTopology.ComposeProviderEntity),
            static name => name.StartsWith("grabbers.", StringComparison.Ordinal));
    }
}
