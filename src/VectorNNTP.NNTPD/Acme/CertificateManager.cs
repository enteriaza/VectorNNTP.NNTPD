using System.Security.Cryptography.X509Certificates;

namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// Owns server certificate lifecycle: evaluate, issue, renew, and expose validated PFX material.
/// </summary>
public sealed class CertificateManager
{
    private readonly IReadOnlyList<string> _domains;
    private readonly CertificateStore _store;
    private readonly ICertificateIssuer _issuer;
    private readonly string _pfxPassword;
    private readonly TimeSpan _renewalThreshold;
    private readonly ILogger<CertificateManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CertificateMaterial? _current;
    private int _generation;

    /// <summary>Initializes a new instance of the <see cref="CertificateManager"/> class.</summary>
    public CertificateManager(
        string fqdn,
        CertificateStore store,
        ICertificateIssuer issuer,
        string pfxPassword,
        TimeSpan renewalThreshold,
        ILogger<CertificateManager> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(pfxPassword);
        ArgumentNullException.ThrowIfNull(logger);
        if (renewalThreshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(renewalThreshold));
        }

        _domains = CertificateIdentities.ForFqdn(fqdn);
        _store = store;
        _issuer = issuer;
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
            var status = EvaluateExisting();
            if (status is { Usable: true, Material: not null, DueForRenewal: false })
            {
                _current = status.Material;
                _logger.LogInformation(
                    "Reusing existing server certificate; notAfter={NotAfter:o}",
                    status.Material.NotAfter);
                return status.Material;
            }

            if (status is { Usable: true, DueForRenewal: true })
            {
                _logger.LogInformation("Existing certificate due for renewal; issuing replacement");
            }
            else
            {
                _logger.LogInformation(
                    "No usable server certificate ({Reason}); requesting issuance",
                    status.Reason);
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
                _logger.LogWarning(
                    "Certificate renewal failed ({Failure}); preserving existing certificate",
                    AcmeFailureSanitizer.Sanitize(ex));
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

        _store.Save(status.Material);
        _current = status.Material;
        _generation++;
        _logger.LogInformation(
            "Server certificate ready generation={Generation} notAfter={NotAfter:o}",
            _generation,
            status.Material.NotAfter);
        return status.Material;
    }
}
