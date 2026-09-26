namespace VectorNNTP.BackFiller.Nntp;

/// <summary>Resolves a provider definition for a consuming backbone.</summary>
public interface IBackFillerProviderCatalog
{
    /// <summary>Gets the configured providers.</summary>
    IReadOnlyList<BackFillerProviderDefinition> Providers { get; }

    /// <summary>
    /// Attempts to resolve the provider for <paramref name="backbone"/>.
    /// </summary>
    /// <param name="backbone">Work-item backbone (ordinal-ignore-case match).</param>
    /// <param name="provider">Resolved definition when found.</param>
    /// <returns><see langword="true"/> when a provider is configured.</returns>
    bool TryGetProvider(string backbone, out BackFillerProviderDefinition provider);
}

/// <summary>Fixed in-memory catalog used by tests. Production uses the MySQL-backed live catalog.</summary>
public sealed class StaticBackFillerProviderCatalog : IBackFillerProviderCatalog
{
    private readonly IReadOnlyList<BackFillerProviderDefinition> _providers;

    /// <summary>Creates an empty catalog.</summary>
    public StaticBackFillerProviderCatalog()
        : this([])
    {
    }

    /// <summary>Creates a catalog from <paramref name="providers"/>.</summary>
    /// <param name="providers">Explicit provider definitions. Must not contain secrets in logs.</param>
    public StaticBackFillerProviderCatalog(IReadOnlyList<BackFillerProviderDefinition> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers;
    }

    /// <inheritdoc />
    public IReadOnlyList<BackFillerProviderDefinition> Providers => _providers;

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
