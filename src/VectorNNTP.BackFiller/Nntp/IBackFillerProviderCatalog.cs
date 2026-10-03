namespace VectorNNTP.BackFiller.Nntp;

/// <summary>Resolves a provider definition for a consuming backbone.</summary>
internal interface IBackFillerProviderCatalog
{
    /// <summary>Gets the provider definitions this catalog currently exposes.</summary>
    IReadOnlyList<BackFillerProviderDefinition> Providers { get; }

    /// <summary>
    /// Attempts to resolve the provider for <paramref name="backbone"/>.
    /// </summary>
    /// <param name="backbone">Work-item backbone compared ordinal-ignore-case. The first match wins.</param>
    /// <param name="provider">
    /// The matching definition when the method returns <see langword="true"/>; otherwise <see langword="null"/>.
    /// </param>
    /// <returns><see langword="true"/> when a provider is configured for <paramref name="backbone"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="backbone"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="backbone"/> is empty or white space.</exception>
    bool TryGetProvider(string backbone, out BackFillerProviderDefinition provider);
}

/// <summary>
/// Fixed in-memory catalog. Production DI uses the MySQL-backed catalog.
/// This type holds an explicit list, including the empty fallback when no catalog is supplied.
/// </summary>
internal sealed class StaticBackFillerProviderCatalog : IBackFillerProviderCatalog
{
    /// <summary>Definitions passed to the constructor. The list is not copied.</summary>
    private readonly IReadOnlyList<BackFillerProviderDefinition> _providers;

    /// <summary>Creates an empty catalog.</summary>
    internal StaticBackFillerProviderCatalog()
        : this([])
    {
    }

    /// <summary>Creates a catalog that stores <paramref name="providers"/> by reference.</summary>
    /// <param name="providers">Provider definitions. The list is not copied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="providers"/> is null.</exception>
    internal StaticBackFillerProviderCatalog(IReadOnlyList<BackFillerProviderDefinition> providers)
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
