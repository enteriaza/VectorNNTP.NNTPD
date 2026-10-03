using System.Security.Cryptography.X509Certificates;

namespace VectorNNTP.Common.Acme
{
    /// <summary>
    /// Owns server certificate lifecycle: evaluate, issue, renew, and expose validated PFX material.
    /// </summary>
    public sealed class CertificateManager
    {
        private readonly string _fqdn;
        private readonly string _stateDir;
        private readonly string _acmeDirectoryUrl;
        private readonly IReadOnlyList<string> _domains;
        private readonly CertificateStore _store;
        private readonly ICertificateIssuer _issuer;
        private readonly AcmeTransactionJournal _journal;
        private readonly string _pfxPassword;
        private readonly TimeSpan _renewalThreshold;
        private readonly ILogger<CertificateManager> _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private CertificateMaterial? _current;
        private int _generation;

        /// <summary>Initializes a new instance of the <see cref="CertificateManager"/> class.</summary>
        /// <param name="fqdn">Certificate FQDN that owns this manager.</param>
        /// <param name="stateDir">Shared ACME state root.</param>
        /// <param name="acmeDirectoryUrl">ACME directory URL recorded in the journal.</param>
        /// <param name="store">FQDN-scoped live certificate store.</param>
        /// <param name="issuer">Certificate issuer.</param>
        /// <param name="pfxPassword">PKCS#12 password.</param>
        /// <param name="renewalThreshold">Renewal threshold.</param>
        /// <param name="logger">Logger.</param>
        /// <param name="includeNewsHostname">Whether to include <see cref="CertificateIdentities.NewsHostname"/>.</param>
        /// <param name="journal">
        /// Optional shared transaction journal. When omitted, this manager creates one
        /// for <paramref name="fqdn"/>. Production wiring shares the instance with
        /// <see cref="Dns01Solver"/> and <see cref="AcmeIssuer"/>.
        /// </param>
        public CertificateManager(
            string fqdn,
            string stateDir,
            string acmeDirectoryUrl,
            CertificateStore store,
            ICertificateIssuer issuer,
            string pfxPassword,
            TimeSpan renewalThreshold,
            ILogger<CertificateManager> logger,
            bool includeNewsHostname = true,
            AcmeTransactionJournal? journal = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
            ArgumentException.ThrowIfNullOrWhiteSpace(acmeDirectoryUrl);
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(issuer);
            ArgumentNullException.ThrowIfNull(pfxPassword);
            ArgumentNullException.ThrowIfNull(logger);
            if (renewalThreshold <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(renewalThreshold));
            }

            _fqdn = CertificateIdentities.NormalizeFqdn(fqdn);
            _stateDir = stateDir;
            _acmeDirectoryUrl = acmeDirectoryUrl.Trim();
            _domains = CertificateIdentities.ForFqdn(_fqdn, includeNewsHostname);
            _store = store;
            _issuer = issuer;
            _journal = journal ?? new AcmeTransactionJournal(_stateDir, _fqdn);
            _pfxPassword = pfxPassword;
            _renewalThreshold = renewalThreshold;
            _logger = logger;
        }

        /// <summary>Gets the required certificate DNS identities.</summary>
        public IReadOnlyList<string> Domains => _domains;

        /// <summary>Gets the monotonic generation counter after successful persist.</summary>
        public int Generation => _generation;

        /// <summary>Gets the current in-memory material, if any.</summary>
        public CertificateMaterial? CurrentMaterial => _current;

        /// <summary>Assesses on-disk certificate without contacting ACME.</summary>
        public CertificateStatus EvaluateExisting(DateTimeOffset? now = null)
        {
            try
            {
                var loaded = _store.Load();
                if (loaded is null)
                {
                    return new CertificateStatus(false, true, null, "absent");
                }

                return CertificateValidator.ValidatePfx(
                    loaded.PfxBytes,
                    _pfxPassword,
                    _domains,
                    _renewalThreshold,
                    now);
            }
            catch (AcmeStorageException ex)
            {
                return new CertificateStatus(false, true, null, ex.Category);
            }
            catch (AcmeCertificateException ex)
            {
                return new CertificateStatus(false, true, null, ex.Category);
            }
        }

        /// <summary>Reuses a usable certificate or issues a new one (startup path).</summary>
        public async Task<CertificateMaterial> EnsureCertificateAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var issuanceLock = await AcmeIssuanceLock
                    .AcquireAsync(_stateDir, _fqdn, cancellationToken)
                    .ConfigureAwait(false);
                var status = EvaluateExisting();
                if (status is { Usable: true, Material: not null, DueForRenewal: false })
                {
                    _current = status.Material;
                    AcmeLogMessages.ReusingExistingCertificate(_logger, status.Material.NotAfter);
                    return status.Material;
                }

                if (status is { Usable: true, DueForRenewal: true })
                {
                    AcmeLogMessages.ExistingCertificateDueForRenewal(_logger);
                }
                else
                {
                    AcmeLogMessages.NoUsableCertificate(_logger, status.Reason);
                }

                return await IssueAndPersistAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Renews when absent, invalid, or past the renewal threshold.
        /// Returns <see langword="true"/> when a new certificate was persisted.
        /// </summary>
        public async Task<bool> RenewIfDueAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var issuanceLock = await AcmeIssuanceLock
                    .AcquireAsync(_stateDir, _fqdn, cancellationToken)
                    .ConfigureAwait(false);
                var status = EvaluateExisting();
                if (status is { Usable: true, Material: not null, DueForRenewal: false })
                {
                    _current = status.Material;
                    return false;
                }

                var prior = status.Usable ? status.Material : null;
                try
                {
                    _ = await IssueAndPersistAsync(cancellationToken).ConfigureAwait(false);
                    return true;
                }
                catch (Exception ex) when (prior is not null && ex is not OperationCanceledException)
                {
                    _current = prior;
                    AcmeLogMessages.RenewalFailedPreservingExisting(_logger, AcmeFailureSanitizer.Sanitize(ex));
                    return false;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Loads an <see cref="X509Certificate2"/> with private key from the current live PFX.
        /// Caller owns disposal. The certificate remains usable after the file path is closed.
        /// </summary>
        public X509Certificate2 CreateTlsCertificate()
        {
            var material = _current ?? EvaluateExisting().Material
                ?? throw new AcmeCertificateException("no_certificate", "live material missing");
            return PfxCrypto.LoadCertificate(material.PfxBytes, _pfxPassword);
        }

        private async Task<CertificateMaterial> IssueAndPersistAsync(CancellationToken cancellationToken)
        {
            var transactionId = _journal.BeginTransaction(_domains, _acmeDirectoryUrl, DateTimeOffset.UtcNow);
            try
            {
                var material = await _issuer.IssueAsync(_domains, cancellationToken).ConfigureAwait(false);
                var status = CertificateValidator.ValidatePfx(
                    material.PfxBytes,
                    _pfxPassword,
                    _domains,
                    _renewalThreshold);
                if (!status.Usable || status.Material is null)
                {
                    throw new AcmeCertificateException("invalid_certificate", status.Reason);
                }

                _journal.RecordCertificateIssued(status.Material);
                var persisted = _store.Save(status.Material);
                _journal.RecordCertificatePersisted(persisted.GenerationId, status.Material);
                _journal.RecordCertificatePromoted(persisted.GenerationId);
                _journal.CompleteSuccess(
                    transactionId,
                    status.Material,
                    persisted.GenerationId,
                    _pfxPassword,
                    DateTimeOffset.UtcNow);
                _current = status.Material;
                _generation++;
                AcmeLogMessages.ServerCertificateReady(_logger, _generation, status.Material.NotAfter);
                return status.Material;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var category = ex is AcmeException acme ? acme.Category : ex.GetType().Name;
                _journal.CompleteFailure(
                    transactionId,
                    category,
                    AcmeFailureSanitizer.Sanitize(ex),
                    DateTimeOffset.UtcNow);
                throw;
            }
        }
    }
}
