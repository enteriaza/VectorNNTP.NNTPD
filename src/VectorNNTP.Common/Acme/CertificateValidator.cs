using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VectorNNTP.NNTPD.Acme;

/// <summary>Validates PKCS#12/PFX certificate material before reuse or promotion.</summary>
public static class CertificateValidator
{
    /// <summary>
    /// Validates a password-protected PFX and returns usability / renewal status.
    /// </summary>
    /// <remarks>Never includes the password in returned reasons.</remarks>
    public static CertificateStatus ValidatePfx(
        ReadOnlySpan<byte> pfxBytes,
        string password,
        IReadOnlyList<string> requiredDomains,
        TimeSpan renewalThreshold,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(requiredDomains);

        var current = now ?? DateTimeOffset.UtcNow;

        X509Certificate2 leaf;
        try
        {
            leaf = PfxCrypto.LoadCertificate(pfxBytes, password);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CertificateStatus(
                Usable: false,
                DueForRenewal: true,
                Material: null,
                Reason: $"parse_failed:{ex.GetType().Name}");
        }

        try
        {
            if (!leaf.HasPrivateKey)
            {
                return new CertificateStatus(false, true, null, "missing_private_key");
            }

            using var privateKey = leaf.GetRSAPrivateKey()
                ?? throw new AcmeCertificateException("key_mismatch", "unsupported_key_type");

            var domains = GetDnsNames(leaf);
            AssertRequiredDomains(domains, requiredDomains);
            AssertValidityWindow(leaf, current);
            AssertKeyMatch(leaf, privateKey);
            AssertServerAuth(leaf);

            var material = new CertificateMaterial(
                PfxBytes: pfxBytes.ToArray(),
                Domains: domains.OrderBy(static d => d, StringComparer.Ordinal).ToArray(),
                NotBefore: leaf.NotBefore.ToUniversalTime(),
                NotAfter: leaf.NotAfter.ToUniversalTime());

            var due = current >= material.NotAfter - renewalThreshold;
            return new CertificateStatus(
                Usable: true,
                DueForRenewal: due,
                Material: material,
                Reason: due ? "renewal_threshold" : "ok");
        }
        catch (AcmeCertificateException ex)
        {
            return new CertificateStatus(
                Usable: false,
                DueForRenewal: true,
                Material: null,
                Reason: ex.Category);
        }
        finally
        {
            leaf.Dispose();
        }
    }

    /// <summary>Validates and returns material, throwing on failure.</summary>
    public static CertificateMaterial RequireValidPfx(
        ReadOnlySpan<byte> pfxBytes,
        string password,
        IReadOnlyList<string> requiredDomains,
        TimeSpan renewalThreshold,
        DateTimeOffset? now = null)
    {
        var status = ValidatePfx(pfxBytes, password, requiredDomains, renewalThreshold, now);
        if (!status.Usable || status.Material is null)
        {
            throw new AcmeCertificateException("invalid_certificate", status.Reason);
        }

        return status.Material;
    }

    private static HashSet<string> GetDnsNames(X509Certificate2 cert)
    {
        var extension = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (extension is null)
        {
            throw new AcmeCertificateException("missing_san", "no_san_extension");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dnsName in extension.EnumerateDnsNames())
        {
            names.Add(dnsName.ToLowerInvariant());
        }

        if (names.Count == 0)
        {
            throw new AcmeCertificateException("missing_san", "empty_san");
        }

        return names;
    }

    private static void AssertRequiredDomains(HashSet<string> present, IReadOnlyList<string> required)
    {
        foreach (var name in required)
        {
            if (!present.Contains(name))
            {
                throw new AcmeCertificateException("missing_san", "required_identity_absent");
            }
        }
    }

    private static void AssertValidityWindow(X509Certificate2 cert, DateTimeOffset now)
    {
        var notBefore = cert.NotBefore.ToUniversalTime();
        var notAfter = cert.NotAfter.ToUniversalTime();

        if (now < notBefore)
        {
            throw new AcmeCertificateException("not_yet_valid", "not_before");
        }

        if (now >= notAfter)
        {
            throw new AcmeCertificateException("expired", "not_after");
        }
    }

    private static void AssertKeyMatch(X509Certificate2 cert, RSA privateKey)
    {
        using var certPublic = cert.GetRSAPublicKey()
            ?? throw new AcmeCertificateException("key_mismatch", "unsupported_key_type");

        var certSpki = certPublic.ExportSubjectPublicKeyInfo();
        var keySpki = privateKey.ExportSubjectPublicKeyInfo();
        if (!CryptographicOperations.FixedTimeEquals(certSpki, keySpki))
        {
            throw new AcmeCertificateException("key_mismatch", "public_key_differ");
        }
    }

    private static void AssertServerAuth(X509Certificate2 cert)
    {
        var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (eku is null)
        {
            return;
        }

        if (!eku.EnhancedKeyUsages.Cast<Oid>().Any(static oid => oid.Value == "1.3.6.1.5.5.7.3.1"))
        {
            throw new AcmeCertificateException("not_server_auth", "eku_missing_server_auth");
        }
    }
}
