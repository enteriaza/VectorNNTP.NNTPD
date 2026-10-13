using VectorNNTP.Common.Networking;

namespace VectorNNTP.Common.Cloudflare
{
    /// <summary>
    /// Authoritative Cloudflare DNS reconciler for the generated FQDN.
    /// </summary>
    /// <remarks>
    /// Startup reconciliation publishes A/AAAA for resolved bind addresses.
    /// Shutdown clean-up removes every DNS record for the exact FQDN (all types).
    /// </remarks>
    internal interface ICloudflareDnsReconciler
    {
        /// <summary>
        /// Makes Cloudflare DNS for <paramref name="fqdn"/> match <paramref name="desired"/> exactly,
        /// then verifies the remote state. Success is reported only after verification of both A and AAAA.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="fqdn">Generated server FQDN.</param>
        /// <param name="desired">Desired eligible bind addresses (including intentional private addresses).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task that completes when reconciliation and verification succeed.</returns>
        /// <exception cref="CloudflareDnsException">
        /// Thrown when API operations or verification fail. Partial mutations are not treated as success.
        /// Uncertain mutation outcomes are marked via <see cref="CloudflareDnsException.IsOutcomeUncertain"/>.
        /// </exception>
        /// <exception cref="InvalidOperationException">Thrown when <paramref name="desired"/> is empty.</exception>
        /// <remarks>
        /// Cloudflare multi-record updates are not atomic. Implementations must re-read remote state on retry
        /// and must not claim success without verification.
        /// </remarks>
        Task ReconcileAsync(
            string zoneId,
            string fqdn,
            ResolvedBindAddresses desired,
            CancellationToken cancellationToken);

        /// <summary>
        /// Gets whether the most recent <see cref="ReconcileAsync"/> on this instance deleted, or began deleting,
        /// a record that was already present.
        /// </summary>
        /// <remarks>
        /// When this is <see langword="true"/>, compensating cleanup must not delete the desired records created
        /// by that attempt: the previous records may already be gone. Cloudflare provides no transaction to undo that.
        /// </remarks>
        bool DeletedPreExistingDuringLastReconcile { get; }

        /// <summary>
        /// Deletes every DNS record for the exact <paramref name="fqdn"/> (all types), then verifies none remain.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="fqdn">Generated server FQDN (exact ownership boundary).</param>
        /// <param name="cancellationToken">Cancellation token (cooperates with graceful shutdown).</param>
        /// <param name="operationTimeout">
        /// Optional wall-clock budget for this clean-up. When null, <c>CloudFlareOperationTimeout</c> is used.
        /// Failed-start clean-up passes a shorter dedicated budget.
        /// </param>
        /// <returns>A task that completes only after verification that no records remain for the FQDN.</returns>
        /// <exception cref="CloudflareDnsException">
        /// Thrown when listing, deletion, or verification fails. Partial deletion is never reported as success.
        /// </exception>
        /// <remarks>
        /// Parent, child/subdomain, and other hostnames are never deleted. Idempotent when the FQDN is already absent.
        /// Serialized with <see cref="ReconcileAsync"/> on the same instance so startup cannot recreate after clean-up begins.
        /// </remarks>
        Task RemoveAllRecordsForFqdnAsync(
            string zoneId,
            string fqdn,
            CancellationToken cancellationToken,
            TimeSpan? operationTimeout = null);

        /// <summary>
        /// Lists record ids for the exact <paramref name="fqdn"/> without mutating them.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="fqdn">Exact DNS name.</param>
        /// <param name="cancellationToken">Cancels the list.</param>
        /// <returns>Ids of records whose name matches <paramref name="fqdn"/> exactly. Records without an id are omitted.</returns>
        Task<IReadOnlyList<string>> ListRecordIdsAsync(
            string zoneId,
            string fqdn,
            CancellationToken cancellationToken);

        /// <summary>
        /// Deletes only the given record ids when they still exist for the exact <paramref name="fqdn"/>.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="fqdn">Exact DNS name. Ids that belong to another name are not deleted.</param>
        /// <param name="recordIds">Ids created by this process. Other ids on the name are left in place.</param>
        /// <param name="cancellationToken">Cancels the delete.</param>
        /// <param name="operationTimeout">Optional wall-clock budget. When null, <c>CloudFlareOperationTimeout</c> is used.</param>
        /// <returns>A task that completes when every requested id is absent. Already-absent ids are success.</returns>
        /// <exception cref="CloudflareDnsException">Thrown when a requested id is still present after the delete attempt.</exception>
        Task DeleteRecordIdsAsync(
            string zoneId,
            string fqdn,
            IReadOnlyCollection<string> recordIds,
            CancellationToken cancellationToken,
            TimeSpan? operationTimeout = null);
    }
}
