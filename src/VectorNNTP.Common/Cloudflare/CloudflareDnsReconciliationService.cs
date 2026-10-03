using VectorNNTP.Common.Networking;
using VectorNNTP.Common.Configuration;

namespace VectorNNTP.Common.Cloudflare
{
    /// <summary>
    /// Application service that reconciles Cloudflare DNS on startup and removes the exact FQDN on shutdown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This host is authoritative for the entire configured <c>{Fqdn}</c> in the configured zone.
    /// <see cref="StartAsync"/> publishes A/AAAA for resolved bind addresses and fails closed on verify failure.
    /// <see cref="StopAsync"/> deletes every DNS record for the exact FQDN (all types), then verifies none remain.
    /// </para>
    /// <para>
    /// Registered first among application services so reverse-order stop runs clean-up after other services
    /// (including future listeners) have stopped. Startup failure after reconcile work begins attempts clean-up
    /// so partial A/AAAA publications are not left without a recovery path. Concurrent start/stop are rejected
    /// by the application host; reconcile and clean-up share the reconciler gate.
    /// </para>
    /// </remarks>
    internal sealed class CloudflareDnsReconciliationService
    {
        /// <summary>Maximum wall-clock budget for best-effort clean-up after a failed or cancelled startup reconcile.</summary>
        public static readonly TimeSpan FailedStartCleanupTimeout = TimeSpan.FromSeconds(15);

        /// <summary>FQDN, zone id, and bind addresses used for reconcile and cleanup.</summary>
        private readonly IOptions<AcmeCloudflareOptions> _options;

        /// <summary>Resolves the bind addresses published as A and AAAA records.</summary>
        private readonly IBindAddressResolver _bindAddressResolver;

        /// <summary>Performs the Cloudflare read/mutate/verify work.</summary>
        private readonly ICloudflareDnsReconciler _reconciler;

        /// <summary>Reconciliation-service diagnostics.</summary>
        private readonly ILogger<CloudflareDnsReconciliationService> _logger;

        /// <summary>
        /// Non-zero after a reconcile in this process returns successfully.
        /// <see cref="StopAsync"/> exchanges it back to zero and skips cleanup when it was already zero.
        /// </summary>
        private int _fqdnOwnershipActive;

        /// <summary>
        /// Initializes a new instance of the <see cref="CloudflareDnsReconciliationService"/> class.
        /// </summary>
        /// <param name="options">ACME and Cloudflare options, including the generated FQDN and zone id.</param>
        /// <param name="bindAddressResolver">Resolves addresses published as A and AAAA records.</param>
        /// <param name="reconciler">Cloudflare DNS reconciler.</param>
        /// <param name="logger">Reconciliation-service logger.</param>
        public CloudflareDnsReconciliationService(
            IOptions<AcmeCloudflareOptions> options,
            IBindAddressResolver bindAddressResolver,
            ICloudflareDnsReconciler reconciler,
            ILogger<CloudflareDnsReconciliationService> logger)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(bindAddressResolver);
            ArgumentNullException.ThrowIfNull(reconciler);
            ArgumentNullException.ThrowIfNull(logger);

            _options = options;
            _bindAddressResolver = bindAddressResolver;
            _reconciler = reconciler;
            _logger = logger;
        }

        /// <summary>Gets the stable service name recorded by the application service manager.</summary>
        internal string Name => "CloudflareDnsReconciliation";

        /// <summary>Gets null. This service has no background execution after <see cref="StartAsync"/> returns.</summary>
        internal Task? Execution => null;

        /// <summary>
        /// Publishes A and AAAA records for the resolved bind addresses and marks FQDN ownership active only after verification.
        /// </summary>
        /// <param name="cancellationToken">Cancels reconcile. Cancellation after reconcile begins attempts a bounded cleanup, then propagates.</param>
        /// <returns>A task that completes when Cloudflare has verified the desired address set.</returns>
        /// <exception cref="InvalidOperationException">
        /// The generated FQDN or zone id is missing, or bind-address resolution produced no eligible address.
        /// </exception>
        /// <remarks>
        /// A non-cancellation failure after reconcile begins also attempts cleanup of the exact FQDN.
        /// Cleanup failure is logged and does not replace the original startup exception.
        /// </remarks>
        internal async Task StartAsync(CancellationToken cancellationToken)
        {
            var options = _options.Value;
            var fqdn = options.Fqdn;
            if (string.IsNullOrWhiteSpace(fqdn))
            {
                throw new InvalidOperationException(
                    "Generated FQDN is empty; ServerId and DnsSuffix must be validated before DNS reconciliation.");
            }

            if (string.IsNullOrWhiteSpace(options.CloudFlareZoneId))
            {
                throw new InvalidOperationException(
                    $"{AcmeCloudflareOptions.CloudFlareZoneIdConfigurationKey} is required for DNS reconciliation.");
            }

            CloudflareLogMessages.StartingReconciliation(_logger, fqdn, options.CloudFlareZoneId);

            var resolved = _bindAddressResolver.Resolve(options);
            if (!resolved.HasAny)
            {
                throw new InvalidOperationException(
                    "No eligible IP addresses were obtained from BindAddress. " +
                    "Wildcard binding requires at least one non-loopback, non-link-local, non-multicast " +
                    "unicast address on a local interface; explicit entries must be eligible for DNS publication. " +
                    "Startup cannot continue with an empty address set.");
            }

            var reconcileBegun = false;
            try
            {
                reconcileBegun = true;
                await _reconciler
                    .ReconcileAsync(options.CloudFlareZoneId, fqdn, resolved, cancellationToken)
                    .ConfigureAwait(false);

                Volatile.Write(ref _fqdnOwnershipActive, 1);

                CloudflareLogMessages.ReconciliationCompleted(_logger, fqdn, resolved.All.Count);
            }
            catch (Exception ex) when (reconcileBegun && ex is not OperationCanceledException)
            {
                // Partial reconcile may have published records; remove the entire FQDN before failing startup.
                await TryCleanupAfterFailedStartAsync(options.CloudFlareZoneId, fqdn, ex).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (reconcileBegun)
            {
                await TryCleanupAfterFailedStartAsync(options.CloudFlareZoneId, fqdn, cancellationException: true)
                    .ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Removes every DNS record for the exact FQDN when this process completed a successful reconcile.
        /// </summary>
        /// <param name="cancellationToken">Passed to cleanup. Cooperative with the caller's shutdown budget.</param>
        /// <returns>A task that completes when cleanup is skipped or Cloudflare verifies the name is empty.</returns>
        /// <exception cref="InvalidOperationException">
        /// Ownership was active but the FQDN or zone id is now missing.
        /// </exception>
        /// <remarks>
        /// When ownership is inactive, cleanup is skipped and this method returns without calling Cloudflare.
        /// </remarks>
        internal async Task StopAsync(CancellationToken cancellationToken)
        {
            // Runs during normal shutdown and startup rollback while lifecycle may already be Stopping.
            // Do not skip clean-up merely because ownership tracking or lifecycle state changed.
            if (Interlocked.Exchange(ref _fqdnOwnershipActive, 0) == 0)
            {
                CloudflareLogMessages.CleanupSkippedOwnershipInactive(_logger);
                return;
            }

            var options = _options.Value;
            var fqdn = options.Fqdn;
            if (string.IsNullOrWhiteSpace(fqdn) || string.IsNullOrWhiteSpace(options.CloudFlareZoneId))
            {
                throw new InvalidOperationException(
                    "Cannot clean up Cloudflare DNS: FQDN or CloudFlareZoneId is missing after ownership was active.");
            }

            CloudflareLogMessages.StoppingReconciliation(_logger, fqdn);

            await _reconciler
                .RemoveAllRecordsForFqdnAsync(options.CloudFlareZoneId, fqdn, cancellationToken)
                .ConfigureAwait(false);

            CloudflareLogMessages.CleanupCompleted(_logger, fqdn);
        }

        /// <summary>
        /// Best-effort exact-FQDN removal after a failed or canceled startup reconcile, bounded by
        /// <see cref="FailedStartCleanupTimeout"/>.
        /// </summary>
        /// <param name="zoneId">Cloudflare zone id.</param>
        /// <param name="fqdn">Exact FQDN to remove.</param>
        /// <param name="original">Startup failure logged with the cleanup attempt. Null when startup was canceled.</param>
        /// <param name="cancellationException"><see langword="true"/> when startup failed because it was canceled.</param>
        /// <remarks>
        /// Success clears <see cref="_fqdnOwnershipActive"/>. Timeout, cancellation, and other cleanup failures are logged
        /// and swallowed so the original startup exception remains the one propagated by <see cref="StartAsync"/>.
        /// </remarks>
        private async Task TryCleanupAfterFailedStartAsync(
            string zoneId,
            string fqdn,
            Exception? original = null,
            bool cancellationException = false)
        {
            try
            {
                CloudflareLogMessages.PostFailureCleanupAttempt(
                    _logger,
                    original,
                    fqdn,
                    cancellationException);

                // Best-effort clean-up after failed/cancelled reconcile: dedicated 15s budget (not unbounded).
                using var cleanupCts = new CancellationTokenSource(FailedStartCleanupTimeout);
                await _reconciler
                    .RemoveAllRecordsForFqdnAsync(
                        zoneId,
                        fqdn,
                        cleanupCts.Token,
                        operationTimeout: FailedStartCleanupTimeout)
                    .ConfigureAwait(false);

                Volatile.Write(ref _fqdnOwnershipActive, 0);
                CloudflareLogMessages.PostFailureCleanupVerified(_logger, fqdn);
            }
            catch (OperationCanceledException cleanupEx)
            {
                CloudflareLogMessages.PostFailureCleanupTimedOut(
                    _logger,
                    cleanupEx,
                    fqdn,
                    FailedStartCleanupTimeout);
                // Preserve the original startup failure; do not replace it with clean-up failure.
            }
            catch (Exception cleanupEx)
            {
                CloudflareLogMessages.PostFailureCleanupFailed(_logger, cleanupEx, fqdn);
                // Preserve the original startup failure; do not replace it with clean-up failure.
            }
        }
    }
}
