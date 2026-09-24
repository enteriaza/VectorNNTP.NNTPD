using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Core;
using VectorNNTP.NNTPD.Networking;

namespace VectorNNTP.NNTPD.Cloudflare;

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
/// by <see cref="ApplicationServiceManager"/>; reconcile and clean-up share the reconciler gate.
/// </para>
/// </remarks>
public sealed class CloudflareDnsReconciliationService : IApplicationService
{
    /// <summary>Maximum wall-clock budget for best-effort clean-up after a failed or cancelled startup reconcile.</summary>
    public static readonly TimeSpan FailedStartCleanupTimeout = TimeSpan.FromSeconds(15);

    private readonly IOptions<NntpdOptions> _options;
    private readonly IBindAddressResolver _bindAddressResolver;
    private readonly ICloudflareDnsReconciler _reconciler;
    private readonly ILogger<CloudflareDnsReconciliationService> _logger;
    private int _fqdnOwnershipActive;

    /// <summary>
    /// Initializes a new instance of the <see cref="CloudflareDnsReconciliationService"/> class.
    /// </summary>
    public CloudflareDnsReconciliationService(
        IOptions<NntpdOptions> options,
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

    /// <inheritdoc />
    public string Name => "CloudflareDnsReconciliation";

    /// <inheritdoc />
    public Task? Execution => null;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
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
                $"{NntpdOptions.CloudFlareZoneIdConfigurationKey} is required for DNS reconciliation.");
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

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
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
