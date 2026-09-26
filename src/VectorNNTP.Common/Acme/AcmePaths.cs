namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// Filesystem layout under <c>AcmeStateDir</c> (generation semantics match pyNNTPD).
/// </summary>
/// <remarks>
/// Layout:
/// <code>
/// {stateDir}/
///   account/
///     private_key.der              # PKCS#8 DER ACME account private key (not the TLS credential)
///     registration.json            # account_uri, directory_url, registration_body
///     private_key.der.pending      # staged key before CA registration completes
///     registration.pending.json
///   live/
///     current                      # active generation id (32 hex chars)
///     gens/{id}/
///       certificate.pfx            # PKCS#12: leaf + private key + chain
///       complete                   # marker written after PFX round-trip validation
///   dns01/journal/{entryId}.json
/// </code>
/// </remarks>
public static class AcmePaths
{
    private const string Live = "live";
    private const string Gens = "gens";
    private const string Current = "current";
    private const string Complete = "complete";
    private const string CertificatePfx = "certificate.pfx";
    private const string AccountPrivateKey = "private_key.der";

    /// <summary>Ensures account, live, generation, and DNS-01 journal directories exist.</summary>
    public static void EnsureStateLayout(string stateDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(AccountDir(stateDir));
        Directory.CreateDirectory(LiveDir(stateDir));
        Directory.CreateDirectory(GenerationsDir(stateDir));
        Directory.CreateDirectory(Dns01JournalDir(stateDir));
    }

    /// <summary>Account directory path.</summary>
    public static string AccountDir(string stateDir) => Path.Combine(stateDir, "account");

    /// <summary>Account PKCS#8 DER private key path.</summary>
    public static string AccountKeyPath(string stateDir) => Path.Combine(AccountDir(stateDir), AccountPrivateKey);

    /// <summary>Account registration metadata JSON path.</summary>
    public static string AccountMetaPath(string stateDir) => Path.Combine(AccountDir(stateDir), "registration.json");

    /// <summary>Pending account key path (crash recovery).</summary>
    public static string AccountPendingKeyPath(string stateDir) =>
        Path.Combine(AccountDir(stateDir), AccountPrivateKey + ".pending");

    /// <summary>Pending registration metadata path.</summary>
    public static string AccountPendingMetaPath(string stateDir) =>
        Path.Combine(AccountDir(stateDir), "registration.pending.json");

    /// <summary>Live certificate root.</summary>
    public static string LiveDir(string stateDir) => Path.Combine(stateDir, Live);

    /// <summary>Certificate generations directory.</summary>
    public static string GenerationsDir(string stateDir) => Path.Combine(LiveDir(stateDir), Gens);

    /// <summary>Current generation pointer file.</summary>
    public static string CurrentGenerationPointerPath(string stateDir) =>
        Path.Combine(LiveDir(stateDir), Current);

    /// <summary>Directory for one generation id.</summary>
    public static string GenerationDir(string stateDir, string generationId) =>
        Path.Combine(GenerationsDir(stateDir), generationId);

    /// <summary>Paths for a generation's PKCS#12/PFX material.</summary>
    public static CertificatePaths GenerationCertificatePaths(string stateDir, string generationId)
    {
        var root = GenerationDir(stateDir, generationId);
        return new CertificatePaths(Path.Combine(root, CertificatePfx));
    }

    /// <summary>Completion marker for a generation.</summary>
    public static string GenerationCompleteMarker(string stateDir, string generationId) =>
        Path.Combine(GenerationDir(stateDir, generationId), Complete);

    /// <summary>DNS-01 durable journal directory.</summary>
    public static string Dns01JournalDir(string stateDir) => Path.Combine(stateDir, "dns01", "journal");

    /// <summary>Issuance advisory lock file path.</summary>
    public static string IssuanceLockPath(string stateDir) => Path.Combine(stateDir, ".issuance.lock");
}
