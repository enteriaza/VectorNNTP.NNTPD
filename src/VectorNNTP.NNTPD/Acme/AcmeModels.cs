namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// Persisted ACME account identity (account private key + registration metadata).
/// This is not an X.509 certificate; the credential is the account keypair.
/// </summary>
/// <param name="AccountUri">ACME account resource URI.</param>
/// <param name="DirectoryUrl">Directory URL used when the account was registered.</param>
/// <param name="PrivateKeyDer">PKCS#8 DER encoding of the account private key.</param>
/// <param name="RegistrationBody">Optional opaque registration body retained for diagnostics.</param>
public sealed record AcmeAccountState(
    string AccountUri,
    string DirectoryUrl,
    byte[] PrivateKeyDer,
    string RegistrationBody = "");

/// <summary>Filesystem location for the active server certificate PKCS#12/PFX.</summary>
/// <param name="PfxPath">Path to <c>certificate.pfx</c>.</param>
public sealed record CertificatePaths(string PfxPath);

/// <summary>Validated PKCS#12/PFX server certificate material (leaf + key + chain).</summary>
/// <param name="PfxBytes">Binary PKCS#12/PFX bytes (password-protected).</param>
/// <param name="Domains">DNS SANs present on the leaf.</param>
/// <param name="NotBefore">Leaf not-before (UTC).</param>
/// <param name="NotAfter">Leaf not-after (UTC).</param>
public sealed record CertificateMaterial(
    byte[] PfxBytes,
    IReadOnlyList<string> Domains,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter);

/// <summary>Usability assessment for on-disk certificate material.</summary>
/// <param name="Usable">Whether the material can be used for TLS.</param>
/// <param name="DueForRenewal">Whether renewal should be attempted.</param>
/// <param name="Material">Validated material when usable.</param>
/// <param name="Reason">Short reason code.</param>
public sealed record CertificateStatus(
    bool Usable,
    bool DueForRenewal,
    CertificateMaterial? Material,
    string Reason = "");
