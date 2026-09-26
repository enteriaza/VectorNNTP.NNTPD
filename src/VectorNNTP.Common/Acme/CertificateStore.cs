using System.Text.RegularExpressions;

namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// Crash-safe live server certificate store using generation directories and a <c>current</c> pointer.
/// Persists the TLS credential as PKCS#12/PFX (<c>certificate.pfx</c>).
/// </summary>
public sealed class CertificateStore
{
    private static readonly Regex GenerationIdRegex = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _stateDir;
    private readonly string _pfxPassword;
    private readonly IReadOnlyList<string> _requiredDomains;
    private readonly TimeSpan _renewalThreshold;

    /// <summary>Initializes a new instance of the <see cref="CertificateStore"/> class.</summary>
    /// <param name="stateDir">ACME state directory.</param>
    /// <param name="pfxPassword">PKCS#12 password (never logged).</param>
    /// <param name="requiredDomains">Required DNS SANs for round-trip validation.</param>
    /// <param name="renewalThreshold">Renewal threshold used when assessing reloaded material.</param>
    public CertificateStore(
        string stateDir,
        string pfxPassword,
        IReadOnlyList<string> requiredDomains,
        TimeSpan renewalThreshold)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        ArgumentNullException.ThrowIfNull(pfxPassword);
        ArgumentNullException.ThrowIfNull(requiredDomains);
        if (renewalThreshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(renewalThreshold));
        }

        _stateDir = stateDir;
        _pfxPassword = pfxPassword;
        _requiredDomains = requiredDomains;
        _renewalThreshold = renewalThreshold;
        AcmePaths.EnsureStateLayout(stateDir);
        Recover();
    }

    /// <summary>Returns paths for the active generation (after recovery).</summary>
    public CertificatePaths Paths()
    {
        Recover();
        var active = ReadCurrentId();
        if (active is not null && GenerationIsComplete(active))
        {
            return AcmePaths.GenerationCertificatePaths(_stateDir, active);
        }

        throw new AcmeStorageException("no_certificate", "no complete certificate generation");
    }

    /// <summary>
    /// Loads and validates the active PFX, or <see langword="null"/> when no complete generation exists.
    /// </summary>
    public CertificateMaterial? Load()
    {
        Recover();
        var active = ReadCurrentId();
        if (active is null || !GenerationIsComplete(active))
        {
            return null;
        }

        var paths = AcmePaths.GenerationCertificatePaths(_stateDir, active);
        byte[] pfxBytes;
        try
        {
            pfxBytes = File.ReadAllBytes(paths.PfxPath);
        }
        catch (IOException ex)
        {
            throw new AcmeStorageException("certificate_read_failed", ex.GetType().Name);
        }

        if (pfxBytes.Length == 0)
        {
            throw new AcmeStorageException("incomplete_certificate", "empty certificate material");
        }

        var status = CertificateValidator.ValidatePfx(
            pfxBytes,
            _pfxPassword,
            _requiredDomains,
            _renewalThreshold);
        if (!status.Usable || status.Material is null)
        {
            throw new AcmeStorageException("invalid_certificate", status.Reason);
        }

        return status.Material;
    }

    /// <summary>
    /// Persists a new generation after a serialize → reload → validate round trip, then commits <c>current</c>.
    /// </summary>
    public CertificatePaths Save(CertificateMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        AcmePaths.EnsureStateLayout(_stateDir);
        Recover();

        // Pre-validate in-memory bytes before touching disk.
        var preStatus = CertificateValidator.ValidatePfx(
            material.PfxBytes,
            _pfxPassword,
            _requiredDomains,
            _renewalThreshold);
        if (!preStatus.Usable || preStatus.Material is null)
        {
            throw new AcmeCertificateException("invalid_certificate", preStatus.Reason);
        }

        var generationId = Guid.NewGuid().ToString("N");
        var genRoot = AcmePaths.GenerationDir(_stateDir, generationId);
        try
        {
            Directory.CreateDirectory(genRoot);
            var paths = AcmePaths.GenerationCertificatePaths(_stateDir, generationId);
            AtomicFile.WriteBytes(paths.PfxPath, material.PfxBytes);

            // Round-trip: reload from disk and validate before complete/current.
            byte[] reloaded;
            try
            {
                reloaded = File.ReadAllBytes(paths.PfxPath);
            }
            catch (IOException ex)
            {
                throw new AcmeStorageException("certificate_read_failed", ex.GetType().Name);
            }

            var roundTrip = CertificateValidator.ValidatePfx(
                reloaded,
                _pfxPassword,
                _requiredDomains,
                _renewalThreshold);
            if (!roundTrip.Usable || roundTrip.Material is null)
            {
                throw new AcmeCertificateException("pfx_roundtrip_failed", roundTrip.Reason);
            }

            AtomicFile.WriteBytes(AcmePaths.GenerationCompleteMarker(_stateDir, generationId), "ok\n"u8);
            AtomicFile.WriteText(AcmePaths.CurrentGenerationPointerPath(_stateDir), generationId + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AcmeCertificateException)
        {
            TryDeleteDirectory(genRoot);
            if (ex is AcmeCertificateException)
            {
                throw;
            }

            throw new AcmeStorageException("certificate_persist_failed", ex.GetType().Name);
        }

        CleanupNonCurrentGenerations(keep: generationId);
        return AcmePaths.GenerationCertificatePaths(_stateDir, generationId);
    }

    /// <summary>Idempotent recovery for interrupted promotions.</summary>
    public void Recover()
    {
        AcmePaths.EnsureStateLayout(_stateDir);
        var pointer = AcmePaths.CurrentGenerationPointerPath(_stateDir);
        var currentId = ReadCurrentId();
        if (File.Exists(pointer) && currentId is null)
        {
            AtomicFile.TryDelete(pointer);
        }

        if (currentId is not null && GenerationIsComplete(currentId))
        {
            CleanupNonCurrentGenerations(keep: currentId);
            return;
        }

        if (currentId is not null)
        {
            AtomicFile.TryDelete(pointer);
        }

        CleanupNonCurrentGenerations(keep: null);
    }

    private string? ReadCurrentId()
    {
        var path = AcmePaths.CurrentGenerationPointerPath(_stateDir);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var raw = File.ReadAllText(path).Trim();
            return GenerationIdRegex.IsMatch(raw) ? raw : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private bool GenerationIsComplete(string generationId)
    {
        if (!GenerationIdRegex.IsMatch(generationId))
        {
            return false;
        }

        var marker = AcmePaths.GenerationCompleteMarker(_stateDir, generationId);
        var paths = AcmePaths.GenerationCertificatePaths(_stateDir, generationId);
        if (!File.Exists(marker) || !File.Exists(paths.PfxPath))
        {
            return false;
        }

        try
        {
            return new FileInfo(paths.PfxPath).Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void CleanupNonCurrentGenerations(string? keep)
    {
        var root = AcmePaths.GenerationsDir(_stateDir);
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(entry);
            if (keep is not null && string.Equals(name, keep, StringComparison.Ordinal))
            {
                continue;
            }

            TryDeleteDirectory(entry);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort.
        }
    }
}
