using VectorNNTP.BackFiller.Nntp;

namespace VectorNNTP.BackFiller.Accounts;

/// <summary>
/// Atomically published provider snapshot. Readers see the previous complete set or the new complete set.
/// </summary>
public sealed class ProviderConfigurationCatalog : IBackFillerProviderCatalog
{
    private volatile IReadOnlyList<BackFillerProviderDefinition> _providers = [];

    /// <inheritdoc />
    public IReadOnlyList<BackFillerProviderDefinition> Providers => _providers;

    /// <summary>Publishes a complete snapshot. The list is treated as immutable after publication.</summary>
    public void Publish(IReadOnlyList<BackFillerProviderDefinition> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers;
    }

    /// <inheritdoc />
    public bool TryGetProvider(string backbone, out BackFillerProviderDefinition provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backbone);
        foreach (var candidate in _providers)
        {
            if (string.Equals(candidate.Backbone, backbone, StringComparison.OrdinalIgnoreCase))
            {
                provider = candidate;
                return true;
            }
        }

        provider = null!;
        return false;
    }
}
