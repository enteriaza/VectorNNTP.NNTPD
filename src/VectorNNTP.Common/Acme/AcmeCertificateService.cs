using VectorNNTP.Common.Configuration;

namespace VectorNNTP.Common.Acme
{
    /// <summary>
    /// Application service that ensures ACME account/certificate readiness when TLS is enabled,
    /// and periodically renews certificates while running.
    /// </summary>
    /// <remarks>
    /// When <see cref="AcmeCloudflareOptions.IsTlsListenerEnabled"/> is <see langword="false"/>, start is a no-op
    /// and no ACME network calls are made. Shutdown does not contact Let's Encrypt.
    /// After a usable PFX is ensured or renewed, an immutable TLS certificate context is published
    /// for the TLS listener (atomic swap; existing connections are unaffected).
    /// </remarks>
    public sealed class AcmeCertificateService : IAsyncDisposable
    {
        /// <summary>Background renewal check interval (default 6 hours).</summary>
        private static readonly TimeSpan RenewalCheckInterval = TimeSpan.FromHours(6);

        /// <summary>Options read at start and when publishing a renewed PFX.</summary>
        private readonly IOptions<AcmeCloudflareOptions> _options;

        /// <summary>Creates the certificate manager only when TLS is enabled.</summary>
        private readonly AcmeComponentFactory _factory;

        /// <summary>Receives the PFX after a successful ensure or renewal.</summary>
        private readonly IAcmeCertificatePublisher _certificatePublisher;

        /// <summary>Set after the startup PFX is published. Not set again on later renewals.</summary>
        private readonly IAcmeCertificateReadiness _readiness;

        /// <summary>Logger for idle, provisioning, and renewal events.</summary>
        private readonly ILogger<AcmeCertificateService> _logger;

        /// <summary>Cancels <see cref="RunRenewalLoopAsync"/>. Disposed by <see cref="DisposeAsync"/>.</summary>
        private readonly CancellationTokenSource _runCts = new();

        /// <summary>Manager created during a TLS-enabled start. <see langword="null"/> when TLS is disabled or start has not created it.</summary>
        private CertificateManager? _manager;

        /// <summary>Renewal loop task. <see langword="null"/> until a TLS-enabled start publishes a certificate.</summary>
        private Task? _execution;

        /// <summary>Non-zero after the first <see cref="StartAsync"/> call. Later calls return immediately.</summary>
        private int _started;

        /// <summary>Non-zero after <see cref="DisposeAsync"/> has begun.</summary>
        private int _disposed;

        /// <summary>Stores the options, factory, publisher, readiness gate, and logger. Does not contact the CA.</summary>
        /// <param name="options">ACME options, including whether the TLS listener is enabled.</param>
        /// <param name="factory">Builds the certificate manager when TLS is enabled.</param>
        /// <param name="certificatePublisher">Publishes PFX bytes for the TLS listener.</param>
        /// <param name="readiness">Gate set after the first successful publish.</param>
        /// <param name="logger">Logger for this service.</param>
        internal AcmeCertificateService(
            IOptions<AcmeCloudflareOptions> options,
            AcmeComponentFactory factory,
            IAcmeCertificatePublisher certificatePublisher,
            IAcmeCertificateReadiness readiness,
            ILogger<AcmeCertificateService> logger)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(factory);
            ArgumentNullException.ThrowIfNull(certificatePublisher);
            ArgumentNullException.ThrowIfNull(readiness);
            ArgumentNullException.ThrowIfNull(logger);
            _options = options;
            _factory = factory;
            _certificatePublisher = certificatePublisher;
            _readiness = readiness;
            _logger = logger;
        }

        /// <summary>Stable service name <c>AcmeCertificate</c> read by the host application-service adapter.</summary>
        internal static string Name => "AcmeCertificate";

        /// <summary>
        /// Renewal-loop task after a successful TLS-enabled start.
        /// <see langword="null"/> when TLS is disabled, start has not finished, or start failed before the loop was assigned.
        /// </summary>
        internal Task? Execution => _execution;

        /// <summary>Gets whether the service initialized ACME components (for tests).</summary>
        internal bool AcmeInitialized => _manager is not null;

        /// <summary>
        /// When TLS is disabled, logs and returns without creating ACME state.
        /// Otherwise ensures a certificate, publishes the PFX, marks readiness, and starts the renewal loop.
        /// A second call returns without doing that work again.
        /// </summary>
        /// <param name="cancellationToken">Cancels certificate ensure. Cancellation is propagated. Other failures are logged and rethrown.</param>
        /// <returns>A task that completes when startup has finished or failed.</returns>
        /// <exception cref="AcmeConfigurationException">Thrown when TLS is enabled but the factory returns no manager.</exception>
        internal async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return;
            }

            var options = _options.Value;
            if (!options.IsTlsListenerEnabled)
            {
                AcmeLogMessages.TlsDisabledIdle(_logger);
                return;
            }

            _manager = _factory.GetOrCreateManager()
                ?? throw new AcmeConfigurationException(
                    "manager_missing",
                    "TLS is enabled but CertificateManager could not be created");

            AcmeLogMessages.EnsuringCertificate(_logger, options.AcmeDirectoryUrl);

            try
            {
                var material = await _manager.EnsureCertificateAsync(cancellationToken).ConfigureAwait(false);
                _certificatePublisher.PublishFromPfx(material.PfxBytes, options.AcmeCertificatePassword);
                _readiness.MarkReady();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AcmeLogMessages.ProvisioningFailed(_logger, AcmeFailureSanitizer.Sanitize(ex));
                throw;
            }

            _execution = RunRenewalLoopAsync(_runCts.Token);
        }

        /// <summary>
        /// Cancels the renewal loop and waits for it to finish.
        /// Does not contact the certificate authority. A missing loop task returns immediately.
        /// <see cref="OperationCanceledException"/> from the wait is ignored; other wait failures are logged.
        /// </summary>
        /// <param name="cancellationToken">Bounds the wait for the renewal loop. It does not cancel the loop; <see cref="_runCts"/> does that.</param>
        /// <returns>A task that completes when the wait finishes or there is no loop.</returns>
        internal async Task StopAsync(CancellationToken cancellationToken)
        {
            await _runCts.CancelAsync().ConfigureAwait(false);
            if (_execution is null)
            {
                return;
            }

            try
            {
                await _execution.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on cancel.
            }
            catch (Exception ex)
            {
                AcmeLogMessages.RenewalLoopStopError(_logger, ex);
            }
        }

        /// <summary>
        /// Cancels and disposes the renewal-loop token source. A second call does nothing.
        /// Does not wait for the loop and does not contact the certificate authority.
        /// </summary>
        /// <returns>A task that completes after the token source is disposed.</returns>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            try
            {
                await _runCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Already disposed.
            }

            _runCts.Dispose();
        }

        /// <summary>
        /// After <see cref="RenewalCheckInterval"/>, renews when due and publishes the new PFX.
        /// Failures other than cancellation of <paramref name="cancellationToken"/> are logged and the loop continues.
        /// </summary>
        /// <param name="cancellationToken">Stops the loop. This is <see cref="_runCts"/>, not the start token.</param>
        /// <returns>A task that completes when the loop is cancelled or <see cref="_manager"/> is null.</returns>
        private async Task RunRenewalLoopAsync(CancellationToken cancellationToken)
        {
            if (_manager is null)
            {
                return;
            }

            var password = _options.Value.AcmeCertificatePassword;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(RenewalCheckInterval, cancellationToken).ConfigureAwait(false);
                    var renewed = await _manager.RenewIfDueAsync(cancellationToken).ConfigureAwait(false);
                    if (renewed)
                    {
                        var material = _manager.CurrentMaterial
                            ?? throw new AcmeCertificateException("no_certificate", "renewed without material");
                        _certificatePublisher.PublishFromPfx(material.PfxBytes, password);
                        AcmeLogMessages.RenewalCompleted(_logger);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AcmeLogMessages.RenewalCheckFailed(_logger, AcmeFailureSanitizer.Sanitize(ex));
                }
            }
        }
    }
}
