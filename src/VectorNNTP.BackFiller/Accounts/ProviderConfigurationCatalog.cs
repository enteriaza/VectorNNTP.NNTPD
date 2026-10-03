using VectorNNTP.BackFiller.Nntp;

namespace VectorNNTP.BackFiller.Accounts
{
    /// <summary>
    /// Atomically published provider snapshot. Readers see the previous complete set or the new complete set.
    /// </summary>
    internal sealed class ProviderConfigurationCatalog : IBackFillerProviderCatalog
    {
        /// <summary>
        /// Latest published list. <see cref="Publish"/> replaces the reference.
        /// The initial value is an empty array. Readers are not given a copy.
        /// </summary>
        private volatile IReadOnlyList<BackFillerProviderDefinition> _providers = [];

        /// <summary>Gets the most recently published provider list.</summary>
        /// <value>The list reference written by the last <see cref="Publish"/>, or an empty array before the first publication.</value>
        /// <remarks>The read observes one complete list. It does not merge a publication that starts after the read.</remarks>
        public IReadOnlyList<BackFillerProviderDefinition> Providers => _providers;

        /// <summary>Publishes a complete snapshot. The list is treated as immutable after publication.</summary>
        /// <param name="providers">Complete provider list. Not copied.</param>
        /// <exception cref="ArgumentNullException"><paramref name="providers"/> is null.</exception>
        /// <remarks>
        /// Replaces <see cref="_providers"/> with a volatile writer. Callers must not mutate
        /// <paramref name="providers"/> after this returns; later readers observe that same instance.
        /// </remarks>
        internal void Publish(IReadOnlyList<BackFillerProviderDefinition> providers)
        {
            ArgumentNullException.ThrowIfNull(providers);
            _providers = providers;
        }

        /// <summary>
        /// Resolves <paramref name="backbone"/> against the list reference visible at the start of the call.
        /// </summary>
        /// <param name="backbone">Work-item backbone. Compared ordinal-ignore-case. Null, empty, and white space are rejected.</param>
        /// <param name="provider">Matching definition when this method returns <see langword="true"/>; otherwise <see langword="null"/>.</param>
        /// <returns><see langword="true"/> when the current list contains a matching backbone.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="backbone"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="backbone"/> is empty or white space.</exception>
        public bool TryGetProvider(string backbone, out BackFillerProviderDefinition provider)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(backbone);
            foreach (var candidate in _providers)
            {
                if (!string.Equals(candidate.Backbone, backbone, StringComparison.OrdinalIgnoreCase)) continue;
                provider = candidate;
                return true;
            }

            provider = null!;
            return false;
        }
    }
}
