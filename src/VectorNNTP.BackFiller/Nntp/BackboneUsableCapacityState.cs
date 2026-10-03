using System.Collections.Immutable;

namespace VectorNNTP.BackFiller.Nntp
{
    /// <summary>
    /// Read-only view of per-backbone usable NNTP capacity used to gate Article Work consume.
    /// </summary>
    internal interface IBackboneUsableCapacityProvider
    {
        /// <summary>Raised after a snapshot is published.</summary>
        event EventHandler? SnapshotPublished;

        /// <summary>
        /// Returns whether the published count for <paramref name="backbone"/> is positive.
        /// </summary>
        /// <param name="backbone">Backbone name compared with the snapshot's ordinal-ignore-case key comparer.</param>
        /// <returns><see langword="true"/> when the snapshot contains a count greater than zero.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="backbone"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="backbone"/> is empty or white space.</exception>
        bool HasUsableCapacityForBackbone(string backbone);

        /// <summary>Returns the published count for <paramref name="backbone"/>.</summary>
        /// <param name="backbone">Backbone name compared with the snapshot's ordinal-ignore-case key comparer.</param>
        /// <returns>The published count, or zero when <paramref name="backbone"/> is absent.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="backbone"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="backbone"/> is empty or white space.</exception>
        int GetUsableCapacityForBackbone(string backbone);
    }

    /// <summary>Publishes authoritative backbone usable-capacity snapshots.</summary>
    internal interface IBackboneUsableCapacityStateWriter
    {
        /// <summary>
        /// Replaces the current snapshot. Non-positive counts and blank names are excluded,
        /// matching the old control-plane publisher.
        /// </summary>
        /// <param name="capacityByBackbone">
        /// Candidate backbone counts. Blank names and counts less than or equal to zero are omitted.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="capacityByBackbone"/> is null.</exception>
        /// <remarks>
        /// <see cref="BackboneUsableCapacityState"/> stores keys with <see cref="StringComparer.OrdinalIgnoreCase"/>
        /// and then raises <see cref="IBackboneUsableCapacityProvider.SnapshotPublished"/>.
        /// </remarks>
        void PublishSnapshot(IReadOnlyDictionary<string, int> capacityByBackbone);
    }

    /// <summary>
    /// Holds the latest published backbone counts.
    /// Usable capacity is a positive count. The provider registry publishes ACTIVE session counts here.
    /// </summary>
    internal sealed class BackboneUsableCapacityState : IBackboneUsableCapacityProvider, IBackboneUsableCapacityStateWriter
    {
        /// <summary>
        /// Latest published counts. Empty until the first <see cref="PublishSnapshot"/>.
        /// Keys use <see cref="StringComparer.OrdinalIgnoreCase"/>.
        /// </summary>
        private ImmutableDictionary<string, int> _capacityByBackbone =
            ImmutableDictionary<string, int>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

        /// <inheritdoc />
        public event EventHandler? SnapshotPublished;

        /// <inheritdoc />
        public bool HasUsableCapacityForBackbone(string backbone)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(backbone);
            return _capacityByBackbone.TryGetValue(backbone, out var usableCount) && usableCount > 0;
        }

        /// <inheritdoc />
        public int GetUsableCapacityForBackbone(string backbone)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(backbone);
            return _capacityByBackbone.TryGetValue(backbone, out var usableCount) ? usableCount : 0;
        }

        /// <summary>
        /// Replaces <see cref="_capacityByBackbone"/> with the filtered counts, then raises <see cref="SnapshotPublished"/>.
        /// </summary>
        /// <param name="capacityByBackbone">
        /// Candidate backbone counts. Blank names and counts less than or equal to zero are omitted.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="capacityByBackbone"/> is null.</exception>
        /// <remarks>
        /// The replacement uses <see cref="StringComparer.OrdinalIgnoreCase"/>, so a later entry replaces an earlier
        /// entry that differs only by case. The event is invoked on the caller after the field is assigned,
        /// with this instance and <see cref="EventArgs.Empty"/>, including when the filtered snapshot is empty.
        /// A subscriber exception propagates after the field has already been replaced.
        /// </remarks>
        public void PublishSnapshot(IReadOnlyDictionary<string, int> capacityByBackbone)
        {
            ArgumentNullException.ThrowIfNull(capacityByBackbone);

            var builder = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (backbone, usableCount) in capacityByBackbone)
            {
                if (string.IsNullOrWhiteSpace(backbone) || usableCount <= 0)
                {
                    continue;
                }

                builder[backbone] = usableCount;
            }

            _capacityByBackbone = builder.ToImmutable();
            SnapshotPublished?.Invoke(this, EventArgs.Empty);
        }
    }
}
