using VectorNNTP.NNTPD.Networking;

namespace VectorNNTP.NNTPD.Cloudflare;

/// <summary>
/// Authoritative Cloudflare DNS reconciler for the generated FQDN.
/// </summary>
/// <remarks>
/// Startup reconciliation publishes A/AAAA for resolved bind addresses.
/// Shutdown cleanup removes every DNS record for the exact FQDN (all types).
/// </remarks>
public interface ICloudflareDnsReconciler
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
    /// Deletes every DNS record for the exact <paramref name="fqdn"/> (all types), then verifies none remain.
    /// </summary>
    /// <param name="zoneId">Cloudflare zone id.</param>
    /// <param name="fqdn">Generated server FQDN (exact ownership boundary).</param>
    /// <param name="cancellationToken">Cancellation token (cooperates with graceful shutdown).</param>
    /// <returns>A task that completes only after verification that no records remain for the FQDN.</returns>
    /// <exception cref="CloudflareDnsException">
    /// Thrown when listing, deletion, or verification fails. Partial deletion is never reported as success.
    /// </exception>
    /// <remarks>
    /// Parent, child/subdomain, and other hostnames are never deleted. Idempotent when the FQDN is already absent.
    /// Serialized with <see cref="ReconcileAsync"/> on the same instance so startup cannot recreate after cleanup begins.
    /// </remarks>
    Task RemoveAllRecordsForFqdnAsync(
        string zoneId,
        string fqdn,
        CancellationToken cancellationToken);
}
